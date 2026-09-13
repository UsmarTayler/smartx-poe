using System.Buffers;
using System.Text;

namespace SmartX.Api.Attachments;

/// <summary>
/// Turns an uploaded file name into something safe to display and to suggest on download.
/// </summary>
/// <remarks>
/// <b>This is not what keeps the disk safe.</b> Stored files are named from a generated GUID, and the
/// uploaded name is never used to build a path. A name such as <c>..\..\Windows\System32\drivers</c> is a
/// perfectly valid string, and any code that combined it with a directory would write wherever it
/// pointed. Generating the stored name removes that entire class of bug instead of trying to out-guess
/// every separator, encoding and device name an attacker might use.
/// <para>
/// The name is still sanitised because it is shown in the UI and sent back in a Content-Disposition
/// header, where a stray control character, a path, or a Windows device name would cause trouble of its
/// own. The rules are written out explicitly rather than taken from
/// <see cref="Path.GetInvalidFileNameChars"/>, because that list depends on the operating system: on
/// Linux it contains only '/' and NUL, so a name that is safe on a Linux gateway could still be a
/// problem for the Windows machine that downloads it.
/// </para>
/// </remarks>
internal static class FileNameSanitizer
{
    /// <summary>The name used when nothing usable survives sanitising.</summary>
    public const string Fallback = "attachment";

    private const int MaxLength = 128;

    private static readonly SearchValues<char> Forbidden = SearchValues.Create("<>:\"/\\|?*");

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Fallback;
        }

        // Keep only the last path segment, splitting on both separators. Some browsers send a full client
        // path such as "C:\fakepath\photo.png", and an attacker can send anything at all.
        var lastSeparator = fileName.AsSpan().LastIndexOfAny('/', '\\');
        var segment = lastSeparator >= 0 ? fileName[(lastSeparator + 1)..] : fileName;

        var builder = new StringBuilder(segment.Length);

        foreach (var character in segment)
        {
            builder.Append(char.IsControl(character) || Forbidden.Contains(character) ? '_' : character);
        }

        // Leading dots would make a hidden file or a relative reference; trailing dots and spaces are
        // silently stripped by Windows, so "report.txt." and "report.txt" would collide there.
        var cleaned = builder.ToString().Trim().Trim('.').Trim();

        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        if (cleaned.Length == 0)
        {
            return Fallback;
        }

        // A Windows device name is reserved with or without an extension: "con.txt" cannot be saved.
        var stem = Path.GetFileNameWithoutExtension(cleaned);

        if (ReservedDeviceNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        return Truncate(cleaned);
    }

    /// <summary>Shortens a long name from the middle of the stem, keeping the extension intact.</summary>
    private static string Truncate(string name)
    {
        if (name.Length <= MaxLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);

        if (extension.Length > 16)
        {
            extension = string.Empty;
        }

        return name[..(MaxLength - extension.Length)] + extension;
    }
}
