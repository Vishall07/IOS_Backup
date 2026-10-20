namespace IosBackupApp.Models;

public class DeviceInfoModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public int? BatteryLevel { get; set; }
    public bool IsSimulated { get; set; }
    public bool IsConnected { get; set; }

    public string DisplayName => IsSimulated 
        ? $"[Demo Mode] {Name} ({Model})"
        : $"{Name} ({Model}) - {(BatteryLevel.HasValue ? $"{BatteryLevel}% Battery" : "Connected")}";

    public override string ToString() => DisplayName;
}
