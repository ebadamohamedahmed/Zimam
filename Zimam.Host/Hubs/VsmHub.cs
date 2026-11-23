using Microsoft.AspNetCore.SignalR;
using Zimam.Abstractions;
using Zimam.Host.Core;

namespace Zimam.Host.Hubs;

public class VsmHub : Hub
{
    private readonly StateEngine _engine;
    public VsmHub(StateEngine engine) => _engine = engine;

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("SafeModeChanged", _engine.SafeMode);
        await Clients.Caller.SendAsync("AvailableDrivers", _engine.GetAvailableDriverTypes());
        await Clients.Caller.SendAsync("DeviceConfigs", _engine.GetDeviceConfigs());
        await Clients.Caller.SendAsync("InitialState", _engine.GetAllStates());
        await base.OnConnectedAsync();
    }

    public async Task SetSafeMode(bool enabled)
        => await _engine.SetSafeModeAsync(enabled);

    public async Task SetValue(string deviceId, string paramId, string value)
        => await _engine.SendCommandAsync(deviceId, paramId, value);

    public async Task<ManagementResult> ExecuteManagement(string deviceId, string action, Dictionary<string, string> args)
        => await _engine.ExecuteManagementAsync(deviceId, action, args);

    public async Task SaveDevice(DeviceConfig config)
        => await _engine.AddOrUpdateDeviceAsync(config);

    public async Task DeleteDevice(string deviceId)
        => await _engine.RemoveDeviceAsync(deviceId);
}
