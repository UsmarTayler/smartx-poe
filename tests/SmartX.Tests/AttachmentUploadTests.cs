using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers streamed, encrypted upload end to end through the real API.
/// </summary>
public class AttachmentUploadTests
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x01, 0x02, 0x03, 0x04];

    [Fact]
    public async Task A_streamed_upload_round_trips_byte_identical_after_decryption()
    {
        // Five megabytes, about eighty chunks, generated as it is sent. Neither the upload nor the check
        // holds the file in a byte array: both sides are hashed as streams.
        const long size = 5L * 1024 * 1024 + 123;
        using var host = await AttachmentTestHost.CreateAsync();

        var upload = await host.UploadAsync(new GeneratedLogStream(size), "gateway.log", "text/plain");

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var attachment = (await upload.Content.ReadFromJsonAsync<AttachmentMetadata>())!;

        var expected = await GeneratedLogStream.HashAsync(size);
        Assert.Equal(size, attachment.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(expected), attachment.Sha256);
        Assert.Equal(AttachmentKind.HardwareLog, attachment.Kind);

        using var download = await host.Client.GetAsync($"/api/attachments/{attachment.Id}", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(size, download.Content.Headers.ContentLength);

        await using var body = await download.Content.ReadAsStreamAsync();
        Assert.Equal(expected, await SHA256.HashDataAsync(body));
    }

    [Fact]
    public async Task What_is_stored_on_disk_is_ciphertext_of_the_expected_size()
    {
        using var host = await AttachmentTestHost.CreateAsync();
        var plaintext = "sample_rate: 10\nthreshold: 42\n"u8.ToArray();

        var upload = await host.UploadAsync(plaintext, "sensor.yaml", "application/x-yaml");
        var attachment = (await upload.Content.ReadFromJsonAsync<AttachmentMetadata>())!;

        var stored = Assert.Single(host.StoredFiles());
        var bytes = await File.ReadAllBytesAsync(stored);

        Assert.Equal("SMXA"u8.ToArray(), bytes[..4]);
        Assert.Equal(Api.Attachments.EncryptedFileFormat.CiphertextLength(plaintext.Length), bytes.Length);
        Assert.Equal("application/yaml", attachment.ContentType);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_rejected_mid_stream_and_leaves_nothing_behind()
    {
        // A 256 KB limit and a 2 MB upload. The rejection comes from counting the bytes as they arrive;
        // no Content-Length is involved, because the generated stream does not have one.
        using var host = await AttachmentTestHost.CreateAsync(maxFileBytes: 256 * 1024);

        var response = await host.UploadAsync(new GeneratedLogStream(2L * 1024 * 1024), "huge.log", "text/plain");

        // In-process the 413 always arrives. Over a real network a client still mid-send may instead see the
        // connection close, because the server stops reading rather than draining the rest — see the README.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(host.StoredFiles());

        var list = await host.Client.GetFromJsonAsync<List<AttachmentMetadata>>($"/api/sensors/{Thermometer}/attachments");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task A_file_exactly_at_the_limit_is_accepted()
    {
        using var host = await AttachmentTestHost.CreateAsync(maxFileBytes: 200_000);

        var upload = await host.UploadAsync(new GeneratedLogStream(200_000), "edge.log", "text/plain");

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
    }

    [Fact]
    public async Task Png_bytes_are_rejected_when_anything_claims_otherwise()
    {
        // One host for the three claims; each Theory row used to boot its own. The message names the failing claim.
        using var host = await AttachmentTestHost.CreateAsync();

        foreach (var (fileName, declared) in new[] { ("photo.txt", "text/plain"), ("photo.png", "text/plain"), ("photo.jpg", "image/jpeg") })
        {
            var response = await host.UploadAsync(PngBytes, fileName, declared);

            Assert.True(response.StatusCode == HttpStatusCode.UnsupportedMediaType, $"{fileName} declared {declared}: expected 415, got {(int)response.StatusCode}.");
            Assert.True(host.StoredFiles().Length == 0, $"{fileName} declared {declared}: something was stored.");
        }
    }

    [Fact]
    public async Task Text_declared_as_an_image_is_rejected()
    {
        using var host = await AttachmentTestHost.CreateAsync();

        var response = await host.UploadAsync("<script>alert(1)</script>"u8.ToArray(), "photo.png", "image/png");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_binary_disguised_as_a_log_is_rejected_even_when_it_starts_like_text()
    {
        // Opens with a line of plausible text, so the first bytes look fine. The NUL bytes after the first
        // chunk are what give it away, which is why text is validated across the whole stream.
        using var host = await AttachmentTestHost.CreateAsync();
        var content = new MemoryStream();
        content.Write(Encoding.UTF8.GetBytes(new string('a', 100_000)));
        content.Write(new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x03, 0x00 });
        content.Position = 0;

        var response = await host.UploadAsync(content, "firmware.log", "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task Extensions_outside_the_policy_are_rejected()
    {
        using var host = await AttachmentTestHost.CreateAsync();

        foreach (var fileName in new[] { "payload.exe", "page.html", "noextension" })
        {
            var response = await host.UploadAsync("hello"u8.ToArray(), fileName, "text/plain");

            Assert.True(response.StatusCode == HttpStatusCode.UnsupportedMediaType, $"{fileName}: expected 415, got {(int)response.StatusCode}.");
        }
    }

    [Fact]
    public async Task A_real_image_is_stored_as_the_type_its_bytes_say()
    {
        using var host = await AttachmentTestHost.CreateAsync();

        // No declared type at all: the bytes decide.
        var response = await host.UploadAsync(PngBytes, "install.png", contentType: null);
        var attachment = (await response.Content.ReadFromJsonAsync<AttachmentMetadata>())!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(AttachmentKind.DeploymentPhoto, attachment.Kind);
    }

    [Fact]
    public async Task A_deployment_photo_must_be_an_image()
    {
        using var host = await AttachmentTestHost.CreateAsync();

        var response = await host.UploadAsync("not a photo"u8.ToArray(), "notes.txt", "text/plain", kind: "DeploymentPhoto");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_filename_with_path_traversal_is_stored_safely()
    {
        using var host = await AttachmentTestHost.CreateAsync();
        var outside = Path.GetDirectoryName(host.StorageDirectory)!;
        var known = new HashSet<string>(host.StoredFiles());

        foreach (var fileName in new[]
        {
            @"..\..\Windows\System32\evil.log",
            "../../etc/passwd.txt",
            @"C:\fakepath\con.txt",
            "..\\..\\..\\..\\..\\..\\..\\escape.log",
        })
        {
            var response = await host.UploadAsync("line one\n"u8.ToArray(), fileName, "text/plain");

            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{fileName}: expected 201, got {(int)response.StatusCode}.");
            var attachment = (await response.Content.ReadFromJsonAsync<AttachmentMetadata>())!;

            // Stored under a generated name, inside the storage directory, and nowhere else. Each upload adds exactly one file.
            var stored = Assert.Single(host.StoredFiles(), path => !known.Contains(path));
            known.Add(stored);
            Assert.True(host.StorageDirectory == Path.GetDirectoryName(stored), $"{fileName}: stored outside the storage directory.");
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(stored), "^[0-9a-f]{32}\\.smxa$"), $"{fileName}: stored under {Path.GetFileName(stored)}.");
            Assert.True(Directory.GetFiles(outside).Length == 0, $"{fileName}: something was written beside the storage directory.");

            // The remembered name keeps nothing that could be read as a path.
            Assert.DoesNotContain("/", attachment.OriginalFileName, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", attachment.OriginalFileName, StringComparison.Ordinal);
            Assert.DoesNotContain("..", attachment.OriginalFileName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Upload_to_an_unregistered_mac_returns_404_and_reads_nothing()
    {
        using var host = await AttachmentTestHost.CreateAsync();

        var response = await host.UploadAsync(new MemoryStream("x"u8.ToArray()), "a.log", "text/plain", mac: "AA:BB:CC:DD:EE:99");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task A_request_carrying_two_files_is_rejected_and_neither_is_kept()
    {
        using var host = await AttachmentTestHost.CreateAsync();
        var body = new MultipartFormDataContent
        {
            { new ByteArrayContent("one"u8.ToArray()), "file", "one.txt" },
            { new ByteArrayContent("two"u8.ToArray()), "file", "two.txt" },
        };

        var response = await host.Client.PostAsync($"/api/sensors/{Thermometer}/attachments", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.StoredFiles());
    }

    [Fact]
    public async Task A_non_multipart_body_is_rejected()
    {
        using var host = await AttachmentTestHost.CreateAsync();

        var response = await host.Client.PostAsync($"/api/sensors/{Thermometer}/attachments", new StringContent("raw"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
