namespace SmartX.Shared;

/// <summary>
/// Which files may be attached, shared by the client's instant checks and the server's enforcement.
/// </summary>
/// <remarks>
/// The client uses this to fail an obviously wrong file immediately, before a large upload begins.
/// That is purely for responsiveness. The server applies the same list, and then goes further by
/// inspecting the bytes themselves — a client can be modified, bypassed or simply lie, so nothing the
/// client checks is ever trusted.
/// </remarks>
public static class AttachmentPolicy
{
    /// <summary>File extensions whose content is an image, and the type each must sniff as.</summary>
    public static IReadOnlyDictionary<string, string> ImageExtensions { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
        };

    /// <summary>File extensions whose content is text, and the content type each is served as.</summary>
    /// <remarks>
    /// Configuration formats beyond the brief's JSON and YAML — .conf, .cfg, .ini — are included
    /// because they are what device configuration files are actually named. They are served as plain
    /// text. Nothing that a browser would execute or render as a document, such as .html or .svg, is
    /// on either list.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> TextExtensions { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = "text/plain",
            [".log"] = "text/plain",
            [".conf"] = "text/plain",
            [".cfg"] = "text/plain",
            [".ini"] = "text/plain",
            [".json"] = "application/json",
            [".yaml"] = "application/yaml",
            [".yml"] = "application/yaml",
            [".csv"] = "text/csv",
        };

    /// <summary>Every permitted extension, for display and for a file picker's accept list.</summary>
    public static IReadOnlyList<string> AllowedExtensions { get; } =
        [.. ImageExtensions.Keys, .. TextExtensions.Keys];

    /// <summary>Whether a file name ends in a permitted extension.</summary>
    public static bool IsAllowedFileName(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);

        return ImageExtensions.ContainsKey(extension) || TextExtensions.ContainsKey(extension);
    }
}
