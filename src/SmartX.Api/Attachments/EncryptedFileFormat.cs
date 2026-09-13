using System.Buffers.Binary;

namespace SmartX.Api.Attachments;

/// <summary>
/// The on-disk layout of an encrypted attachment.
/// </summary>
/// <remarks>
/// <b>Why chunks, and not one AES-GCM operation over the whole file.</b> .NET's
/// <see cref="System.Security.Cryptography.AesGcm"/> is one-shot: it encrypts a complete buffer in a
/// single call and has no streaming mode. Encrypting a 100 MB log that way would mean holding all
/// 100 MB of plaintext in memory first — exactly what the streamed upload exists to avoid. So the
/// plaintext is cut into fixed 64 KiB chunks and each chunk is sealed on its own, which caps the
/// plaintext held at any moment at one chunk regardless of file size.
/// <para>
/// Sealing chunks independently opens three attacks a single GCM operation would not have, and the
/// nonce construction closes all three. This is the STREAM construction (Hoang, Reyhanitabar, Rogaway
/// and Vizár, 2015), the same approach used by Google Tink's streaming AEAD and by age.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Reordering</b> chunks: each nonce contains the chunk's position, so a chunk moved elsewhere is
/// decrypted under the wrong nonce and its tag fails.
/// </description></item>
/// <item><description>
/// <b>Truncation</b> — cutting the file off at a chunk boundary: the last chunk's nonce carries a
/// "final" flag. A file cut short ends on a chunk that was sealed as not final, so it fails when the
/// reader opens it as the last one.
/// </description></item>
/// <item><description>
/// <b>Splicing</b> chunks from another file: every nonce starts with a random prefix unique to its file,
/// and the header is authenticated with every chunk, so a chunk taken from any other file fails.
/// </description></item>
/// </list>
/// <para>Layout:</para>
/// <code>
/// header (16 bytes)  "SMXA" | version (1) | chunk size, uint32 BE (4) | nonce prefix (7, random)
/// chunk 0            ciphertext (chunk size) | GCM tag (16)
/// chunk 1            ...
/// final chunk        ciphertext (0..chunk size) | GCM tag (16), sealed with the final flag set
/// nonce for chunk i  nonce prefix (7) | i, uint32 BE (4) | final flag (1)
/// </code>
/// <para>
/// Every file gets a fresh random prefix, so nonces are not reused under the key across files, and
/// within a file the counter makes every nonce distinct. Reusing a GCM nonce under one key is
/// catastrophic — it leaks the XOR of the two plaintexts and allows tag forgery — which is why the prefix
/// is random per file rather than derived from something predictable such as the attachment's id.
/// </para>
/// </remarks>
internal static class EncryptedFileFormat
{
    public const byte Version = 1;
    public const int ChunkSize = 64 * 1024;
    public const int TagSize = 16;
    public const int NonceSize = 12;
    public const int NoncePrefixSize = 7;
    public const int HeaderSize = 16;

    /// <summary>Where the nonce prefix sits inside the header.</summary>
    public const int NoncePrefixOffset = 9;

    /// <summary>The four bytes every encrypted attachment starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "SMXA"u8;

    /// <summary>Builds the nonce for one chunk.</summary>
    public static void WriteNonce(Span<byte> nonce, ReadOnlySpan<byte> header, uint chunkIndex, bool isFinal)
    {
        header.Slice(NoncePrefixOffset, NoncePrefixSize).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixSize..], chunkIndex);
        nonce[NonceSize - 1] = isFinal ? (byte)1 : (byte)0;
    }

    /// <summary>The size on disk of a file holding <paramref name="plaintextBytes"/> bytes.</summary>
    /// <remarks>
    /// The final chunk always exists, even for an empty file, so that a file with no chunks at all is
    /// recognisably truncated rather than indistinguishable from an empty plaintext.
    /// </remarks>
    public static long CiphertextLength(long plaintextBytes)
    {
        var chunks = Math.Max(1, (plaintextBytes + ChunkSize - 1) / ChunkSize);

        return HeaderSize + plaintextBytes + (chunks * TagSize);
    }
}

/// <summary>A stored attachment failed authentication: it has been altered, truncated or corrupted.</summary>
/// <remarks>
/// Thrown instead of returning any bytes from the failing chunk. Decrypted data is only released after
/// its chunk's tag verifies, so a caller never receives plaintext that was not authenticated.
/// </remarks>
public sealed class AttachmentIntegrityException(string message, Exception? inner = null) : Exception(message, inner);
