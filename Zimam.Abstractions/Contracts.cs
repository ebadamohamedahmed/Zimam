namespace Zimam.Abstractions;

public enum ParamCategory { Monitor, Control, Management }
public enum AlarmLevel { Normal, Warning, Critical }

public record DeviceParameter(
    string DeviceId,
    string ParamId,
    string Value,
    DateTime LastUpdated,
    ParamCategory Category = ParamCategory.Control,
    AlarmLevel Alarm = AlarmLevel.Normal
);

public record ManagementResult(bool Success, string Message, object? Data = null);

public class DeviceConfig
{
    public string DeviceId { get; set; } = string.Empty;
    public string DriverType { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();
}

public interface IDeviceDriver
{
    string DriverType { get; }
    string DeviceId { get; }
    event Func<DeviceParameter, Task>? OnStateChanged;

    void Initialize(DeviceConfig config);
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync();
    Task SetParameterAsync(string paramId, string value);
    Task<ManagementResult> ExecuteManagementAsync(string action, Dictionary<string, string> args);
}
