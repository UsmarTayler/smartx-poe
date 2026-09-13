using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Attachments;

/// <summary>Why an upload did not produce a stored attachment.</summary>
internal enum UploadRejection
{
    None,
    TooLarge,
    UnsupportedType
}

/// <summary>The outcome of streaming one file.</summary>
internal sealed record UploadResult(AttachmentMetadata? Attachment, UploadRejection Rejection, string? Reason)
{
    public static UploadResult Stored(AttachmentMetadata attachment) => new(attachment, UploadRejection.None, null);

    public static UploadResult Rejected(UploadRejection rejection, string reason) => new(null, rejection, reason);
}

/// <summary>
/// Streams one uploaded file from the request, through inspection and encryption, onto disk.
/// </summary>
/// <remarks>
/// <b>The pipeline.</b> Each read from the request is at most one 64 KiB chunk, and that chunk goes through
/// every stage before the next is read:
/// <list type="number">
/// <item><description>the first chunk's leading bytes identify the file's real type;</description></item>
/// <item><description>the running byte count is checked against the size limit;</description></item>
/// <item><description>text files have the chunk checked for being text;</description></item>
/// <item><description>the chunk is added to a running SHA-256 of the plaintext;</description></item>
/// <item><description>the chunk is encrypted and written to a partial file.</description></item>
/// </list>
/// <para>
/// No stage buffers the file. The largest amount of the upload held at once is one read buffer plus the
/// encryptor's one chunk, so the memory an upload costs is the same for a 10 KB configuration file and a
/// 100 MB log. The plaintext is never written anywhere: only ciphertext reaches the partial file.
/// </para>
/// <para>
/// Any failure — a rejection, a client disconnect, a full disk — deletes the partial file before the method
/// returns or throws. A rejected or interrupted upload leaves nothing behind.
/// </para>
/// </remarks>
internal sealed class AttachmentUploader(
    AttachmentStorage storage,
    IAttachmentStore store,
    AttachmentKeyProvider keys,
    IOptions<AttachmentOptions> options,
    TimeProvider time)
{
    private readonly long _maxFileBytes = options.Value.MaxFileBytes;

    /// <param name="macAddress">The sensor, already confirmed to be registered.</param>
    /// <param name="requestedKind">The kind the uploader asked for, or null to infer it from the content.</param>
    /// <param name="fileName">The uploaded file name, as sent.</param>
    /// <param name="declaredContentType">The section's Content-Type, as sent. Checked, never trusted.</param>
    /// <param name="content">The file section's body, read forwards once.</param>
    public async Task<UploadResult> StoreAsync(
        string macAddress,
        AttachmentKind? requestedKind,
        string? fileName,
        string? declaredContentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        var displayName = FileNameSanitizer.Sanitize(fileName);

        // The extension check needs no bytes at all, so an obviously unacceptable file is refused before a
        // partial file is even created.
        if (!AttachmentPolicy.IsAllowedFileName(displayName))
        {
            var extension = Path.GetExtension(displayName);
            var subject = extension.Length > 0 ? $"{extension} files" : "Files with no extension";

            return UploadResult.Rejected(
                UploadRejection.UnsupportedType,
                $"{subject} are not permitted. Allowed: {string.Join(", ", AttachmentPolicy.AllowedExtensions)}.");
        }

        var id = Guid.NewGuid();
        var buffer = ArrayPool<byte>.Shared.Rent(EncryptedFileFormat.ChunkSize);
        TextValidator? textValidator = null;
        var committed = false;

        try
        {
            await using var file = storage.CreatePartial(id);
            await using var encrypting = new AesGcmEncryptingStream(file, keys.Key);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // Read enough to recognise a signature before anything is written. For a file smaller than the
            // signature, this simply reads all of it.
            var read = await content.ReadAtLeastAsync(
                buffer.AsMemory(0, EncryptedFileFormat.ChunkSize),
                ContentInspector.SignatureLength,
                throwOnEndOfStream: false,
                cancellationToken);

            var family = ContentInspector.Identify(buffer.AsSpan(0, Math.Min(read, ContentInspector.SignatureLength)));
            var contentType = ContentInspector.Resolve(family, displayName, declaredContentType, out var typeRejection);

            if (contentType is null)
            {
                return UploadResult.Rejected(UploadRejection.UnsupportedType, $"{displayName} was rejected: {typeRejection}.");
            }

            var isImage = family != ContentFamily.Text;

            if (requestedKind == AttachmentKind.DeploymentPhoto && !isImage)
            {
                return UploadResult.Rejected(UploadRejection.UnsupportedType, $"{displayName} was rejected: a deployment photo must be an image.");
            }

            textValidator = isImage ? null : new TextValidator();

            long total = 0;

            while (read > 0)
            {
                total += read;

                // Counted from the bytes that actually arrived, never from Content-Length. That header is
                // whatever the client chose to send, and a client that declares 1 KB and then sends 10 GB
                // would sail past a check made against it.
                if (total > _maxFileBytes)
                {
                    return UploadResult.Rejected(
                        UploadRejection.TooLarge,
                        $"{displayName} is larger than the {FormatBytes(_maxFileBytes)} limit.");
                }

                var chunk = buffer.AsMemory(0, read);

                if (textValidator is not null && !textValidator.Accept(chunk.Span, isFinal: false))
                {
                    return UploadResult.Rejected(
                        UploadRejection.UnsupportedType,
                        $"{displayName} was rejected: it is named as a text file but contains binary data.");
                }

                sha256.AppendData(chunk.Span);
                await encrypting.WriteAsync(chunk, cancellationToken);

                read = await content.ReadAsync(buffer.AsMemory(0, EncryptedFileFormat.ChunkSize), cancellationToken);
            }

            // An incomplete multi-byte character at the very end is only detectable once there is no more input.
            if (textValidator is not null && !textValidator.Accept([], isFinal: true))
            {
                return UploadResult.Rejected(
                    UploadRejection.UnsupportedType,
                    $"{displayName} was rejected: it ends partway through a character, so it is not valid UTF-8 text.");
            }

            await encrypting.CompleteAsync(cancellationToken);
            await file.FlushAsync(cancellationToken);
            await file.DisposeAsync();

            var attachment = new AttachmentMetadata(
                id,
                macAddress,
                displayName,
                contentType,
                total,
                Convert.ToHexStringLower(sha256.GetHashAndReset()),
                time.GetUtcNow(),
                requestedKind ?? InferKind(isImage, displayName));

            storage.Commit(id);
            await store.AddAsync(attachment, cancellationToken);
            committed = true;

            return UploadResult.Stored(attachment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
            textValidator?.Release();

            if (!committed)
            {
                // Runs after the using declarations above have closed the file, so the delete cannot fail
                // on a handle this method still holds.
                storage.Delete(id);
            }
        }
    }

    /// <summary>Removes a stored attachment's file and metadata.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await store.RemoveAsync(id, cancellationToken))
        {
            return false;
        }

        storage.Delete(id);

        return true;
    }

    /// <summary>An image is a deployment photo; a .log is a hardware log; anything else is configuration.</summary>
    private static AttachmentKind InferKind(bool isImage, string fileName) =>
        isImage ? AttachmentKind.DeploymentPhoto
        : string.Equals(Path.GetExtension(fileName), ".log", StringComparison.OrdinalIgnoreCase) ? AttachmentKind.HardwareLog
        : AttachmentKind.ConfigurationFile;

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes} bytes";
}
