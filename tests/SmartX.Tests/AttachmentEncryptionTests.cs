using System.Security.Cryptography;
using SmartX.Api.Attachments;

namespace SmartX.Tests;

/// <summary>
/// Covers the chunked AES-GCM format directly: round trips at every chunk boundary, and each attack the
/// nonce construction exists to stop.
/// </summary>
/// <remarks>
/// These run against memory streams because they are testing the format, not the disk. The upload tests
/// cover the same streams writing real files.
/// </remarks>
public class AttachmentEncryptionTests
{
    private const int Chunk = EncryptedFileFormat.ChunkSize;
    private const int Record = EncryptedFileFormat.ChunkSize + EncryptedFileFormat.TagSize;

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static byte[] Plaintext(int length)
    {
        // Seeded, so a failure reproduces with exactly the same bytes.
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);

        return bytes;
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, byte[]? key = null)
    {
        using var destination = new MemoryStream();

        await using (var encrypting = new AesGcmEncryptingStream(destination, key ?? Key))
        {
            // Written in uneven pieces, so chunk boundaries never line up with write boundaries.
            for (var offset = 0; offset < plaintext.Length; offset += 10_007)
            {
                await encrypting.WriteAsync(plaintext.AsMemory(offset, Math.Min(10_007, plaintext.Length - offset)));
            }

            await encrypting.CompleteAsync();
        }

        return destination.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] ciphertext, byte[]? key = null)
    {
        using var source = new MemoryStream(ciphertext);
        await using var decrypting = await AesGcmDecryptingStream.OpenAsync(source, key ?? Key);
        using var plaintext = new MemoryStream();

        await decrypting.CopyToAsync(plaintext);

        return plaintext.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    [InlineData((3 * Chunk) + 7)]
    public async Task Every_size_round_trips_byte_identical(int length)
    {
        var plaintext = Plaintext(length);

        var ciphertext = await EncryptAsync(plaintext);

        Assert.Equal(EncryptedFileFormat.CiphertextLength(length), ciphertext.Length);
        Assert.Equal(plaintext, await DecryptAsync(ciphertext));
    }

    [Fact]
    public async Task The_same_file_encrypts_differently_every_time()
    {
        // A fresh random nonce prefix per file. Identical ciphertext would mean a reused nonce, and would
        // also reveal that two stored files have the same contents.
        var plaintext = Plaintext(1000);

        Assert.NotEqual(await EncryptAsync(plaintext), await EncryptAsync(plaintext));
    }

    [Theory]
    [InlineData(EncryptedFileFormat.HeaderSize + 5)]
    [InlineData(EncryptedFileFormat.HeaderSize + Record + 100)]
    [InlineData(EncryptedFileFormat.HeaderSize + Record + Chunk + 3)]
    public async Task Flipping_one_bit_anywhere_in_a_chunk_fails_loudly(int position)
    {
        var ciphertext = await EncryptAsync(Plaintext((2 * Chunk) + 50));
        ciphertext[position] ^= 0x01;

        await Assert.ThrowsAsync<AttachmentIntegrityException>(() => DecryptAsync(ciphertext));
    }

    [Fact]
    public async Task No_bytes_of_a_tampered_chunk_are_released()
    {
        // The first chunk is intact, the second is tampered. A reader gets all of chunk one and then an
        // exception — never any of chunk two's unauthenticated bytes.
        var ciphertext = await EncryptAsync(Plaintext(2 * Chunk));
        ciphertext[EncryptedFileFormat.HeaderSize + Record + 10] ^= 0xFF;

        using var source = new MemoryStream(ciphertext);
        await using var decrypting = await AesGcmDecryptingStream.OpenAsync(source, Key);
        var buffer = new byte[Chunk];
        var released = 0;

        var failure = await Xunit.Record.ExceptionAsync(async () =>
        {
            int read;

            while ((read = await decrypting.ReadAsync(buffer)) > 0)
            {
                released += read;
            }
        });

        Assert.IsType<AttachmentIntegrityException>(failure);
        Assert.Equal(Chunk, released);
    }

    [Fact]
    public async Task Dropping_the_final_chunk_is_detected_as_truncation()
    {
        // Three full chunks plus a short final one. Cutting the short one off leaves a file that ends on a
        // chunk sealed as "not final", which must not be accepted as a shorter, valid file.
        var ciphertext = await EncryptAsync(Plaintext((3 * Chunk) + 7));
        var truncated = ciphertext[..(EncryptedFileFormat.HeaderSize + (3 * Record))];

        await Assert.ThrowsAsync<AttachmentIntegrityException>(() => DecryptAsync(truncated));
    }

    [Fact]
    public async Task Swapping_two_chunks_is_detected()
    {
        var ciphertext = await EncryptAsync(Plaintext(3 * Chunk));
        var first = ciphertext.AsSpan(EncryptedFileFormat.HeaderSize, Record).ToArray();
        var second = ciphertext.AsSpan(EncryptedFileFormat.HeaderSize + Record, Record).ToArray();

        second.CopyTo(ciphertext, EncryptedFileFormat.HeaderSize);
        first.CopyTo(ciphertext, EncryptedFileFormat.HeaderSize + Record);

        await Assert.ThrowsAsync<AttachmentIntegrityException>(() => DecryptAsync(ciphertext));
    }

    [Fact]
    public async Task Altering_the_header_breaks_every_chunk()
    {
        var ciphertext = await EncryptAsync(Plaintext(100));
        ciphertext[EncryptedFileFormat.NoncePrefixOffset] ^= 0x01;

        await Assert.ThrowsAsync<AttachmentIntegrityException>(() => DecryptAsync(ciphertext));
    }

    [Fact]
    public async Task The_wrong_key_fails_rather_than_producing_garbage()
    {
        var ciphertext = await EncryptAsync(Plaintext(100));

        await Assert.ThrowsAsync<AttachmentIntegrityException>(() => DecryptAsync(ciphertext, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public async Task A_file_abandoned_before_completion_can_never_be_read()
    {
        // What an aborted upload leaves behind: chunks, but no final chunk.
        using var destination = new MemoryStream();

        await using (var encrypting = new AesGcmEncryptingStream(destination, Key))
        {
            await encrypting.WriteAsync(Plaintext((2 * Chunk) + 10));
        }

        await Assert.ThrowsAsync<AttachmentIntegrityException>(() => DecryptAsync(destination.ToArray()));
    }

    [Fact]
    public async Task A_file_that_is_not_an_attachment_is_rejected_at_the_header()
    {
        await Assert.ThrowsAsync<AttachmentIntegrityException>(() => DecryptAsync("plain text, not encrypted"u8.ToArray()));
    }
}
