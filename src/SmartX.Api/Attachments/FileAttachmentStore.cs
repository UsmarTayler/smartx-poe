using System.Collections.Concurrent;
using System.Text.Json;
using SmartX.Shared;

namespace SmartX.Api.Attachments;

/// <summary>Holds attachment metadata.</summary>
public interface IAttachmentStore
{
    Task AddAsync(AttachmentMetadata attachment, CancellationToken cancellationToken = default);

    Task<AttachmentMetadata?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>A sensor's attachments, newest first.</summary>
    Task<IReadOnlyList<AttachmentMetadata>> ListForAsync(string macAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes an attachment's metadata.</summary>
    /// <returns>False when there was nothing to remove.</returns>
    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Attachment metadata kept as one small JSON index entry beside each encrypted file, and cached in memory.
/// </summary>
/// <remarks>
/// <b>Why on disk at all.</b> The encrypted files already survive a restart; an index held only in memory
/// did not, so after a restart every file was still there and none could be found. Writing the entry next to
/// its file, under the same GUID, means the directory is the whole store: a Docker volume or a copied folder
/// carries attachments with their names intact, and nothing needs a database.
/// <para>
/// <b>Reads never touch the disk.</b> Entries are loaded once at startup and served from a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>, so listing a sensor's attachments costs the same as it
/// did in memory. The disk is written only when an attachment is added or removed.
/// </para>
/// <para>
/// <b>Entries under a different key are skipped.</b> Each entry records the <see cref="AttachmentKeyProvider.KeyId"/>
/// its file was encrypted with. If the key has changed — the temporary development key after a restart, or a
/// deployment given a new one — those files cannot be decrypted, and listing them would only offer downloads
/// that fail. They are left on disk untouched, not deleted, because restoring the old key makes them readable
/// again.
/// </para>
/// <para>
/// The index is not encrypted. It holds the original file name, size and SHA-256 of each attachment, which is
/// exactly what the API returns to any client listing them; the file contents are what the key protects.
/// </para>
/// </remarks>
internal sealed class FileAttachmentStore : IAttachmentStore
{
    private readonly ConcurrentDictionary<Guid, AttachmentMetadata> _attachments = new();
    private readonly AttachmentStorage _storage;
    private readonly string _keyId;

    public FileAttachmentStore(AttachmentStorage storage, AttachmentKeyProvider keys, ILogger<FileAttachmentStore> logger)
    {
        _storage = storage;
        _keyId = keys.KeyId;

        var otherKey = 0;

        foreach (var path in Directory.EnumerateFiles(storage.Root, "*" + AttachmentStorage.MetadataExtension))
        {
            try
            {
                // Small, and read once at startup, so reading the whole entry into memory is fine here — the
                // no-whole-file-in-a-byte-array rule is about attachment content, which this never touches.
                var entry = JsonSerializer.Deserialize<IndexEntry>(File.ReadAllText(path), JsonSerializerOptions.Web);

                if (entry?.Attachment is not { } attachment || !File.Exists(storage.StoredPath(attachment.Id)))
                {
                    logger.LogWarning("Skipped attachment index entry {Path}: it is unreadable or its file is missing.", path);
                }
                else if (entry.KeyId != _keyId)
                {
                    otherKey++;
                }
                else
                {
                    _attachments.TryAdd(attachment.Id, attachment);
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                logger.LogWarning(exception, "Skipped attachment index entry {Path}: it could not be read.", path);
            }
        }

        if (otherKey > 0)
        {
            logger.LogWarning(
                "{Count} stored attachments were encrypted with a different key and are hidden. Restore that key " +
                "to make them readable again.",
                otherKey);
        }
    }

    public async Task AddAsync(AttachmentMetadata attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (_attachments.ContainsKey(attachment.Id))
        {
            throw new InvalidOperationException($"An attachment with id {attachment.Id} already exists.");
        }

        // Written to a partial name and moved into place, like the content, so a crash mid-write never leaves
        // a truncated entry that would fail to load.
        var partial = _storage.PartialMetadataPath(attachment.Id);

        await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(file, new IndexEntry(attachment, _keyId), JsonSerializerOptions.Web, cancellationToken);
        }

        File.Move(partial, _storage.MetadataPath(attachment.Id));
        _attachments.TryAdd(attachment.Id, attachment);
    }

    public Task<AttachmentMetadata?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_attachments.TryGetValue(id, out var attachment) ? attachment : null);

    public Task<IReadOnlyList<AttachmentMetadata>> ListForAsync(string macAddress, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AttachmentMetadata> attachments =
        [
            .. _attachments.Values
                .Where(attachment => string.Equals(attachment.MacAddress, macAddress, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(attachment => attachment.UploadedAt)
        ];

        return Task.FromResult(attachments);
    }

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!_attachments.TryRemove(id, out _))
        {
            return Task.FromResult(false);
        }

        File.Delete(_storage.MetadataPath(id));

        return Task.FromResult(true);
    }

    /// <summary>What is written for each attachment: its metadata and the key its file needs.</summary>
    private sealed record IndexEntry(AttachmentMetadata Attachment, string KeyId);
}
