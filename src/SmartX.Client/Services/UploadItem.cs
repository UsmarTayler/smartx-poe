using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>Where one queued file is in its upload.</summary>
public enum UploadStatus
{
    Queued,
    Uploading,
    Done,
    Failed,
    Cancelled
}

/// <summary>
/// One file in the upload queue, with its own progress and its own outcome.
/// </summary>
/// <remarks>
/// Every file carries its own status, so one failure is recorded against that file alone and the rest of
/// the queue carries on. A rejected 90 MB log does not discard the three photos queued behind it.
/// </remarks>
public sealed class UploadItem(string id, string fileName, long sizeBytes)
{
    /// <summary>The key the browser holds the file under.</summary>
    public string Id { get; } = id;

    public string FileName { get; } = fileName;

    public long SizeBytes { get; } = sizeBytes;

    public UploadStatus Status { get; internal set; } = UploadStatus.Queued;

    /// <summary>Bytes handed to the network, as a fraction of the request, between 0 and 1.</summary>
    public double Progress { get; internal set; }

    /// <summary>Why it failed, in words, when it did.</summary>
    public string? Error { get; internal set; }

    /// <summary>The stored attachment, once the upload succeeds.</summary>
    public AttachmentMetadata? Result { get; internal set; }

    public bool IsFinished => Status is UploadStatus.Done or UploadStatus.Failed or UploadStatus.Cancelled;
}

/// <summary>Everything the client holds for one sensor's attachments.</summary>
public sealed class SensorAttachments
{
    internal List<AttachmentMetadata> ItemList { get; } = [];

    internal List<UploadItem> UploadList { get; } = [];

    /// <summary>Stored attachments, newest first.</summary>
    public IReadOnlyList<AttachmentMetadata> Items => ItemList;

    /// <summary>Files queued, uploading, or recently finished.</summary>
    public IReadOnlyList<UploadItem> Uploads => UploadList;

    public bool IsLoaded { get; internal set; }

    public ApiProblem? LoadProblem { get; internal set; }
}
