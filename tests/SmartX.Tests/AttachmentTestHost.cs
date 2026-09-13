using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace SmartX.Tests;

/// <summary>
/// An API host for attachment tests, with its own storage directory and its own key.
/// </summary>
/// <remarks>
/// Each test gets a fresh temporary directory, so assertions about what is on disk — no partial files, no
/// plaintext, nothing outside the directory — see only that test's files. The key is random per host and
/// supplied through configuration, exactly as a real deployment supplies it, never from source.
/// </remarks>
internal sealed class AttachmentTestHost : IDisposable
{
    private AttachmentTestHost(long maxFileBytes)
    {
        StorageDirectory = Path.Combine(Path.GetTempPath(), "smartx-tests", Guid.NewGuid().ToString("N"), "attachments");
        Directory.CreateDirectory(StorageDirectory);

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Attachments:StoragePath"] = StorageDirectory,
                    ["Attachments:EncryptionKey"] = key,
                    ["Attachments:MaxFileBytes"] = maxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })));

        Client = Factory.CreateClient();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public string StorageDirectory { get; }

    /// <summary>A host with the sensor <see cref="ApiTestHost.Thermometer"/> already registered.</summary>
    public static async Task<AttachmentTestHost> CreateAsync(long maxFileBytes = 100L * 1024 * 1024)
    {
        var host = new AttachmentTestHost(maxFileBytes);
        var response = await host.Client.PostAsync("/api/sensors", ApiTestHost.Thermometer10To30());
        response.EnsureSuccessStatusCode();

        return host;
    }

    /// <summary>Every file in the storage directory, including any partial files.</summary>
    public string[] StoredFiles() => Directory.GetFiles(StorageDirectory, "*", SearchOption.AllDirectories);

    /// <summary>Uploads one file as a streamed multipart section.</summary>
    public Task<HttpResponseMessage> UploadAsync(
        Stream content,
        string fileName,
        string? contentType,
        string mac = ApiTestHost.Thermometer,
        string? kind = null)
    {
        var file = new StreamContent(content, 64 * 1024);

        if (contentType is not null)
        {
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        var body = new MultipartFormDataContent { { file, "file", fileName } };
        var query = kind is null ? string.Empty : $"?kind={kind}";

        return Client.PostAsync($"/api/sensors/{mac}/attachments{query}", body);
    }

    public Task<HttpResponseMessage> UploadAsync(byte[] content, string fileName, string? contentType, string? kind = null) =>
        UploadAsync(new MemoryStream(content), fileName, contentType, kind: kind);

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();

        try
        {
            Directory.Delete(Path.GetDirectoryName(StorageDirectory)!, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a virus scanner holding a handle should not fail the test that just passed.
        }
    }
}

/// <summary>
/// A read-only, non-seekable stream of generated log text, so large uploads never need a byte[] of the file.
/// </summary>
/// <remarks>
/// Deterministic for a given seed and length, so the same content can be regenerated to hash and compare
/// against a download without ever having held the original.
/// </remarks>
internal sealed class GeneratedLogStream(long length, int seed = 42, string marker = "") : Stream
{
    private readonly Random _random = new(seed);
    private byte[] _line = [];
    private int _lineOffset;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>The SHA-256 the generated content will have.</summary>
    public static async Task<byte[]> HashAsync(long length, int seed = 42, string marker = "")
    {
        await using var stream = new GeneratedLogStream(length, seed, marker);

        return await SHA256.HashDataAsync(stream);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var written = 0;

        while (written < buffer.Length && _position < length)
        {
            if (_lineOffset == _line.Length)
            {
                _line = Encoding.UTF8.GetBytes(
                    $"2026-09-13T12:{_random.Next(60):00}:{_random.Next(60):00}Z node=AA:BB:CC:DD:EE:01 " +
                    $"temp={_random.Next(100, 400) / 10.0:0.0} seq={_random.Next()} {marker}\n");
                _lineOffset = 0;
            }

            var take = (int)Math.Min(Math.Min(buffer.Length - written, _line.Length - _lineOffset), length - _position);
            _line.AsSpan(_lineOffset, take).CopyTo(buffer[written..]);

            _lineOffset += take;
            _position += take;
            written += take;
        }

        return written;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
