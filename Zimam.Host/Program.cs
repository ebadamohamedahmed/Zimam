#pragma warning disable SYSLIB0014
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Web.WebView2.WinForms;
using Zimam.Host.Core;
using Zimam.Host.Hubs;

namespace Zimam.Host;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null); // Allow large video uploads
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = long.MaxValue);
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<StateEngine>();

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapHub<VsmHub>("/vsmhub");

        // FTP Stream Download: GET /api/hyperdeck/{deviceId}/download?slot=1&file=clip.mov
        app.MapGet("/api/hyperdeck/{deviceId}/download", async (string deviceId, string slot, string file, StateEngine eng, HttpContext ctx) =>
        {
            var cfg = eng.GetDeviceConfigs().FirstOrDefault(c => c.DeviceId == deviceId);
            if (cfg == null) return Results.NotFound("Device not found.");

            var req = (FtpWebRequest)WebRequest.Create($"ftp://{cfg.IpAddress}/{slot}/{Uri.EscapeDataString(file)}");
            req.Method = WebRequestMethods.Ftp.DownloadFile;
            req.Credentials = new NetworkCredential("anonymous", "zimam@local");
            req.UsePassive = true;

            var resp = (FtpWebResponse)await req.GetResponseAsync();
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{file}\"";
            return Results.Stream(resp.GetResponseStream(), "application/octet-stream");
        });

        // FTP Stream Upload: POST /api/hyperdeck/{deviceId}/upload?slot=1
        app.MapPost("/api/hyperdeck/{deviceId}/upload", async (string deviceId, string slot, StateEngine eng, HttpRequest request) =>
        {
            if (eng.SafeMode)
            {
                eng.LogAudit($"BLOCKED [SAFE MODE] -> {deviceId} | FTP Upload to Slot {slot}");
                return Results.Ok(new { success = false, message = "Blocked: Safe Mode (Monitor-Only) is active." });
            }
            var cfg = eng.GetDeviceConfigs().FirstOrDefault(c => c.DeviceId == deviceId);
            if (cfg == null) return Results.NotFound(new { success = false, message = "Device not found." });
            if (!request.HasFormContentType) return Results.BadRequest(new { success = false, message = "Multipart form required." });

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file == null || file.Length == 0) return Results.BadRequest(new { success = false, message = "No file selected." });

            try
            {
                var req = (FtpWebRequest)WebRequest.Create($"ftp://{cfg.IpAddress}/{slot}/{Uri.EscapeDataString(file.FileName)}");
                req.Method = WebRequestMethods.Ftp.UploadFile;
                req.Credentials = new NetworkCredential("anonymous", "zimam@local");
                req.UsePassive = true;
                req.ContentLength = file.Length;

                using (var ftpStream = await req.GetRequestStreamAsync())
                    await file.CopyToAsync(ftpStream);

                using var resp = (FtpWebResponse)await req.GetResponseAsync();
                return Results.Ok(new { success = true, message = $"Uploaded '{file.FileName}' to Slot {slot}." });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, message = $"Upload failed: {ex.Message}" });
            }
        });

        var engine = app.Services.GetRequiredService<StateEngine>();
        var pluginsPath = Path.Combine(AppContext.BaseDirectory, "Plugins");
        var configPath = Path.Combine(AppContext.BaseDirectory, "devices.json");
        _ = engine.InitializeAsync(pluginsPath, configPath, app.Lifetime.ApplicationStopping);

        _ = Task.Run(() => app.Run("http://0.0.0.0:5000"));

        var form = new Form
        {
            Text = "Zimam — Broadcast Control",
            Width = 1280,
            Height = 800,
            BackColor = Color.FromArgb(18, 18, 22), Icon = File.Exists("zimam.ico") ? new Icon("zimam.ico") : null
        };

        var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);
        form.Load += async (_, _) =>
        {
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Navigate("http://localhost:5000/index.html");
        };

        Application.Run(form);
    }
}


