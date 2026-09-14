using Microsoft.Extensions.Options;

namespace SmartX.Api.Attachments;

/// <summary>
/// Where encrypted attachment files, and the index entries that describe them, live on disk.
/// </summary>
/// <remarks>
/// Every path is built from a <see cref="Guid"/> and nothing else. The uploader's file name never reaches
/// this class, so there is no string it could contain that would change which directory a file lands in.
/// A GUID formatted as 32 hex digits cannot contain a separator, a dot-dot, a drive letter or a device name.
/// <para>
/// Files are written under a ".partial" name and renamed only once completely written. A crash or an
/// aborted upload therefore never leaves a file under a final name, and nothing ever tries to read one.
/// </para>
/// </remarks>
internal sealed class AttachmentStorage
{
    private const string StoredExtension = ".smxa";
    private const string PartialExtension = ".partial";

    /// <summary>The extension of an attachment's index entry.</summary>
    public const string MetadataExtension = ".json";

    public AttachmentStorage(IOptions<AttachmentOptions> options, IHostEnvironment environment)
    {
        Root = Path.GetFullPath(options.Value.StoragePath, environment.ContentRootPath);
        Directory.CreateDirectory(Root);
    }

    /// <summary>The absolute storage directory.</summary>
    public string Root { get; }

    public string StoredPath(Guid id) => Path.Combine(Root, id.ToString("N") + StoredExtension);

    public string PartialPath(Guid id) => Path.Combine(Root, id.ToString("N") + PartialExtension);

    public string MetadataPath(Guid id) => Path.Combine(Root, id.ToString("N") + MetadataExtension);

    public string PartialMetadataPath(Guid id) => MetadataPath(id) + PartialExtension;

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

    /// <summary>Removes every file for an id, stored or partial, content or index. Missing files are not an error.</summary>
    /// <remarks>
    /// The index entry is removed first. If the process dies partway through, what is left is content with no
    /// entry — invisible, and never listed — rather than an entry pointing at content that no longer exists.
    /// </remarks>
    public void Delete(Guid id)
    {
        File.Delete(PartialMetadataPath(id));
        File.Delete(MetadataPath(id));
        File.Delete(PartialPath(id));
        File.Delete(StoredPath(id));
    }
}

