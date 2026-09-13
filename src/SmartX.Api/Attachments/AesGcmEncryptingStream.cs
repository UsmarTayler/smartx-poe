using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SmartX.Api.Attachments;

/// <summary>
/// A write-only stream that encrypts everything written to it, one chunk at a time, into another stream.
/// </summary>
/// <remarks>
/// Holds at most one chunk of plaintext. That buffer is zeroed after every chunk is sealed and before it
/// goes back to the pool, so plaintext does not linger in memory for whatever rents the buffer next.
/// <para>
/// <see cref="CompleteAsync"/> must be called to write the final chunk. Disposing without completing
/// deliberately leaves the file without one, so an aborted upload fails as truncated if anything ever
/// tries to read it — it can never be mistaken for a complete file.
/// </para>
/// <para>
/// Only asynchronous writes are supported. The upload path is asynchronous end to end, and a synchronous
/// write here would block a request thread on disk I/O.
/// </para>
/// </remarks>
internal sealed class AesGcmEncryptingStream : Stream
{
    private readonly Stream _destination;
    private readonly AesGcm _aes;
    private readonly byte[] _header = new byte[EncryptedFileFormat.HeaderSize];
    private readonly byte[] _nonce = new byte[EncryptedFileFormat.NonceSize];
    private readonly byte[] _tag = new byte[EncryptedFileFormat.TagSize];
    private readonly byte[] _plaintext = ArrayPool<byte>.Shared.Rent(EncryptedFileFormat.ChunkSize);
    private readonly byte[] _ciphertext = ArrayPool<byte>.Shared.Rent(EncryptedFileFormat.ChunkSize);

    private int _buffered;
    private uint _chunkIndex;
    private bool _headerWritten;
    private bool _completed;
    private bool _disposed;

    /// <param name="destination">Receives the ciphertext. Not disposed by this stream.</param>
    /// <param name="key">A 256-bit AES key.</param>
    public AesGcmEncryptingStream(Stream destination, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(destination);

        _destination = destination;
        _aes = new AesGcm(key, EncryptedFileFormat.TagSize);

        EncryptedFileFormat.Magic.CopyTo(_header);
        _header[4] = EncryptedFileFormat.Version;
        BinaryPrimitives.WriteUInt32BigEndian(_header.AsSpan(5), EncryptedFileFormat.ChunkSize);
        RandomNumberGenerator.Fill(_header.AsSpan(EncryptedFileFormat.NoncePrefixOffset, EncryptedFileFormat.NoncePrefixSize));
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed && !_completed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed)
        {
            throw new InvalidOperationException("The encrypted file has already been completed.");
        }

        await EnsureHeaderAsync(cancellationToken);

        while (!buffer.IsEmpty)
        {
            // A full buffer is only sealed once more data arrives. Until then it might be the last chunk,
            // and the last chunk has to be sealed with the final flag set.
            if (_buffered == EncryptedFileFormat.ChunkSize)
            {
                await SealChunkAsync(isFinal: false, cancellationToken);
            }

            var take = Math.Min(EncryptedFileFormat.ChunkSize - _buffered, buffer.Length);
            buffer.Span[..take].CopyTo(_plaintext.AsSpan(_buffered));
            _buffered += take;
            buffer = buffer[take..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Seals whatever is buffered as the final chunk. Nothing may be written afterwards.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_completed)
        {
            return;
        }

        await EnsureHeaderAsync(cancellationToken);
        await SealChunkAsync(isFinal: true, cancellationToken);
        await _destination.FlushAsync(cancellationToken);

        _completed = true;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync; the upload path never blocks a thread on I/O.");

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _aes.Dispose();

            CryptographicOperations.ZeroMemory(_plaintext);
            ArrayPool<byte>.Shared.Return(_plaintext);
            ArrayPool<byte>.Shared.Return(_ciphertext);
        }

        base.Dispose(disposing);
    }

    private async ValueTask EnsureHeaderAsync(CancellationToken cancellationToken)
    {
        if (_headerWritten)
        {
            return;
        }

        await _destination.WriteAsync(_header, cancellationToken);
        _headerWritten = true;
    }

    private async ValueTask SealChunkAsync(bool isFinal, CancellationToken cancellationToken)
    {
        if (_chunkIndex == uint.MaxValue)
        {
            // 2^32 chunks of 64 KiB is about 256 TiB. Unreachable under any sane size limit, but a wrapped
            // counter would reuse nonces, so it is refused rather than assumed away.
            throw new InvalidOperationException("The file is too large to encrypt under a single nonce prefix.");
        }

        var length = _buffered;

        EncryptedFileFormat.WriteNonce(_nonce, _header, _chunkIndex, isFinal);

        // The header is passed as associated data: authenticated with every chunk, but not encrypted.
        // Altering the version, chunk size or nonce prefix therefore breaks every tag in the file.
        _aes.Encrypt(_nonce, _plaintext.AsSpan(0, length), _ciphertext.AsSpan(0, length), _tag, _header);

        CryptographicOperations.ZeroMemory(_plaintext.AsSpan(0, length));

        await _destination.WriteAsync(_ciphertext.AsMemory(0, length), cancellationToken);
        await _destination.WriteAsync(_tag, cancellationToken);

        _buffered = 0;
        _chunkIndex++;
    }
}
