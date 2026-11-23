#pragma warning disable SYSLIB0014
using System.Net;
using System.Net.Sockets;
using System.Text;
using Zimam.Abstractions;

namespace Zimam.Driver.HyperDeck;

public class HyperDeckDriver : IDeviceDriver
{
    public string DriverType => "HyperDeck";
    public string DeviceId { get; private set; } = string.Empty;
    private string _ip = string.Empty;
    private int _port = 9993;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource? _cts;

    public event Func<DeviceParameter, Task>? OnStateChanged;

    public void Initialize(DeviceConfig config)
    {
        DeviceId = config.DeviceId;
        _ip = config.IpAddress;
        _port = config.Port > 0 ? config.Port : 9993;
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
                await SendRawAsync("notify: transport: true");
                await SendRawAsync("notify: slot: true");
                await SendRawAsync("transport info");
                await SendRawAsync("slot info: slot id: 1");

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
        while (_reader != null && !ct.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(ct);
            if (line == null) break;

            if (line.StartsWith("status: "))
            {
                var val = line[8..].Trim().ToUpper();
                await EmitAsync("STATUS", val, ParamCategory.Control);
                await EmitAsync("TRANSPORT_STATE", val, ParamCategory.Monitor);
            }
            else if (line.StartsWith("timecode: "))
                await EmitAsync("TIMECODE", line[10..].Trim(), ParamCategory.Monitor);
            else if (line.StartsWith("clip id: "))
                await EmitAsync("CLIP_ID", line[9..].Trim(), ParamCategory.Monitor);
            else if (line.StartsWith("slot id: "))
                await EmitAsync("SLOT_ID", line[9..].Trim(), ParamCategory.Monitor);
            else if (line.StartsWith("recording time: "))
            {
                var secStr = line[16..].Trim();
                var alarm = int.TryParse(secStr, out var s) && s < 300 ? AlarmLevel.Warning : AlarmLevel.Normal;
                await EmitAsync("REC_TIME_REMAINING", $"{secStr}s", ParamCategory.Monitor, alarm);
            }
            else if (line.StartsWith("1"))
                await EmitAsync("LAST_ERROR", line.Trim(), ParamCategory.Monitor, AlarmLevel.Warning);
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(10000, ct).ContinueWith(_ => { });
            if (!ct.IsCancellationRequested) await SendRawAsync("ping");
        }
    }

    public async Task SetParameterAsync(string paramId, string value)
    {
        if (paramId != "TRANSPORT") return;
        string? cmd = value.ToUpper() switch
        {
            "PLAY"   => "play",
            "STOP"   => "stop",
            "RECORD" => "record",
            "PREV"   => "goto: clip id: -1",
            "NEXT"   => "goto: clip id: +1",
            "SLOT_1" => "slot select: slot id: 1",
            "SLOT_2" => "slot select: slot id: 2",
            _ => null
        };
        if (cmd != null) await SendRawAsync(cmd);
    }

    public async Task<ManagementResult> ExecuteManagementAsync(string action, Dictionary<string, string> args)
    {
        switch (action.ToUpper())
        {
            case "FTP_LIST":
                return await ListFtpClipsAsync(args.GetValueOrDefault("slot", "1"));

            case "FTP_RENAME":
                return await RenameFtpClipAsync(
                    args.GetValueOrDefault("slot", "1"),
                    args.GetValueOrDefault("oldName", ""),
                    args.GetValueOrDefault("newName", ""));

            case "REFRESH_INFO":
                if (_writer == null) return new ManagementResult(false, "Device is offline.");
                await SendRawAsync("transport info");
                await SendRawAsync("slot info: slot id: 1");
                return new ManagementResult(true, "Requested transport & slot refresh.");

            case "RAW":
                if (_writer == null) return new ManagementResult(false, "Device is offline.");
                var cmd = args.GetValueOrDefault("command", "");
                if (string.IsNullOrWhiteSpace(cmd)) return new ManagementResult(false, "Empty command.");
                await SendRawAsync(cmd);
                return new ManagementResult(true, $"Sent: {cmd}");

            default:
                return new ManagementResult(false, $"Unknown management action: {action}");
        }
    }

    private async Task<ManagementResult> ListFtpClipsAsync(string slot)
    {
        try
        {
            var req = (FtpWebRequest)WebRequest.Create($"ftp://{_ip}/{slot}/");
            req.Method = WebRequestMethods.Ftp.ListDirectory;
            req.Credentials = new NetworkCredential("anonymous", "zimam@local");
            req.UsePassive = true;
            req.Timeout = 5000;

            var files = new List<string>();
            using var resp = (FtpWebResponse)await req.GetResponseAsync();
            using var reader = new StreamReader(resp.GetResponseStream());
            while (!reader.EndOfStream)
            {
                var line = (await reader.ReadLineAsync())?.Trim();
                if (!string.IsNullOrEmpty(line) && line != "." && line != "..")
                    files.Add(Path.GetFileName(line));
            }
            return new ManagementResult(true, $"Found {files.Count} clip(s) on Slot {slot}.", files);
        }
        catch (Exception ex)
        {
            return new ManagementResult(false, $"FTP list failed: {ex.Message}");
        }
    }

    private async Task<ManagementResult> RenameFtpClipAsync(string slot, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return new ManagementResult(false, "Both old and new filenames are required.");

        try
        {
            var req = (FtpWebRequest)WebRequest.Create($"ftp://{_ip}/{slot}/{Uri.EscapeDataString(oldName)}");
            req.Method = WebRequestMethods.Ftp.Rename;
            req.RenameTo = newName;
            req.Credentials = new NetworkCredential("anonymous", "zimam@local");
            req.UsePassive = true;
            req.Timeout = 5000;

            using var resp = (FtpWebResponse)await req.GetResponseAsync();
            return new ManagementResult(true, $"Renamed '{oldName}' to '{newName}'.");
        }
        catch (Exception ex)
        {
            return new ManagementResult(false, $"FTP rename failed: {ex.Message}");
        }
    }

    private async Task SendRawAsync(string command)
    {
        if (_writer != null) await _writer.WriteLineAsync(command);
    }

    private async Task EmitAsync(string paramId, string value, ParamCategory cat = ParamCategory.Control, AlarmLevel alarm = AlarmLevel.Normal)
    {
        if (OnStateChanged != null)
            await OnStateChanged.Invoke(new DeviceParameter(DeviceId, paramId, value, DateTime.UtcNow, cat, alarm));
    }
}
