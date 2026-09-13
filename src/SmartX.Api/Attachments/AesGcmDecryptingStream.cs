using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmartX.Api.Attachments;

/// <summary>
/// A read-only stream that decrypts an encrypted attachment chunk by chunk, verifying every chunk's tag
/// before releasing any of its bytes.
/// </summary>
/// <remarks>
/// A chunk is decrypted into a private buffer and handed out only after its tag has verified. If
/// verification fails, the read throws <see cref="AttachmentIntegrityException"/> and the buffer is
/// zeroed, so a tampered file fails loudly instead of returning plausible-looking corrupt data.
/// <para>
/// Requires a seekable source, which a file on disk always is. Knowing where the file ends is how a full
/// chunk at the very end is recognised as the final one.
/// </para>
/// </remarks>
internal sealed class AesGcmDecryptingStream : Stream
{
    private const int RecordSize = EncryptedFileFormat.ChunkSize + EncryptedFileFormat.TagSize;

    private readonly Stream _source;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly byte[] _nonce = new byte[EncryptedFileFormat.NonceSize];
    private readonly byte[] _record = ArrayPool<byte>.Shared.Rent(RecordSize);
    private readonly byte[] _plaintext = ArrayPool<byte>.Shared.Rent(EncryptedFileFormat.ChunkSize);

    private int _plaintextStart;
    private int _plaintextEnd;
    private uint _chunkIndex;
    private bool _finished;
    private bool _disposed;

    private AesGcmDecryptingStream(Stream source, ReadOnlySpan<byte> key, byte[] header)
    {
        _source = source;
        _header = header;
        _aes = new AesGcm(key, EncryptedFileFormat.TagSize);
    }

    /// <summary>Reads and checks the header, ready to decrypt.</summary>
    /// <param name="source">A seekable stream at the start of the encrypted file. Not disposed by this stream.</param>
    /// <param name="key">The 256-bit key the file was encrypted with.</param>
    /// <exception cref="AttachmentIntegrityException">The header is missing or is not a supported format.</exception>
    public static async Task<AesGcmDecryptingStream> OpenAsync(
        Stream source,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanSeek)
        {
            throw new ArgumentException("The encrypted source must be seekable.", nameof(source));
        }

        var header = new byte[EncryptedFileFormat.HeaderSize];
        var read = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);

        if (read < header.Length
            || !header.AsSpan(0, 4).SequenceEqual(EncryptedFileFormat.Magic)
            || header[4] != EncryptedFileFormat.Version
            || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5)) != EncryptedFileFormat.ChunkSize)
        {
            throw new AttachmentIntegrityException("The stored file does not have a valid encrypted attachment header.");
        }

        return new AesGcmDecryptingStream(source, key.Span, header);
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_plaintextStart == _plaintextEnd)
        {
            if (_finished)
            {
                return 0;
            }

            await OpenNextChunkAsync(cancellationToken);
        }

        var count = Math.Min(buffer.Length, _plaintextEnd - _plaintextStart);
        _plaintext.AsSpan(_plaintextStart, count).CopyTo(buffer.Span);
        _plaintextStart += count;

        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use ReadAsync; downloads never block a thread on I/O.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _aes.Dispose();

            CryptographicOperations.ZeroMemory(_plaintext);
            ArrayPool<byte>.Shared.Return(_plaintext);
            ArrayPool<byte>.Shared.Return(_record);
        }

        base.Dispose(disposing);
    }

    private async ValueTask OpenNextChunkAsync(CancellationToken cancellationToken)
    {
        var read = await _source.ReadAtLeastAsync(_record.AsMemory(0, RecordSize), RecordSize, throwOnEndOfStream: false, cancellationToken);

        if (read < EncryptedFileFormat.TagSize)
        {
            // The writer always emits a final chunk, even for an empty file, so running out of data before
            // one has been seen means the file was cut short.
            throw new AttachmentIntegrityException("The stored file is truncated.");
        }

        // A short record can only be the last. A full record is the last exactly when nothing follows it.
        var isFinal = read < RecordSize || _source.Position >= _source.Length;
        var length = read - EncryptedFileFormat.TagSize;

        EncryptedFileFormat.WriteNonce(_nonce, _header, _chunkIndex, isFinal);

        try
        {
            _aes.Decrypt(
                _nonce,
                _record.AsSpan(0, length),
                _record.AsSpan(length, EncryptedFileFormat.TagSize),
                _plaintext.AsSpan(0, length),
                _header);
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(_plaintext);

            throw new AttachmentIntegrityException(
                $"Chunk {_chunkIndex} of the stored file failed authentication: it has been altered, truncated or corrupted.",
                exception);
        }

        _plaintextStart = 0;
        _plaintextEnd = length;
        _chunkIndex++;
        _finished = isFinal;
    }
}
