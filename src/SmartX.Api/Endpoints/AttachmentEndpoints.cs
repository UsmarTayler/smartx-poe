using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using SmartX.Api.Attachments;
using SmartX.Api.Storage;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>
/// Attaching files to sensors: streamed, encrypted upload, and decrypting download.
/// </summary>
/// <remarks>
/// <b>Why <see cref="MultipartReader"/> and not <see cref="IFormFile"/>.</b> Binding an
/// <see cref="IFormFile"/> parameter — or touching <c>Request.Form</c> at all — makes ASP.NET Core read and
/// buffer the <i>entire</i> multipart body before the handler runs: into memory for small files, and into a
/// temporary file on disk once a file passes 64 KB. The handler only receives a file after all of it has
/// arrived. Three people uploading 100 MB logs at once is then 300 MB of gateway memory or temp storage
/// before a single byte has been checked, and the plaintext has been written to a temp file unencrypted —
/// which alone would break encryption at rest.
/// <para>
/// <see cref="MultipartReader"/> instead walks the raw request body one section at a time, and each file
/// section's body is a stream read directly off the connection. The upload handler therefore declares no
/// form parameters, so Minimal APIs has no form to bind and never buffers one, and it reads the request
/// through <see cref="HttpRequest.Body"/> only. Bytes go from the network, through inspection and
/// encryption, to disk, one chunk at a time. Memory per upload is constant, and nothing is written in the
/// clear.
/// </para>
/// <para>
/// One file per request, deliberately. Each file then gets its own status code — a 413 or a 415 means
/// exactly one file — and the client can show independent progress and failure for each file in a queue.
/// </para>
/// </remarks>
internal static class AttachmentEndpoints
{
    // Headroom above the file limit for the multipart framing around it: boundaries and section headers.
    private const long MultipartOverheadBytes = 64 * 1024;

    // RFC 2046 caps a multipart boundary at 70 characters. Anything longer is malformed or hostile.
    private const int MaxBoundaryLength = 70;

    internal static RouteGroupBuilder MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/sensors/{mac}/attachments", UploadAsync).WithName("UploadAttachment");
        api.MapGet("/sensors/{mac}/attachments", ListAsync).WithName("ListAttachments");
        api.MapGet("/attachments/limits", GetLimits).WithName("GetAttachmentLimits");
        api.MapGet("/attachments/{id:guid}", DownloadAsync).WithName("DownloadAttachment");
        api.MapGet("/attachments/{id:guid}/thumbnail", ThumbnailAsync).WithName("GetAttachmentThumbnail");
        api.MapDelete("/attachments/{id:guid}", DeleteAsync).WithName("DeleteAttachment");

