using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace SmartX.Api.Attachments;

/// <summary>
/// Supplies the key attachments are encrypted with.
/// </summary>
/// <remarks>
/// The single place the key is read, so that moving it into a key vault later means changing this class
/// and nothing that encrypts or decrypts.
/// <para>
/// <b>Development fallback.</b> With no key configured in Development, a random key is generated for the
/// life of the process and a warning is logged. That keeps <c>dotnet run</c> working on a fresh clone
/// without anyone committing a key to make it so — but attachments uploaded under it cannot be read after a
/// restart, which is why the README's first-run steps set the published development key instead. Outside
/// Development, <see cref="AttachmentOptionsValidator"/> refuses to start without a real key.
/// </para>
/// </remarks>
internal sealed class AttachmentKeyProvider
{
    /// <summary>
    /// The <see cref="KeyId"/> of the development key published in the README and <c>.env.example</c>.
    /// </summary>
    /// <remarks>
    /// A fingerprint, not the key. It lets the API recognise that key and warn when it is used outside
    /// Development, where anyone who has read the repository could decrypt what it protects. The key itself
    /// stays out of source, as every key does.
    /// </remarks>
    private const string PublishedDevelopmentKeyId = "275a71a038e62986";

    private readonly byte[] _key;

    public AttachmentKeyProvider(
        IOptions<AttachmentOptions> options,
        IHostEnvironment environment,
        ILogger<AttachmentKeyProvider> logger)
    {
        if (TryDecode(options.Value.EncryptionKey, out var key))
        {
            _key = key;
            KeyId = IdOf(key);

            if (KeyId == PublishedDevelopmentKeyId && !environment.IsDevelopment())
            {
                logger.LogWarning(
                    "Attachments are encrypted with the development key published in the README. That is fine for " +
                    "a local Docker demo, but anyone with the repository can decrypt them. Generate a private key " +
                    "for any other deployment.");
            }

            return;
        }

        _key = RandomNumberGenerator.GetBytes(32);
        KeyId = IdOf(_key);

        logger.LogWarning(
            "No {Setting} is configured, so attachments are being encrypted with a temporary key and will be " +
            "unreadable after the API restarts. Set the development key from the README with " +
            "'dotnet user-secrets set \"{Setting}\" <key> --project src/SmartX.Api'.",
            "Attachments:EncryptionKey",
            "Attachments:EncryptionKey");
    }

    /// <summary>The 256-bit key.</summary>
    public ReadOnlySpan<byte> Key => _key;

    /// <summary>The key as memory, for code that has to hold it across an await.</summary>
    public ReadOnlyMemory<byte> KeyMemory => _key;

    /// <summary>A short fingerprint of the key, recorded with each attachment.</summary>
    /// <remarks>
    /// The first 64 bits of the key's SHA-256. A hash of a random 256-bit key reveals nothing usable about
    /// the key, and it lets the attachment index tell which files a changed key can no longer open.
    /// </remarks>
    public string KeyId { get; }

    public static bool TryDecode(string? base64, out byte[] key)
    {
        key = [];

        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        try
        {
            key = Convert.FromBase64String(base64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return key.Length == 32;
    }

    private static string IdOf(byte[] key) => Convert.ToHexStringLower(SHA256.HashData(key).AsSpan(0, 8));
}
