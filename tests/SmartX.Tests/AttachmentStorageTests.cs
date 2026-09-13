using System.Net;
using System.Net.Http.Json;
using System.Text;
using SmartX.Api.Attachments;
using SmartX.Shared;
using static SmartX.Tests.ApiTestHost;

namespace SmartX.Tests;

/// <summary>
/// Covers what happens to a stored attachment afterwards: tampering, plaintext on disk, listing, thumbnails,
/// deletion — plus the file name sanitiser on its own.
/// </summary>
public class AttachmentStorageTests
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static async Task<AttachmentMetadata> UploadAsync(AttachmentTestHost host, Stream content, string fileName, string contentType = "text/plain")
    {
        var response = await host.UploadAsync(content, fileName, contentType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AttachmentMetadata>())!;
    }

    private static async Task FlipByteAsync(string path, long position)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        file.Position = position;
        var original = file.ReadByte();
        file.Position = position;
        file.WriteByte((byte)(original ^ 0x01));
    }

    [Fact]
    public async Task Tampering_with_a_small_file_fails_the_download_before_any_body_is_sent()
    {
        using var host = await AttachmentTestHost.CreateAsync();
        var attachment = await UploadAsync(host, new MemoryStream("threshold=42\n"u8.ToArray()), "device.conf");

        await FlipByteAsync(Assert.Single(host.StoredFiles()), EncryptedFileFormat.HeaderSize + 3);

        var response = await host.Client.GetAsync($"/api/attachments/{attachment.Id}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("integrity", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tampering_deep_inside_a_large_file_aborts_the_download_rather_than_completing_it()
    {
        // The first chunk is intact, so the 200 goes out; the fifth is not, so the transfer must break off
        // instead of delivering a full-length file with corrupt bytes in it.
        using var host = await AttachmentTestHost.CreateAsync();
        var attachment = await UploadAsync(host, new GeneratedLogStream(600_000), "big.log");
        var record = EncryptedFileFormat.ChunkSize + EncryptedFileFormat.TagSize;

        await FlipByteAsync(Assert.Single(host.StoredFiles()), EncryptedFileFormat.HeaderSize + (4L * record) + 100);

        using var response = await host.Client.GetAsync($"/api/attachments/{attachment.Id}", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var body = await response.Content.ReadAsStreamAsync();
            await body.CopyToAsync(Stream.Null);
        });
    }

    [Fact]
    public async Task The_plaintext_never_touches_disk()
    {
        // Every line of the upload carries a unique marker. Afterwards, every file in the storage directory is
        // searched for it, and so is every ASP.NET Core form-buffering temp file created during the upload —
        // the file the IFormFile path would have written the plaintext to.
        var marker = $"PLAINTEXT-MARKER-{Guid.NewGuid():N}";
        var tempDirectory = Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath();
        var bufferingFilesBefore = Directory.GetFiles(tempDirectory, "ASPNETCORE_*.tmp").ToHashSet();

        using var host = await AttachmentTestHost.CreateAsync();
        var attachment = await UploadAsync(host, new GeneratedLogStream(1_000_000, marker: marker), "secret.log");

        var needle = Encoding.UTF8.GetBytes(marker);

        foreach (var file in host.StoredFiles())
        {
            Assert.False(await ContainsAsync(file, needle), $"{file} contains plaintext.");
        }

        Assert.DoesNotContain(Directory.GetFiles(tempDirectory, "ASPNETCORE_*.tmp"), file => !bufferingFilesBefore.Contains(file));

        // And the marker really was in the upload: it comes back out on download.
        var downloaded = await host.Client.GetStringAsync($"/api/attachments/{attachment.Id}");
        Assert.Contains(marker, downloaded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sensors_attachments_are_listed_newest_first()
    {
        using var host = await AttachmentTestHost.CreateAsync();
        await UploadAsync(host, new MemoryStream("a"u8.ToArray()), "first.txt");
        await Task.Delay(20);
        await UploadAsync(host, new MemoryStream("b"u8.ToArray()), "second.txt");

        var list = await host.Client.GetFromJsonAsync<List<AttachmentMetadata>>($"/api/sensors/{Thermometer}/attachments");

        Assert.Equal(["second.txt", "first.txt"], list!.Select(attachment => attachment.OriginalFileName));
    }

    [Fact]
    public async Task Only_images_have_a_thumbnail()
    {
        using var host = await AttachmentTestHost.CreateAsync();
        var photo = await UploadAsync(host, new MemoryStream(PngBytes), "install.png", "image/png");
        var log = await UploadAsync(host, new MemoryStream("x"u8.ToArray()), "run.log");

        var thumbnail = await host.Client.GetAsync($"/api/attachments/{photo.Id}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/png", thumbnail.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngBytes, await thumbnail.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/attachments/{log.Id}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task A_download_is_served_as_an_attachment_the_browser_will_not_sniff()
    {
        using var host = await AttachmentTestHost.CreateAsync();
        var attachment = await UploadAsync(host, new MemoryStream("x"u8.ToArray()), "run.log");

        var response = await host.Client.GetAsync($"/api/attachments/{attachment.Id}");

        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("run.log", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Deleting_removes_the_metadata_and_the_encrypted_file()
    {
        using var host = await AttachmentTestHost.CreateAsync();
        var attachment = await UploadAsync(host, new MemoryStream("x"u8.ToArray()), "run.log");

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync($"/api/attachments/{attachment.Id}")).StatusCode);

        Assert.Empty(host.StoredFiles());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/api/attachments/{attachment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync($"/api/attachments/{attachment.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_limits_are_published_for_the_client()
    {
        using var host = await AttachmentTestHost.CreateAsync(maxFileBytes: 12345);

        var limits = await host.Client.GetFromJsonAsync<AttachmentLimits>("/api/attachments/limits");

        Assert.Equal(12345, limits!.MaxFileBytes);
        Assert.Contains(".log", limits.AllowedExtensions);
    }

    [Theory]
    [InlineData(@"..\..\Windows\System32\drivers\etc\hosts.txt", "hosts.txt")]
    [InlineData("../../etc/passwd.log", "passwd.log")]
    [InlineData(@"C:\fakepath\photo.png", "photo.png")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("  ..report.txt..  ", "report.txt")]
    [InlineData("a<b>c:d|e?f*.log", "a_b_c_d_e_f_.log")]
    [InlineData("tab\there.txt", "tab_here.txt")]
    [InlineData("...", FileNameSanitizer.Fallback)]
    [InlineData("", FileNameSanitizer.Fallback)]
    public void File_names_are_reduced_to_a_safe_display_name(string input, string expected)
    {
        Assert.Equal(expected, FileNameSanitizer.Sanitize(input));
    }

    [Fact]
    public void A_very_long_name_is_shortened_but_keeps_its_extension()
    {
        var sanitized = FileNameSanitizer.Sanitize(new string('x', 500) + ".log");

        Assert.True(sanitized.Length <= 128);
        Assert.EndsWith(".log", sanitized, StringComparison.Ordinal);
    }

    /// <summary>Searches a file for a byte sequence without reading it all into memory.</summary>
    private static async Task<bool> ContainsAsync(string path, byte[] needle)
    {
        await using var file = File.OpenRead(path);
        var buffer = new byte[64 * 1024 + needle.Length];
        var carried = 0;
        int read;

        while ((read = await file.ReadAsync(buffer.AsMemory(carried, buffer.Length - carried))) > 0)
        {
            var available = carried + read;

            if (buffer.AsSpan(0, available).IndexOf(needle) >= 0)
            {
                return true;
            }

            // Keep the tail so a match straddling two reads is still found.
            carried = Math.Min(needle.Length - 1, available);
            buffer.AsSpan(available - carried, carried).CopyTo(buffer);
        }

        return false;
    }
}
