using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Zimam.Abstractions;
using Zimam.Host.Hubs;

namespace Zimam.Host.Core;

public class StateEngine
{
    private readonly ConcurrentDictionary<string, DeviceParameter> _state = new();
    private readonly ConcurrentDictionary<string, IDeviceDriver> _drivers = new();
    private readonly ConcurrentDictionary<string, DeviceConfig> _configs = new();
    private readonly object _logLock = new();
    private readonly string _auditLogPath = Path.Combine(AppContext.BaseDirectory, "tx_audit.log");
    private static readonly HashSet<string> ReadOnlyMgmtActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "REFRESH_INFO", "FTP_LIST", "QUERY_DEST"
    };

    private Dictionary<string, Type> _availableDrivers = new();
    private string _configPath = string.Empty;
    private CancellationToken _appToken;
    private readonly IHubContext<VsmHub> _hubContext;

    public bool SafeMode { get; private set; } = true; // Starts locked (Monitor-Only)

    public StateEngine(IHubContext<VsmHub> hubContext) => _hubContext = hubContext;

    public void LogAudit(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        lock (_logLock)
        {
            try { File.AppendAllText(_auditLogPath, line + Environment.NewLine); } catch { }
        }
    }

    public async Task SetSafeModeAsync(bool enabled)
    {
        SafeMode = enabled;
        LogAudit($"SYSTEM -> SafeMode set to {(enabled ? "ENABLED (Monitor-Only)" : "DISABLED (Live Control)")}");
        await _hubContext.Clients.All.SendAsync("SafeModeChanged", SafeMode);
    }

    public async Task InitializeAsync(string pluginsPath, string configPath, CancellationToken appToken)
    {
        _configPath = configPath;
        _appToken = appToken;
        _availableDrivers = PluginLoader.DiscoverDrivers(pluginsPath);
        LogAudit("SYSTEM -> Zimam Engine started (SafeMode: ENABLED)");

        if (File.Exists(_configPath))
        {
            var list = JsonSerializer.Deserialize<List<DeviceConfig>>(File.ReadAllText(_configPath)) ?? new();
            foreach (var cfg in list)
                await StartDeviceAsync(cfg);
        }
    }

    public async Task AddOrUpdateDeviceAsync(DeviceConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DeviceId)) return;
        await RemoveDeviceInternalAsync(cfg.DeviceId);
        await StartDeviceAsync(cfg);
        SaveConfigs();
        LogAudit($"CONFIG -> Saved device {cfg.DeviceId} ({cfg.DriverType} @ {cfg.IpAddress}:{cfg.Port})");
        await BroadcastDevicesAsync();
    }

    public async Task RemoveDeviceAsync(string deviceId)
    {
        await RemoveDeviceInternalAsync(deviceId);
        SaveConfigs();
        LogAudit($"CONFIG -> Removed device {deviceId}");
        await BroadcastDevicesAsync();
    }

    private async Task StartDeviceAsync(DeviceConfig cfg)
    {
        if (_availableDrivers.TryGetValue(cfg.DriverType, out var driverType) &&
            Activator.CreateInstance(driverType) is IDeviceDriver driver)
        {
            driver.Initialize(cfg);
            _configs[cfg.DeviceId] = cfg;
            _drivers[cfg.DeviceId] = driver;
            driver.OnStateChanged += async param => await UpdateStateAsync(param);
            await driver.ConnectAsync(_appToken);
        }
    }

    private async Task RemoveDeviceInternalAsync(string deviceId)
    {
        if (_drivers.TryRemove(deviceId, out var oldDriver))
            await oldDriver.DisconnectAsync();

        _configs.TryRemove(deviceId, out _);
        foreach (var key in _state.Keys.Where(k => k.StartsWith($"{deviceId}:")).ToList())
            _state.TryRemove(key, out _);
    }

    private void SaveConfigs()
    {
        var json = JsonSerializer.Serialize(_configs.Values.OrderBy(c => c.DeviceId), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    private async Task BroadcastDevicesAsync()
    {
        await _hubContext.Clients.All.SendAsync("DeviceConfigs", GetDeviceConfigs());
        await _hubContext.Clients.All.SendAsync("InitialState", GetAllStates());
    }

    public async Task UpdateStateAsync(DeviceParameter param)
    {
        _state[$"{param.DeviceId}:{param.ParamId}"] = param;
        await _hubContext.Clients.All.SendAsync("ParameterUpdated", param);
    }

    public async Task SendCommandAsync(string deviceId, string paramId, string value)
    {
        if (SafeMode)
        {
            LogAudit($"BLOCKED [SAFE MODE] -> {deviceId} | SetParameter {paramId}={value}");
            return;
        }

        if (_drivers.TryGetValue(deviceId, out var driver))
        {
            LogAudit($"TX -> {deviceId} | SetParameter {paramId}={value}");
            await driver.SetParameterAsync(paramId, value);
        }
    }

    public async Task<ManagementResult> ExecuteManagementAsync(string deviceId, string action, Dictionary<string, string> args)
    {
        if (SafeMode && !ReadOnlyMgmtActions.Contains(action))
        {
            LogAudit($"BLOCKED [SAFE MODE] -> {deviceId} | Management {action}");
            return new ManagementResult(false, "Blocked: Safe Mode (Monitor-Only) is active.");
        }

        if (_drivers.TryGetValue(deviceId, out var driver))
        {
            LogAudit($"MGMT -> {deviceId} | {action} {JsonSerializer.Serialize(args ?? new())}");
            return await driver.ExecuteManagementAsync(action, args ?? new());
        }
        return new ManagementResult(false, $"Device {deviceId} not found.");
    }

    public IEnumerable<DeviceParameter> GetAllStates() => _state.Values;
    public IEnumerable<DeviceConfig> GetDeviceConfigs() => _configs.Values.OrderBy(c => c.DeviceId);
    public IEnumerable<string> GetAvailableDriverTypes() => _availableDrivers.Keys.OrderBy(k => k);
}
