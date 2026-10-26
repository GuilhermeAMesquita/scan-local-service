using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

internal static class ScannerDashboard
{
    private const string LocalUrl = "http://127.0.0.1:4545";

    public static void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/ui") ||
                context.Request.Path.StartsWithSegments("/api/dashboard"))
            {
                var origin = context.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin) &&
                    origin != LocalUrl && origin != "http://localhost:4545")
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.XFrameOptions = "DENY";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
            }
            await next(context);
        });

        app.MapGet("/", () => Results.Redirect("/ui/status"));
        app.MapGet("/ui", () => Results.Redirect("/ui/status"));
        app.MapGet("/ui/{page}", (HttpContext context, string page) =>
        {
            if (page is not ("status" or "devices" or "logs"))
                return Results.NotFound();
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; connect-src 'self'; img-src 'self' data:; " +
                "style-src 'self'; script-src 'self'; base-uri 'none'; frame-ancestors 'none'";
            return Embedded("Ui.index.html", "text/html; charset=utf-8");
        });
        app.MapGet("/ui/app.css", () => Embedded("Ui.app.css", "text/css; charset=utf-8"));
        app.MapGet("/ui/app.js", () => Embedded("Ui.app.js", "text/javascript; charset=utf-8"));

        app.MapGet("/api/dashboard/status", () => Results.Ok(ScannerDiagnostics.Snapshot()));
        app.MapGet("/api/dashboard/logs", (int? lines) =>
        {
            int count = Math.Clamp(lines ?? 250, 1, 1000);
            var file = new FileInfo(ScannerDiagnostics.LogPath);
            return Results.Ok(new
            {
                lines = ReadLogTail(count),
                path = ScannerDiagnostics.LogPath,
                bytes = file.Exists ? file.Length : 0,
                updatedAt = file.Exists ? file.LastWriteTime : (DateTime?)null
            });
        });
        app.MapGet("/api/dashboard/logs/download", () =>
        {
            if (!File.Exists(ScannerDiagnostics.LogPath))
                return Results.NotFound();
            var stream = new FileStream(ScannerDiagnostics.LogPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Results.File(stream, "text/plain; charset=utf-8", "scanner-log.txt");
        });
        app.MapGet("/api/dashboard/devices", async () =>
        {
            try
            {
                var devices = await RunStaAsync(ReadDevices).WaitAsync(TimeSpan.FromSeconds(10));
                return Results.Ok(new { available = true, count = devices.Count, devices });
            }
            catch (Exception error)
            {
                return Results.Ok(new
                {
                    available = false,
                    count = 0,
                    devices = new List<DashboardDevice>(),
                    error = error.Message
                });
            }
        });
    }

    private static IResult Embedded(string name, string contentType)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return Results.NotFound();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Results.Content(reader.ReadToEnd(), contentType);
    }

    private static string[] ReadLogTail(int count)
    {
        if (!File.Exists(ScannerDiagnostics.LogPath)) return [];
        try
        {
            using var file = new FileStream(ScannerDiagnostics.LogPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, file.Length - 512 * 1024);
            file.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            string content = reader.ReadToEnd();
            if (start > 0)
            {
                int firstBreak = content.IndexOf('\n');
                content = firstBreak >= 0 ? content[(firstBreak + 1)..] : "";
            }
            return content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .TakeLast(count)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static List<DashboardDevice> ReadDevices()
    {
        var managerType = Type.GetTypeFromProgID("WIA.DeviceManager")
            ?? throw new InvalidOperationException("WIA não está disponível neste Windows.");
        dynamic manager = Activator.CreateInstance(managerType)!;
        var devices = new List<DashboardDevice>();
        int count = manager.DeviceInfos.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic info = manager.DeviceInfos.Item(i);
            var device = new DashboardDevice();
            try { device.Id = info.DeviceID?.ToString() ?? ""; } catch { }
            try { device.Type = (int)info.Type; } catch { }
            device.Properties = ReadProperties(info.Properties);
            device.Name = device.Properties.GetValueOrDefault("Name", $"Dispositivo {i}");
            try
            {
                dynamic connected = info.Connect();
                if (connected.Items.Count > 0)
                {
                    dynamic item = connected.Items[1];
                    device.ScanProperties = ReadProperties(item.Properties);
                    try { device.HorizontalDpi = Convert.ToInt32(item.Properties["6147"].Value); } catch { }
                    try { device.VerticalDpi = Convert.ToInt32(item.Properties["6148"].Value); } catch { }
                }
            }
            catch (Exception error)
            {
                device.DetailsError = error.Message;
            }
            devices.Add(device);
        }
        return devices;
    }

    private static Dictionary<string, string> ReadProperties(dynamic properties)
    {
        var result = new Dictionary<string, string>();
        try
        {
            foreach (dynamic property in properties)
            {
                try
                {
                    string name = property.Name?.ToString() ?? property.PropertyID?.ToString() ?? "Propriedade";
                    string value = property.Value?.ToString() ?? "";
                    if (name.Length > 100) name = name[..100];
                    if (value.Length > 400) value = value[..400];
                    result[name] = value;
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private sealed class DashboardDevice
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public int Type { get; set; }
        public int? HorizontalDpi { get; set; }
        public int? VerticalDpi { get; set; }
        public string? DetailsError { get; set; }
        public Dictionary<string, string> Properties { get; set; } = new();
        public Dictionary<string, string> ScanProperties { get; set; } = new();
    }
}
