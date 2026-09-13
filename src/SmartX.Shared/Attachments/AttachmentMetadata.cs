namespace SmartX.Shared;

/// <summary>
/// Everything known about a stored attachment except its contents.
/// </summary>
/// <param name="Id">The attachment's identity. Also the basis of its name on disk.</param>
/// <param name="MacAddress">The sensor it is attached to.</param>
/// <param name="OriginalFileName">
/// The name the file was uploaded with, sanitised. For display and for the download's suggested name
/// only — it is never used to build a path.
/// </param>
/// <param name="ContentType">
/// The type established by inspecting the file's bytes, never the type the uploader claimed.
/// </param>
/// <param name="SizeBytes">The plaintext size.</param>
/// <param name="Sha256">Hex SHA-256 of the plaintext, computed while it streamed in.</param>
/// <param name="UploadedAt">When the upload completed.</param>
/// <param name="Kind">What the file is for.</param>
public sealed record AttachmentMetadata(
    Guid Id,
    string MacAddress,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset UploadedAt,
    AttachmentKind Kind)
{
    /// <summary>Whether this attachment is an image, and so has a thumbnail.</summary>
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.Ordinal);
}
