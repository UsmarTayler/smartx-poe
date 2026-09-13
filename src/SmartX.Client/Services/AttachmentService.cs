using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// Lists, uploads, downloads and deletes sensor attachments.
/// </summary>
/// <remarks>
/// Scoped, and it owns the upload queue rather than the page doing so. An upload keeps running in the
/// browser if the user navigates away, and because its progress is reported to this service rather than to
/// a page component that has since been destroyed, coming back to the sensor shows the upload still going,
/// or finished — not a blank queue.
/// <para>
/// <b>Client checks are for speed, never for security.</b> Before a file is sent, its extension and size are
/// checked against the limits the API publishes, so an obviously unacceptable file fails instantly instead
/// of after a long upload. The server repeats both checks and then inspects the bytes; nothing here is
/// trusted by it.
/// </para>
/// </remarks>
public sealed class AttachmentService(SmartXApiClient api, ApiConnection connection, IJSRuntime js) : IAsyncDisposable
{
    // Two at a time: parallel enough that one large log does not hold up a batch of photos, restrained enough
    // not to saturate a gateway's uplink or its request threads.
    private const int MaxConcurrentUploads = 2;

    private readonly Dictionary<string, SensorAttachments> _bySensor = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UploadItem> _uploadsById = new(StringComparer.Ordinal);
    private readonly Queue<(string Mac, UploadItem Item, AttachmentKind? Kind)> _waiting = new();

    private IJSObjectReference? _module;
    private DotNetObjectReference<AttachmentService>? _self;
    private int _activeUploads;

    /// <summary>Raised whenever attachments, uploads or progress change.</summary>
    public event Action? Changed;

    /// <summary>What the server accepts, once loaded.</summary>
    public AttachmentLimits? Limits { get; private set; }

    /// <summary>One sensor's attachment state, created empty on first request.</summary>
    public SensorAttachments For(string macAddress)
    {
        if (!_bySensor.TryGetValue(macAddress, out var state))
        {
            state = new SensorAttachments();
            _bySensor[macAddress] = state;
        }

        return state;
    }

