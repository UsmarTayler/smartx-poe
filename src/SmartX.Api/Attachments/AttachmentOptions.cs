using Microsoft.Extensions.Options;

namespace SmartX.Api.Attachments;

/// <summary>
/// Configuration for attachment storage.
/// </summary>
/// <remarks>
/// Bound from the "Attachments" section through <c>IOptions</c>. The encryption key is deliberately not
/// in appsettings.json or anywhere else in the repository. Supply it as a user secret during development
/// or an environment variable (<c>Attachments__EncryptionKey</c>) elsewhere; a real deployment would fetch
/// it from a key vault. See the README for exactly where this demonstration stops short of production key
/// management.
/// </remarks>
public sealed class AttachmentOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Attachments";

    /// <summary>Where encrypted files are written. A relative path is resolved against the content root.</summary>
    public string StoragePath { get; set; } = "App_Data/attachments";

    /// <summary>The largest plaintext file accepted, in bytes.</summary>
    /// <remarks>
    /// 100 MiB: enough for a long hardware log or a full-resolution deployment photo. Enforced while the
    /// upload streams, byte by byte, not by trusting the request's Content-Length header.
    /// </remarks>
    public long MaxFileBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>A base64-encoded 256-bit AES key.</summary>
    public string? EncryptionKey { get; set; }
}

/// <summary>
/// Rejects attachment configuration the API cannot safely run with.
/// </summary>
/// <remarks>
/// A missing key is tolerated only in the Development environment, where
/// <see cref="AttachmentKeyProvider"/> substitutes a throwaway key and says so in the log. Anywhere else a
/// missing or malformed key stops the API at startup. Failing there is the point: the alternative is an
/// API that starts, accepts uploads, and cannot encrypt them.
/// </remarks>
internal sealed class AttachmentOptionsValidator(IHostEnvironment environment) : IValidateOptions<AttachmentOptions>
{
    public ValidateOptionsResult Validate(string? name, AttachmentOptions options)
    {
        List<string> failures = [];

        if (options.MaxFileBytes <= 0)
        {
            failures.Add($"{nameof(AttachmentOptions.MaxFileBytes)} must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.StoragePath))
        {
            failures.Add($"{nameof(AttachmentOptions.StoragePath)} is required.");
        }

        if (string.IsNullOrWhiteSpace(options.EncryptionKey))
        {
            if (!environment.IsDevelopment())
            {
                failures.Add(
                    $"{SectionName}:{nameof(AttachmentOptions.EncryptionKey)} is required outside Development. " +
                    "Supply a base64-encoded 256-bit key from a secret store or an environment variable.");
            }
        }
        else if (!AttachmentKeyProvider.TryDecode(options.EncryptionKey, out _))
        {
            failures.Add(
                $"{SectionName}:{nameof(AttachmentOptions.EncryptionKey)} must be a base64-encoded 256-bit (32-byte) key.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private const string SectionName = AttachmentOptions.SectionName;
}
