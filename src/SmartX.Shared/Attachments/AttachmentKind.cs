using System.Text.Json.Serialization;

namespace SmartX.Shared;

/// <summary>What an attached file is for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttachmentKind>))]
public enum AttachmentKind
{
    /// <summary>A device configuration file: YAML, JSON, INI and the like.</summary>
    ConfigurationFile,

    /// <summary>A photograph of the installed device. Must be an image.</summary>
    DeploymentPhoto,

    /// <summary>A log pulled off the hardware.</summary>
    HardwareLog
}
