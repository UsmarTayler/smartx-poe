using System.Buffers;
using System.Text;
using SmartX.Shared;

namespace SmartX.Api.Attachments;

/// <summary>What a file's bytes say it is.</summary>
internal enum ContentFamily
{
    Unknown,
    Png,
    Jpeg,
    Webp,
    Text
}

/// <summary>
/// Establishes a file's real type from its bytes, and checks that nothing the uploader claimed contradicts it.
/// </summary>
/// <remarks>
/// <b>Why the bytes and not the declared content type.</b> The Content-Type on a multipart section and the
/// extension on a file name are both just text the client chose to send. Renaming <c>payload.exe</c> to
/// <c>photo.png</c> and labelling it <c>image/png</c> takes no skill at all. A file's first bytes are much
/// harder to fake while keeping it the thing it pretends to be: a PNG has to begin with the PNG signature,
/// or no decoder will open it. So the bytes decide the type, and the stored content type always comes from
/// that decision. The declared type and extension are then checked against it, and a contradiction is
/// rejected outright rather than resolved in either direction — a mismatch is a mistake at best and an
/// attempt to smuggle something at worst.
/// <para>
/// Text formats have no signature, so text is established differently: the whole stream, not just its
/// start, has to be valid UTF-8 with no NUL bytes or unexpected control characters. Checking only the first
/// few bytes would accept a file that opens with a line of text and continues as a binary.
/// </para>
/// </remarks>
internal static class ContentInspector
{
    /// <summary>How many leading bytes are needed to recognise every supported signature.</summary>
    public const int SignatureLength = 12;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Identifies the family from a file's first bytes.</summary>
    /// <remarks>
    /// Anything with no recognised image signature is provisionally text. Whether it really is text is
    /// decided by <see cref="TextValidator"/> as the rest of the stream arrives.
    /// </remarks>
    public static ContentFamily Identify(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngSignature))
        {
            return ContentFamily.Png;
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return ContentFamily.Jpeg;
        }

        // RIFF container: "RIFF", a four-byte size, then the form type "WEBP".
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ContentFamily.Webp;
        }

        return TextValidator.LooksLikeTextStart(head) ? ContentFamily.Text : ContentFamily.Unknown;
    }

    /// <summary>
    /// Checks the sniffed family against the file name and the declared type, and works out the type to store.
    /// </summary>
    /// <returns>The content type to store and serve, or null with a reason when the upload must be rejected.</returns>
    public static string? Resolve(ContentFamily family, string fileName, string? declaredContentType, out string? rejection)
    {
        rejection = null;

        var extension = Path.GetExtension(fileName);
        var declared = NormaliseDeclared(declaredContentType);

        if (family == ContentFamily.Unknown)
        {
            rejection = "the file's contents are not a permitted image or text format";

            return null;
        }

        if (family == ContentFamily.Text)
        {
            if (!AttachmentPolicy.TextExtensions.TryGetValue(extension, out var textType))
            {
                rejection = AttachmentPolicy.ImageExtensions.ContainsKey(extension)
                    ? $"the file is named {extension} but its contents are not an image"
                    : $"{Describe(extension)} files are not permitted";

                return null;
            }

            if (declared is not null && !IsTextType(declared))
            {
                rejection = $"it was declared as {declared} but its contents are text";

                return null;
            }

            return textType;
        }

        var sniffedType = family switch
        {
            ContentFamily.Png => "image/png",
            ContentFamily.Jpeg => "image/jpeg",
            _ => "image/webp"
        };

        if (!AttachmentPolicy.ImageExtensions.TryGetValue(extension, out var extensionType) || extensionType != sniffedType)
        {
            rejection = $"its contents are {sniffedType} but the file is named {Describe(extension)}";

            return null;
        }

        if (declared is not null && declared != sniffedType)
        {
            rejection = $"it was declared as {declared} but its contents are {sniffedType}";

            return null;
        }

        return sniffedType;
    }

    /// <summary>The declared type, or null when the client made no specific claim.</summary>
    /// <remarks>
    /// Browsers often send no type, or application/octet-stream, for extensions they do not recognise —
    /// .log and .yaml among them. That is the absence of a claim rather than a contradiction, so it defers
    /// to the bytes instead of rejecting a legitimate log file.
    /// </remarks>
    private static string? NormaliseDeclared(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();

        return mediaType switch
        {
            "" or "application/octet-stream" => null,
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            _ => mediaType
        };
    }

    private static bool IsTextType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.Ordinal)
        || mediaType is "application/json" or "application/yaml" or "application/x-yaml" or "application/csv";

    private static string Describe(string extension) =>
        string.IsNullOrEmpty(extension) ? "a file with no extension" : extension;
}

/// <summary>
/// Confirms, chunk by chunk, that a whole stream is UTF-8 text.
/// </summary>
/// <remarks>
/// Uses a stateful UTF-8 decoder so that a multi-byte character split across two chunks is judged as one
/// character rather than flagged as two invalid halves. Control characters are checked with a vectorised
/// search, so validating a 100 MB log costs a fraction of the time spent encrypting it.
/// </remarks>
internal sealed class TextValidator
{
    // Every C0 control character except tab, line feed, form feed, carriage return and escape. Escape is
    // allowed because hardware logs routinely contain ANSI colour codes. NUL is the tell-tale sign of a
    // binary file, or of UTF-16 text, which is not supported.
    private static readonly SearchValues<byte> DisallowedControls = SearchValues.Create(
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x0B, 0x0E, 0x0F,
         0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1C, 0x1D, 0x1E, 0x1F, 0x7F]);

    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetDecoder();
    private readonly char[] _chars = ArrayPool<char>.Shared.Rent(EncryptedFileFormat.ChunkSize);

    /// <summary>A quick first look used when sniffing: no disallowed control bytes in the opening bytes.</summary>
    public static bool LooksLikeTextStart(ReadOnlySpan<byte> head) => head.IndexOfAny(DisallowedControls) < 0;

    /// <summary>Checks the next piece of the stream.</summary>
    /// <param name="bytes">The next bytes, in order.</param>
    /// <param name="isFinal">True for the last piece, so an incomplete trailing character is caught.</param>
    /// <returns>False when the content is not text.</returns>
    public bool Accept(ReadOnlySpan<byte> bytes, bool isFinal)
    {
        if (bytes.IndexOfAny(DisallowedControls) >= 0)
        {
            return false;
        }

        try
        {
            while (true)
            {
                _decoder.Convert(bytes, _chars, isFinal, out var bytesUsed, out _, out _);
                bytes = bytes[bytesUsed..];

                // Finished once every byte has been consumed. The decoder's own "completed" flag is not the
                // test: when a chunk ends partway through a multi-byte character it consumes those bytes
                // into its state and reports incomplete, and waiting for it to report complete would loop
                // forever. Bytes left over only happen when the character buffer filled, so go round again.
                if (bytes.IsEmpty)
                {
                    return true;
                }
            }
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Returns the pooled buffer.</summary>
    public void Release() => ArrayPool<char>.Shared.Return(_chars);
}
