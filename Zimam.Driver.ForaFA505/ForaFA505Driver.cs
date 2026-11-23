using System.Net.Sockets;
using System.Text;
using Zimam.Abstractions;

namespace Zimam.Driver.ForaFA505;

public class ForaFA505Driver : IDeviceDriver
{
    public string DriverType => "FOR-A_FA505";
    public string DeviceId { get; private set; } = string.Empty;

    private string _ip = string.Empty;
    private int _port = 50011;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;

    public event Func<DeviceParameter, Task>? OnStateChanged;

    private static readonly Dictionary<string, string> CommandMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VIDEO_LEVEL"] = "VID_LVL",
        ["BLACK_LEVEL"] = "BLK_LVL",
        ["CHROMA"]      = "CHR_LVL",
        ["HUE"]         = "HUE",
        ["R_GAIN"]      = "CC_W_R",
        ["G_GAIN"]      = "CC_W_G",
        ["B_GAIN"]      = "CC_W_B",
        ["R_BLACK"]     = "CC_B_R",
        ["G_BLACK"]     = "CC_B_G",
        ["B_BLACK"]     = "CC_B_B",
        ["CC_BYPASS"]   = "CC_ENA"
    };

    public void Initialize(DeviceConfig config)
    {
        DeviceId = config.DeviceId;
        _ip = config.IpAddress;
        _port = config.Port > 0 ? config.Port : 50011;
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => MaintainConnectionAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        _client?.Close();
        _client?.Dispose();
        await EmitAsync("CONNECTION", "OFFLINE", ParamCategory.Monitor, AlarmLevel.Critical);
    }

    private async Task MaintainConnectionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(_ip, _port, ct);
                var stream = _client.GetStream();
                _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                _reader = new StreamReader(stream, Encoding.ASCII);

                await EmitAsync("CONNECTION", "ONLINE", ParamCategory.Monitor, AlarmLevel.Normal);
                await ReadLoopAsync(ct);
            }
            catch
            {
                await EmitAsync("CONNECTION", "OFFLINE", ParamCategory.Monitor, AlarmLevel.Critical);
                _client?.Dispose();
                if (!ct.IsCancellationRequested)
                    await Task.Delay(3000, ct).ContinueWith(_ => { });
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (_reader != null && !ct.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line == null) break;

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            await EmitAsync("LAST_RX", line, ParamCategory.Monitor);
            var sep = line.Contains('=') ? '=' : (line.Contains(':') ? ':' : '\0');
            if (sep != '\0')
            {
                var parts = line.Split(sep, 2);
                if (parts.Length == 2)
                    await EmitAsync(parts[0].Trim().ToUpper(), parts[1].Trim(), ParamCategory.Monitor);
            }
        }
    }

    public async Task SetParameterAsync(string paramId, string value)
    {
        var parts = paramId.Split(':');
        if (parts.Length == 2 && CommandMap.TryGetValue(parts[1], out var cmdKey))
        {
            var fsChannel = parts[0].ToUpper().Replace("CH", "FS");
            var command = $"{fsChannel}_{cmdKey}={value}";
            await SendRawAsync(command);
            await EmitAsync(paramId.ToUpper(), value, ParamCategory.Control);
        }
    }

    public async Task<ManagementResult> ExecuteManagementAsync(string action, Dictionary<string, string> args)
    {
        if (_writer == null)
            return new ManagementResult(false, "Device is offline.");

        switch (action.ToUpper())
        {
            case "RAW":
                var cmd = args.GetValueOrDefault("command", "");
                if (string.IsNullOrWhiteSpace(cmd)) return new ManagementResult(false, "Empty command.");
                await SendRawAsync(cmd);
                return new ManagementResult(true, $"Sent raw command: {cmd}");

            case "RECALL_PRESET":
                var preset = args.GetValueOrDefault("preset", "1");
                await SendRawAsync($"EVENT_LOAD={preset}");
                return new ManagementResult(true, $"Recalled Preset {preset}");

            default:
                return new ManagementResult(false, $"Unknown management action: {action}");
        }
    }

    private async Task SendRawAsync(string command)
    {
        if (_writer != null)
        {
            await _writer.WriteLineAsync(command);
            await EmitAsync("LAST_TX", command, ParamCategory.Management);
        }
    }

    private async Task EmitAsync(string paramId, string value, ParamCategory cat = ParamCategory.Control, AlarmLevel alarm = AlarmLevel.Normal)
    {
        if (OnStateChanged != null)
            await OnStateChanged.Invoke(new DeviceParameter(DeviceId, paramId, value, DateTime.UtcNow, cat, alarm));
    }
}
