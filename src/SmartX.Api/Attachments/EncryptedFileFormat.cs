using System.Buffers.Binary;

namespace SmartX.Api.Attachments;

/// <summary>
/// The on-disk layout of an encrypted attachment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why chunks.</b> .NET's <see cref="System.Security.Cryptography.AesGcm"/> is one-shot, with no streaming mode, so encrypting
/// a 100 MB log in one call would hold it all in memory, which the streamed upload exists to avoid. The plaintext is cut into
/// 64 KiB chunks, each sealed on its own, so memory use is one chunk whatever the file size.
/// </para>
/// <para>
/// Sealing chunks separately would allow three attacks, and the nonce construction closes all three (the STREAM construction, as
/// in Google Tink's streaming AEAD and age). <b>Reordering:</b> each nonce holds the chunk's position, so a moved chunk fails its
/// tag. <b>Truncation:</b> the last chunk's nonce carries a "final" flag, so a file cut at a chunk boundary ends on a chunk
/// sealed as not final and fails. <b>Splicing:</b> every nonce starts with a random per-file prefix and the header is
/// authenticated with every chunk, so a chunk from another file fails.
/// </para>
/// <code>
/// header (16 bytes)  "SMXA" | version (1) | chunk size, uint32 BE (4) | nonce prefix (7, random)
/// chunk 0            ciphertext (chunk size) | GCM tag (16)
/// final chunk        ciphertext (0..chunk size) | GCM tag (16), sealed with the final flag set
/// nonce for chunk i  nonce prefix (7) | i, uint32 BE (4) | final flag (1)
/// </code>
/// <para>
/// The prefix is random per file, not derived from the attachment id, because reusing a GCM nonce under one key leaks the XOR of
/// the plaintexts and allows tag forgery.
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
