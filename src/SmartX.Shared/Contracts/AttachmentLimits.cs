namespace SmartX.Shared;

/// <summary>What the server will accept, so the client can check a file before uploading it.</summary>
/// <param name="MaxFileBytes">The largest file the server will store.</param>
/// <param name="AllowedExtensions">Extensions the server permits.</param>
/// <remarks>
/// Served by the API rather than configured separately in the client, so the two can never disagree
/// about the size limit. If they could, the client would either wave through files the server refuses
/// or refuse files the server would take.
/// </remarks>
public sealed record AttachmentLimits(long MaxFileBytes, IReadOnlyList<string> AllowedExtensions);
