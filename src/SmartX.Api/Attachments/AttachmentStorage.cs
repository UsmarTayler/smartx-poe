using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SmartX.Shared;

namespace SmartX.Api.Attachments;

/// <summary>
/// Where encrypted attachment files live on disk.
/// </summary>
/// <remarks>
/// Every path is built from a <see cref="Guid"/> and nothing else. The uploader's file name never reaches
/// this class, so there is no string it could contain that would change which directory a file lands in.
/// A GUID formatted as 32 hex digits cannot contain a separator, a dot-dot, a drive letter or a device name.
/// <para>
/// Files are written under a ".partial" name and renamed only once completely encrypted. A crash or an
/// aborted upload therefore never leaves a file under a final name, and nothing ever tries to read one.
/// </para>
/// </remarks>
internal sealed class AttachmentStorage
{
    private const string StoredExtension = ".smxa";
    private const string PartialExtension = ".partial";

    public AttachmentStorage(IOptions<AttachmentOptions> options, IHostEnvironment environment)
    {
        Root = Path.GetFullPath(options.Value.StoragePath, environment.ContentRootPath);
        Directory.CreateDirectory(Root);
    }

    /// <summary>The absolute storage directory.</summary>
    public string Root { get; }

    public string StoredPath(Guid id) => Path.Combine(Root, id.ToString("N") + StoredExtension);

    public string PartialPath(Guid id) => Path.Combine(Root, id.ToString("N") + PartialExtension);

    /// <summary>Opens a new partial file for writing. Fails if one somehow already exists.</summary>
    public FileStream CreatePartial(Guid id) => new(
        PartialPath(id),
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: EncryptedFileFormat.ChunkSize,
        FileOptions.Asynchronous);

    /// <summary>Promotes a completely written partial file to its stored name.</summary>
    public void Commit(Guid id) => File.Move(PartialPath(id), StoredPath(id));

    public FileStream OpenRead(Guid id) => new(
        StoredPath(id),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: EncryptedFileFormat.ChunkSize + EncryptedFileFormat.TagSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>Removes both the stored and any partial file for an id. Missing files are not an error.</summary>
    public void Delete(Guid id)
    {
        File.Delete(PartialPath(id));
        File.Delete(StoredPath(id));
    }
}

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

/// <summary>Attachment metadata held in memory for the life of the process.</summary>
/// <remarks>
/// Asynchronous for the same reason as the other stores: the database-backed replacement will be, and the
/// endpoints should not need rewriting when it arrives. Being in memory is also why encrypted files on disk
/// become unreachable after a restart — the index that names them is gone. A durable store closes that gap.
/// </remarks>
internal sealed class InMemoryAttachmentStore : IAttachmentStore
{
    private readonly ConcurrentDictionary<Guid, AttachmentMetadata> _attachments = new();

    public Task AddAsync(AttachmentMetadata attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        if (!_attachments.TryAdd(attachment.Id, attachment))
        {
            throw new InvalidOperationException($"An attachment with id {attachment.Id} already exists.");
        }

        return Task.CompletedTask;
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

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_attachments.TryRemove(id, out _));
}