        return api;
    }

    /// <summary>Streams one file into encrypted storage against a registered sensor.</summary>
    /// <remarks>
    /// Checks are ordered from cheapest to most expensive, so a request that is going to fail does so before
    /// its body is read: the sensor must exist, the kind must be valid, the body must be multipart. Only then
    /// are any file bytes consumed.
    /// </remarks>
    private static async Task<IResult> UploadAsync(
        string mac,
        HttpContext context,
        ISensorRegistry registry,
        AttachmentUploader uploader,
        IOptions<AttachmentOptions> options,
        CancellationToken cancellationToken)
    {
        var sensor = await registry.FindAsync(mac, cancellationToken);

        if (sensor is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Sensor not registered",
                $"No sensor is registered with MAC address '{mac}', so nothing can be attached to it.");
        }

        AttachmentKind? kind = null;

        if (context.Request.Query["kind"] is { Count: > 0 } kindValue)
        {
            if (!Enum.TryParse<AttachmentKind>(kindValue, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Problem(StatusCodes.Status400BadRequest, "Unknown attachment kind",
                    $"'{kindValue}' is not an attachment kind. Use one of: {string.Join(", ", Enum.GetNames<AttachmentKind>())}.");
            }

            kind = parsed;
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, "Expected a multipart upload",
                "Send the file as multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;

        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaxBoundaryLength)
        {
            return Problem(StatusCodes.Status400BadRequest, "Malformed multipart request",
                "The multipart boundary is missing or longer than 70 characters.");
        }

        // Kestrel's default request body limit is about 28.6 MB, which would refuse a legitimate 100 MB log.
        // It is raised for this one endpoint to the file limit plus framing, so Kestrel still stops a body
        // far beyond what could ever be accepted. The per-file limit itself is enforced while streaming.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = options.Value.MaxFileBytes + MultipartOverheadBytes;
        }

        var reader = new MultipartReader(boundary, context.Request.Body);
        UploadResult? stored = null;

        try
        {
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !IsFileSection(disposition))
                {
                    // Plain form fields are skipped. The reader discards an unread section when it moves on.
                    continue;
                }

                if (stored?.Attachment is { } first)
                {
                    await uploader.DeleteAsync(first.Id, CancellationToken.None);

                    return Problem(StatusCodes.Status400BadRequest, "One file per request",
                        "Upload each file in its own request, so each gets its own result.");
                }

                var fileName = disposition.FileNameStar.HasValue
                    ? disposition.FileNameStar.Value
                    : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;

                var result = await uploader.StoreAsync(sensor.MacAddress, kind, fileName, section.ContentType, section.Body, cancellationToken);

                if (result.Rejection != UploadRejection.None)
                {
                    // Returned without reading the rest of the body. For an oversized file that is the point:
                    // the remaining bytes are never processed, and Kestrel closes the connection rather than
                    // draining an arbitrarily large request.
                    return result.Rejection == UploadRejection.TooLarge
                        ? Problem(StatusCodes.Status413PayloadTooLarge, "File too large", result.Reason)
                        : Problem(StatusCodes.Status415UnsupportedMediaType, "File type not permitted", result.Reason);
                }

                stored = result;
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel's own body limit tripped while reading — the request was larger than any permitted file.
            return Problem(StatusCodes.Status413PayloadTooLarge, "File too large",
                $"The request is larger than the {options.Value.MaxFileBytes / (1024 * 1024)} MB limit.");
        }
        catch (InvalidDataException exception)
        {
            // Raised by MultipartReader for a body that does not follow multipart framing.
            return Problem(StatusCodes.Status400BadRequest, "Malformed multipart request", exception.Message);
        }

        if (stored?.Attachment is not { } attachment)
        {
            return Problem(StatusCodes.Status400BadRequest, "No file in the request",
                "The multipart body contained no file section.");
        }

        return Results.Created($"/api/attachments/{attachment.Id}", attachment);
    }

    /// <summary>A sensor's attachments, newest first.</summary>
    private static async Task<IResult> ListAsync(
        string mac,
        ISensorRegistry registry,
        IAttachmentStore store,
        CancellationToken cancellationToken)
    {
        var sensor = await registry.FindAsync(mac, cancellationToken);

        return sensor is null
            ? Problem(StatusCodes.Status404NotFound, "Sensor not registered", $"No sensor is registered with MAC address '{mac}'.")
            : Results.Ok(await store.ListForAsync(sensor.MacAddress, cancellationToken));
    }

    /// <summary>What the server accepts, so the client can check a file before sending it.</summary>
    private static IResult GetLimits(IOptions<AttachmentOptions> options) =>
        Results.Ok(new AttachmentLimits(options.Value.MaxFileBytes, AttachmentPolicy.AllowedExtensions));

    /// <summary>Decrypts an attachment on the way out, as a download.</summary>
    private static Task<IResult> DownloadAsync(
        Guid id,
        HttpContext context,
        IAttachmentStore store,
        AttachmentStorage storage,
        AttachmentKeyProvider keys,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        StreamDecryptedAsync(id, context, store, storage, keys, loggers, asThumbnail: false, cancellationToken);

    /// <summary>An image attachment, for display inline.</summary>
    /// <remarks>
    /// Serves the stored image itself rather than a resized copy; the client scales it down. A true
    /// thumbnail needs an image-decoding library and a full decode of the picture into memory, which is
    /// exactly the cost the streaming design exists to avoid — see the README's trade-offs.
    /// </remarks>
    private static Task<IResult> ThumbnailAsync(
        Guid id,
        HttpContext context,
        IAttachmentStore store,
        AttachmentStorage storage,
        AttachmentKeyProvider keys,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        StreamDecryptedAsync(id, context, store, storage, keys, loggers, asThumbnail: true, cancellationToken);

    private static async Task<IResult> DeleteAsync(Guid id, AttachmentUploader uploader, CancellationToken cancellationToken) =>
        await uploader.DeleteAsync(id, cancellationToken)
            ? Results.NoContent()
            : Problem(StatusCodes.Status404NotFound, "Attachment not found", $"No attachment has id {id}.");

    /// <summary>Decrypts a stored file straight into the response.</summary>
    /// <remarks>
    /// <b>How a tampered file fails loudly.</b> The first chunk is decrypted, and so authenticated, before any
    /// response header is sent. If it fails, the client gets a clear 500 problem response instead of a
    /// download. Most attachments fit in one chunk, so for them this catches any tampering at all. For a
    /// larger file, a later chunk can only fail after the 200 has gone out; then the connection is aborted.
    /// Content-Length has already promised the full size, so the client sees a failed, incomplete transfer —
    /// never a complete-looking file with corrupt bytes in it, because a chunk's plaintext is only released
    /// after its tag verifies.
    /// </remarks>
    private static async Task<IResult> StreamDecryptedAsync(
        Guid id,
        HttpContext context,
        IAttachmentStore store,
        AttachmentStorage storage,
        AttachmentKeyProvider keys,
        ILoggerFactory loggers,
        bool asThumbnail,
        CancellationToken cancellationToken)
    {
        var attachment = await store.FindAsync(id, cancellationToken);

        if (attachment is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Attachment not found", $"No attachment has id {id}.");
        }

        if (asThumbnail && !attachment.IsImage)
        {
            return Problem(StatusCodes.Status404NotFound, "No thumbnail",
                $"{attachment.OriginalFileName} is not an image, so it has no thumbnail.");
        }

        var logger = loggers.CreateLogger("SmartX.Api.Attachments.Download");
        var buffer = new byte[EncryptedFileFormat.ChunkSize];

        await using var file = storage.OpenRead(id);
        await using var decrypting = await OpenOrNullAsync(file, keys, logger, attachment, cancellationToken);

        if (decrypting is null)
        {
            return IntegrityFailure(attachment);
        }

        int read;

        try
        {
            read = await decrypting.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        }
        catch (AttachmentIntegrityException exception)
        {
            logger.LogError(exception, "Attachment {Id} failed its integrity check before download began.", id);

            return IntegrityFailure(attachment);
        }

        var response = context.Response;
        response.ContentType = attachment.ContentType.StartsWith("text/", StringComparison.Ordinal)
            ? attachment.ContentType + "; charset=utf-8"
            : attachment.ContentType;
        response.ContentLength = attachment.SizeBytes;

        // The stored content type came from the file's own bytes, and nosniff stops the browser second-guessing
        // it. A download is always sent as an attachment, so even a text file is saved rather than rendered,
        // and the sandbox policy denies scripts to anything that is opened regardless.
        var disposition = new ContentDispositionHeaderValue(asThumbnail ? "inline" : "attachment");
        disposition.SetHttpFileName(attachment.OriginalFileName);
        response.Headers.ContentDisposition = disposition.ToString();
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ContentSecurityPolicy = "sandbox";
        response.Headers.CacheControl = asThumbnail ? "private, max-age=300" : "private, no-store";

        try
        {
            while (read > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                read = await decrypting.ReadAsync(buffer, cancellationToken);
            }
        }
        catch (AttachmentIntegrityException exception)
        {
            logger.LogError(exception, "Attachment {Id} failed its integrity check partway through download; aborting.", id);
            context.Abort();
        }

        return Results.Empty;
    }

    private static async Task<AesGcmDecryptingStream?> OpenOrNullAsync(
        FileStream file,
        AttachmentKeyProvider keys,
        ILogger logger,
        AttachmentMetadata attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AesGcmDecryptingStream.OpenAsync(file, keys.KeyMemory, cancellationToken);
        }
        catch (AttachmentIntegrityException exception)
        {
            logger.LogError(exception, "Attachment {Id} has an invalid encrypted header.", attachment.Id);

            return null;
        }
    }

    private static IResult IntegrityFailure(AttachmentMetadata attachment) =>
        Problem(StatusCodes.Status500InternalServerError, "Attachment failed its integrity check",
            $"{attachment.OriginalFileName} could not be authenticated. The stored file has been altered or corrupted, " +
            "so it is not being served.");

    private static bool IsFileSection(ContentDispositionHeaderValue disposition) =>
        disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase)
        && (disposition.FileName.HasValue || disposition.FileNameStar.HasValue);

    private static IResult Problem(int status, string title, string? detail) =>
        Results.Problem(statusCode: status, title: title, detail: detail);
}
