using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace IosBackupApp.Models;

/// <summary>
/// Unified JSON Schema representing device state or local backup state.
/// </summary>
public class DeviceState
{
    [JsonPropertyName("DeviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("ScanDate")]
    public DateTime ScanDate { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("Root")]
    public MediaItemNode Root { get; set; } = new MediaItemNode { Name = "DCIM", IsDirectory = true };
}

/// <summary>
/// Represents a file or folder node in the unified JSON schema.
/// </summary>
public class MediaItemNode
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("IsDirectory")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("Size")]
    public long Size { get; set; }

    [JsonPropertyName("Hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }

    [JsonPropertyName("RelativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("Children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MediaItemNode>? Children { get; set; }
}