    /// <summary>Loads the server's limits, once, and a sensor's attachments.</summary>
    public async Task LoadAsync(string macAddress, CancellationToken cancellationToken = default)
    {
        var state = For(macAddress);

        var limitsCall = Limits is null ? api.GetAttachmentLimitsAsync(cancellationToken) : null;
        var listCall = api.GetAttachmentsAsync(macAddress, cancellationToken);

        if (limitsCall is not null && await limitsCall is { IsSuccess: true } limits)
        {
            Limits = limits.Value;
        }

        var list = await listCall;
        connection.Report(list.Problem is not { IsUnreachable: true });

        if (list.IsSuccess)
        {
            state.ItemList.Clear();
            state.ItemList.AddRange(list.Value);
            state.LoadProblem = null;
            state.IsLoaded = true;
        }
        else
        {
            state.LoadProblem = list.Problem;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Queues every file currently selected in the file input inside <paramref name="container"/>.
    /// </summary>
    /// <remarks>
    /// Must be called while the input still holds the selection — from the change event itself. Each file is
    /// checked here first; one that fails is added to the queue already failed, with its reason, and the
    /// others go ahead.
    /// </remarks>
    public async Task QueueSelectedFilesAsync(string macAddress, ElementReference container, AttachmentKind? kind)
    {
        var module = await ModuleAsync();
        var captured = await module.InvokeAsync<CapturedFile[]>("capture", container);
        var state = For(macAddress);

        foreach (var file in captured)
        {
            var item = new UploadItem(file.Id, file.Name, file.Size);
            state.UploadList.Insert(0, item);

            if (CheckBeforeUpload(file) is { } problem)
            {
                item.Status = UploadStatus.Failed;
                item.Error = problem;
                await module.InvokeVoidAsync("release", file.Id);

                continue;
            }

            _uploadsById[item.Id] = item;
            _waiting.Enqueue((macAddress, item, kind));
        }

        Changed?.Invoke();
        StartWaitingUploads();
    }

    /// <summary>Stops a queued or running upload.</summary>
    public async Task CancelAsync(UploadItem item)
    {
        if (item.Status == UploadStatus.Queued)
        {
            // Not yet started, so there is no request to abort; it is skipped when its turn comes.
            item.Status = UploadStatus.Cancelled;
            await (await ModuleAsync()).InvokeVoidAsync("release", item.Id);
            Changed?.Invoke();

            return;
        }

        if (item.Status == UploadStatus.Uploading)
        {
            await (await ModuleAsync()).InvokeVoidAsync("cancel", item.Id);
        }
    }

    /// <summary>Removes a finished upload from the queue display.</summary>
    public void Dismiss(string macAddress, UploadItem item)
    {
        if (item.IsFinished && For(macAddress).UploadList.Remove(item))
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Deletes an attachment and removes it from the list.</summary>
    /// <returns>Null on success, otherwise why it failed.</returns>
    public async Task<ApiProblem?> DeleteAsync(string macAddress, AttachmentMetadata attachment)
    {
        var problem = await api.DeleteAttachmentAsync(attachment.Id);
        connection.Report(problem is not { IsUnreachable: true });

        // A 404 means it is already gone, which is the outcome the user asked for.
        if (problem is null || problem.Status == 404)
        {
            For(macAddress).ItemList.RemoveAll(item => item.Id == attachment.Id);
            Changed?.Invoke();

            return null;
        }

        return problem;
    }

    public string DownloadUrl(Guid id) => api.AbsoluteUrl($"api/attachments/{id}");

    public string ThumbnailUrl(Guid id) => api.AbsoluteUrl($"api/attachments/{id}/thumbnail");

    /// <summary>Receives upload progress from the browser.</summary>
    [JSInvokable]
    public void ReportProgress(string uploadId, double loaded, double total)
    {
        if (total > 0 && _uploadsById.TryGetValue(uploadId, out var item) && item.Status == UploadStatus.Uploading)
        {
            item.Progress = Math.Clamp(loaded / total, 0, 1);
            Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is being torn down; there is nothing left to release.
            }
        }

        _self?.Dispose();
    }

    private string? CheckBeforeUpload(CapturedFile file)
    {
        if (!AttachmentPolicy.IsAllowedFileName(file.Name))
        {
            var extension = Path.GetExtension(file.Name);
            var subject = extension.Length > 0 ? $"{extension} files" : "Files without an extension";

            return $"{subject} can't be attached. Allowed: {string.Join(", ", AttachmentPolicy.AllowedExtensions)}.";
        }

        if (Limits is { } limits && file.Size > limits.MaxFileBytes)
        {
            return $"{FormatBytes(file.Size)} is over the {FormatBytes(limits.MaxFileBytes)} limit.";
        }

        return null;
    }

    /// <summary>Starts waiting uploads until the concurrency limit is reached.</summary>
    /// <remarks>
    /// Each upload runs as a Task that is deliberately not awaited here — never an async void — and it
    /// catches its own failures, so nothing it does can escape as an unobserved exception.
    /// </remarks>
    private void StartWaitingUploads()
    {
        while (_activeUploads < MaxConcurrentUploads && _waiting.TryDequeue(out var next))
        {
            if (next.Item.Status != UploadStatus.Queued)
            {
                continue;
            }

            _activeUploads++;
            _ = RunUploadAsync(next.Mac, next.Item, next.Kind);
        }
    }

    private async Task RunUploadAsync(string macAddress, UploadItem item, AttachmentKind? kind)
    {
        try
        {
            item.Status = UploadStatus.Uploading;
            Changed?.Invoke();

            var module = await ModuleAsync();
            var query = kind is null ? string.Empty : $"?kind={kind}";
            var url = api.AbsoluteUrl($"api/sensors/{Uri.EscapeDataString(macAddress)}/attachments{query}");

            var response = await module.InvokeAsync<UploadResponse>("upload", item.Id, url, _self);

            connection.Report(response.Status != 0);

            if (response.Status == 201 && TryReadAttachment(response.Body) is { } attachment)
            {
                item.Status = UploadStatus.Done;
                item.Progress = 1;
                item.Result = attachment;
                For(macAddress).ItemList.Insert(0, attachment);
            }
            else if (response.Status == -1)
            {
                item.Status = UploadStatus.Cancelled;
            }
            else
            {
                var problem = ApiProblem.FromResponse(response.Status, response.Body);
                item.Status = UploadStatus.Failed;
                item.Error = problem.Message;
            }
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or TaskCanceledException)
        {
            item.Status = UploadStatus.Failed;
            item.Error = "The upload could not be completed in this browser.";
        }
        finally
        {
            _uploadsById.Remove(item.Id);
            _activeUploads--;
            Changed?.Invoke();
            StartWaitingUploads();
        }
    }

    private async ValueTask<IJSObjectReference> ModuleAsync()
    {
        _self ??= DotNetObjectReference.Create(this);

        return _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/attachmentUpload.js");
    }

    private static AttachmentMetadata? TryReadAttachment(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<AttachmentMetadata>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A size the way a person reads it: "812 KB", "4.2 MB".</summary>
    public static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 ? (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB"
        : bytes + " bytes";

    private sealed record CapturedFile(string Id, string Name, long Size, string Type);

    private sealed record UploadResponse(int Status, string Body);
}
