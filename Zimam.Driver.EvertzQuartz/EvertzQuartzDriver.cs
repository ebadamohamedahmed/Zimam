using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Zimam.Abstractions;

namespace Zimam.Driver.EvertzQuartz;

public class EvertzQuartzDriver : IDeviceDriver
{
    public string DriverType => "Evertz_Quartz";
    public string DeviceId { get; private set; } = string.Empty;

    private string _ip = string.Empty;
    private int _port = 4000; // Common Evertz Quartz TCP port (can also be 23)
    private TcpClient? _client;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;

    // Matches Quartz feedback like ".AV1,5" or ".UV12,3" or ".AVA1,4"
    private static readonly Regex RouteRegex = new(@"^\.[AU]([A-Z]+)(\d+),(\d+)$", RegexOptions.Compiled);

    public event Func<DeviceParameter, Task>? OnStateChanged;

    public void Initialize(DeviceConfig config)
    {
        DeviceId = config.DeviceId;
        _ip = config.IpAddress;
        _port = config.Port > 0 ? config.Port : 4000;
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
                // Quartz ASCII protocol uses CR (\r) as command terminator
                _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r" };
                _reader = new StreamReader(stream, Encoding.ASCII);

                await EmitAsync("CONNECTION", "ONLINE", ParamCategory.Monitor, AlarmLevel.Normal);

                // Query Destination 1 on connect to verify two-way communication
                await SendRawAsync(".IV1");

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _ = KeepAliveLoopAsync(pingCts.Token);

                await ReadLoopAsync(ct);
                pingCts.Cancel();
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
        var buffer = new char[1024];
        var sb = new StringBuilder();

        while (_reader != null && !ct.IsCancellationRequested)
        {
            var count = await _reader.ReadAsync(buffer, ct);
            if (count == 0) break;

            for (int i = 0; i < count; i++)
            {
                char c = buffer[i];
                if (c == '\r' || c == '\n')
                {
                    var line = sb.ToString().Trim();
                    sb.Clear();
                    if (!string.IsNullOrEmpty(line))
                        await ProcessLineAsync(line);
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
    }

    private async Task ProcessLineAsync(string line)
    {
        await EmitAsync("LAST_RX", line, ParamCategory.Monitor);

        if (line.StartsWith(".E"))
        {
            await EmitAsync("LAST_ERROR", line, ParamCategory.Monitor, AlarmLevel.Warning);
            return;
        }

        var match = RouteRegex.Match(line);
        if (match.Success)
        {
            var levels = match.Groups[1].Value;
            var dest = match.Groups[2].Value;
            var src = match.Groups[3].Value;
            await EmitAsync($"DEST_{dest}_{levels}", src, ParamCategory.Monitor);
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(15000, ct).ContinueWith(_ => { });
            if (!ct.IsCancellationRequested) await SendRawAsync(".#01");
        }
    }

    /// <summary>
    /// Supports:
    /// - paramId: "ROUTE", value: "V:1,5" (Level:Dest,Src) -> .SV1,5
    /// - paramId: "DEST_1", value: "5" (defaults to Level V) -> .SV1,5
    /// </summary>
    public async Task SetParameterAsync(string paramId, string value)
    {
        if (paramId.Equals("ROUTE", StringComparison.OrdinalIgnoreCase))
        {
            var parts = value.Split(':');
            var level = parts.Length == 2 ? parts[0].ToUpper() : "V";
            var pair = parts.Length == 2 ? parts[1] : parts[0];
            await SendRawAsync($".S{level}{pair}");
        }
        else if (paramId.StartsWith("DEST_", StringComparison.OrdinalIgnoreCase))
        {
            var dest = paramId[5..];
            await SendRawAsync($".SV{dest},{value}");
        }
    }

    public async Task<ManagementResult> ExecuteManagementAsync(string action, Dictionary<string, string> args)
    {
        if (_writer == null)
            return new ManagementResult(false, "Router is offline.");

        switch (action.ToUpper())
        {
            case "SALVO":
                var salvo = args.GetValueOrDefault("salvo", "1");
                await SendRawAsync($".F{salvo}");
                return new ManagementResult(true, $"Fired Salvo {salvo} (.F{salvo})");

            case "QUERY_DEST":
                var qDest = args.GetValueOrDefault("dest", "1");
                var qLevel = args.GetValueOrDefault("level", "V").ToUpper();
                await SendRawAsync($".I{qLevel}{qDest}");
                return new ManagementResult(true, $"Queried Dest {qDest} Level {qLevel}");

            case "LOCK":
                var lDest = args.GetValueOrDefault("dest", "1");
                await SendRawAsync($".L{lDest},1");
                return new ManagementResult(true, $"Locked Dest {lDest}");

            case "UNLOCK":
                var uDest = args.GetValueOrDefault("dest", "1");
                await SendRawAsync($".L{uDest},0");
                return new ManagementResult(true, $"Unlocked Dest {uDest}");

            case "RAW":
                var cmd = args.GetValueOrDefault("command", "");
                if (string.IsNullOrWhiteSpace(cmd)) return new ManagementResult(false, "Empty command.");
                await SendRawAsync(cmd);
                return new ManagementResult(true, $"Sent: {cmd}");

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
