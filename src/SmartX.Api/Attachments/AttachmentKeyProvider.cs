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
/// without anyone committing a key to make it so. It is coherent rather than merely convenient: attachment
/// metadata is held in memory, so after a restart the old files are unreachable anyway, and a key that dies
/// with the process loses nothing that was still addressable. Outside Development,
/// <see cref="AttachmentOptionsValidator"/> refuses to start without a real key.
/// </para>
/// </remarks>
internal sealed class AttachmentKeyProvider
{
    private readonly byte[] _key;

    public AttachmentKeyProvider(IOptions<AttachmentOptions> options, ILogger<AttachmentKeyProvider> logger)
    {
        if (TryDecode(options.Value.EncryptionKey, out var key))
        {
            _key = key;
            return;
        }

        _key = RandomNumberGenerator.GetBytes(32);

        logger.LogWarning(
            "No {Setting} is configured, so attachments are being encrypted with a temporary key that will be " +
            "lost when the API stops. Set a key with 'dotnet user-secrets set \"{Setting}\" <base64 key>' to keep " +
            "attachments readable across restarts.",
            "Attachments:EncryptionKey",
            "Attachments:EncryptionKey");
    }

    /// <summary>The 256-bit key.</summary>
    public ReadOnlySpan<byte> Key => _key;

    /// <summary>The key as memory, for code that has to hold it across an await.</summary>
    public ReadOnlyMemory<byte> KeyMemory => _key;

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
}
