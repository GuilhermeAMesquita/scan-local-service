using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

// ✅ Tray (bandeja)
using System.Drawing;
using System.Windows.Forms;

var builder = WebApplication.CreateBuilder(args);

QuestPDF.Settings.License = LicenseType.Community;

// =======================
// CORS (localhost + Firebase Hosting + domínio próprio)
// =======================
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFlutterWeb", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrWhiteSpace(origin)) return false;

                if (origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase)) return true;
                if (origin.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)) return true;

                if (origin.StartsWith("https://regulariza-farma-8fk4wt.web.app", StringComparison.OrdinalIgnoreCase)) return true;
                if (origin.StartsWith("https://regulariza-farma-8fk4wt.firebaseapp.com", StringComparison.OrdinalIgnoreCase)) return true;

                if (origin.StartsWith("https://regulariza-farma-test.web.app", StringComparison.OrdinalIgnoreCase)) return true;

                return false;
            })
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// ⚠️ UseCors tem que vir ANTES dos MapGet/MapPost
app.UseCors("AllowFlutterWeb");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// =======================
// LISTA DISPOSITIVOS WIA (late binding – SEM using WIA)
// =======================
app.MapGet("/devices", () =>
{
    try
    {
        dynamic manager = Activator.CreateInstance(
            Type.GetTypeFromProgID("WIA.DeviceManager")!
        );

        var devices = new List<object>();

        int count = manager.DeviceInfos.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic info = manager.DeviceInfos.Item(i);

            string id = "";
            string name = "";
            int type = 0;

            try { id = info.DeviceID?.ToString() ?? ""; } catch { }
            try { name = info.Properties["Name"].Value?.ToString() ?? ""; } catch { }
            try { type = (int)info.Type; } catch { }

            devices.Add(new { id, name, type });
        }

        return Results.Ok(devices);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString());
    }
});

// =======================
// SCAN + PDF
// - tenta SEM UI (mais rápido)
// - se falhar, fallback COM UI (compatibilidade)
// - você pode forçar UI: /scan/pdf?ui=1
// =======================
app.MapPost("/scan/pdf", async (HttpContext ctx) =>
{
    try
    {
        bool forceUi =
            ctx.Request.Query.TryGetValue("ui", out var v) &&
            (v.ToString() == "1" || v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));

        byte[] imageBytes = await RunStaAsync(() => AcquireImagePngBytes(forceUi));

        if (imageBytes.Length == 0)
            return Results.BadRequest("Scan cancelado.");

        byte[] pdfBytes = CreatePdf(imageBytes);

        return Results.File(
            pdfBytes,
            "application/pdf",
            $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
        );
    }
    catch (COMException ex)
    {
        return Results.Problem($"Erro COM/WIA: {ex.Message}");
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString());
    }
});

// =======================
// SCAN + PNG (para usar como "imagem" no Flutter)
// - tenta SEM UI
// - se falhar, fallback COM UI
// - pode forçar UI: /scan/png?ui=1
// =======================
app.MapPost("/scan/png", async (HttpContext ctx) =>
{
    try
    {
        bool forceUi =
            ctx.Request.Query.TryGetValue("ui", out var v) &&
            (v.ToString() == "1" || v.ToString().Equals("true", StringComparison.OrdinalIgnoreCase));

        byte[] imageBytes = await RunStaAsync(() => AcquireImagePngBytes(forceUi));

        if (imageBytes.Length == 0)
            return Results.BadRequest("Scan cancelado.");

        return Results.File(
            imageBytes,
            "image/png",
            $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        );
    }
    catch (COMException ex)
    {
        return Results.Problem($"Erro COM/WIA: {ex.Message}");
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.ToString());
    }
});

// ✅ Mantém EXATAMENTE o mesmo bind
app.Urls.Clear();
app.Urls.Add("http://127.0.0.1:4545");

// =======================
// INICIA SERVIDOR SEM CONSOLE + ENTRA EM MODO BANDEJA
// =======================

// 1) Sobe o servidor sem travar UI
await app.StartAsync();

// 2) Cria o ícone de bandeja (sem janela)
ApplicationConfiguration.Initialize();

var menu = new ContextMenuStrip();
menu.Items.Add("Testar /health", null, async (_, __) =>
{
    try
    {
        using var http = new System.Net.Http.HttpClient();
        var txt = await http.GetStringAsync("http://127.0.0.1:4545/health");
        MessageBox.Show(txt, "Scanner Connector");
    }
    catch (Exception ex)
    {
        MessageBox.Show(ex.Message, "Erro");
    }
});

menu.Items.Add("Ver versão", null, (_, __) =>
{
    var version = System.Reflection.Assembly
        .GetExecutingAssembly()
        .GetName()
        .Version?.ToString() ?? "N/A";

    MessageBox.Show($"Versão instalada: {version}", "Scanner Connector");
});

menu.Items.Add("Sair", null, async (_, __) =>
{
    try
    {
        await app.StopAsync();
    }
    catch { }
    Application.Exit();
});

var notify = new NotifyIcon
{
    Text = "Scanner Connector (Regulariza Farma)",
    Icon = SystemIcons.Application, // depois você troca por um .ico seu
    Visible = true,
    ContextMenuStrip = menu
};
notify.ShowBalloonTip(
    3000,
    "Regulariza Popular Escaneamento",
    "Funcionando em segundo plano",
    ToolTipIcon.Info
);

// Opcional: duplo clique abre o teste
notify.DoubleClick += async (_, __) =>
{
    try
    {
        using var http = new System.Net.Http.HttpClient();
        var txt = await http.GetStringAsync("http://127.0.0.1:4545/health");
        MessageBox.Show(txt, "Scanner Connector");
    }
    catch (Exception ex)
    {
        MessageBox.Show(ex.Message, "Erro");
    }
};

// 3) Segura o app vivo sem abrir janela
Application.ApplicationExit += async (_, __) =>
{
    try
    {
        await app.StopAsync();
    }
    catch { }
};
Application.Run();

// 4) Cleanup (quando sair)
notify.Visible = false;

// =======================
// MÉTODOS AUXILIARES
// =======================

static byte[] AcquireImagePngBytes(bool forceUi)
{
    const int ScannerDeviceType = 1; // WiaDeviceType.ScannerDeviceType
    const string WiaFormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}"; // wiaFormatPNG

    // Se pediu UI, vai direto no modo mais compatível
    if (forceUi)
        return AcquireWithUiDialog(WiaFormatPng);

    dynamic deviceManager = Activator.CreateInstance(
        Type.GetTypeFromProgID("WIA.DeviceManager")!
    );

    var candidates = new List<(dynamic info, string name)>();

    int count = deviceManager.DeviceInfos.Count;
    for (int i = 1; i <= count; i++)
    {
        dynamic info = deviceManager.DeviceInfos.Item(i);

        int type = 0;
        try { type = (int)info.Type; } catch { }

        if (type != ScannerDeviceType)
            continue;

        string name = "";
        try { name = info.Properties["Name"].Value?.ToString() ?? ""; } catch { }

        candidates.Add((info, name));
    }

    if (candidates.Count == 0)
        throw new Exception("Nenhum scanner encontrado.");

    // Prioridade: USB (se existir), senão primeiro
    var selected = candidates.FirstOrDefault(c =>
        (c.name ?? "").Contains("USB", StringComparison.OrdinalIgnoreCase)
    );

    dynamic selectedInfo = selected.info ?? candidates[0].info;
    dynamic selectedDevice = selectedInfo.Connect();

    // 1) tenta SILENT (sem UI)
    try
    {
        var bytes = AcquireSilent(selectedDevice, WiaFormatPng);
        if (bytes.Length > 0) return bytes;
    }
    catch
    {
        // cai pro fallback
    }

    // 2) fallback COM UI (mais compatível com drivers variados)
    return AcquireWithUiDialog(WiaFormatPng);
}

static byte[] AcquireSilent(dynamic device, string wiaFormatPng)
{
    // item 1 geralmente é o flatbed
    dynamic item = device.Items[1];

    // Não force ShowUI=false aqui: alguns drivers quebram.
    // No máximo desliga progresso (e ainda assim pode ser ignorado).
    TrySetPropertyDynamic(item.Properties, "ShowProgress", false);

    dynamic image = item.Transfer(wiaFormatPng);
    if (image == null) return Array.Empty<byte>();

    return (byte[])image.FileData.BinaryData;
}

static byte[] AcquireWithUiDialog(string wiaFormatPng)
{
    dynamic dialog = Activator.CreateInstance(
        Type.GetTypeFromProgID("WIA.CommonDialog")!
    );

    const int ScannerDeviceType = 1;
    const int ColorIntent = 1;
    const int MaxQuality = 2;

    dynamic image = dialog.ShowAcquireImage(
        ScannerDeviceType,
        ColorIntent,
        MaxQuality,
        wiaFormatPng,
        false, // AlwaysSelectDevice (driver pode mostrar mesmo assim)
        true,  // UseCommonUI
        false  // CancelError
    );

    if (image == null) return Array.Empty<byte>();
    return (byte[])image.FileData.BinaryData;
}

static void TrySetPropertyDynamic(dynamic props, string name, object value)
{
    try
    {
        foreach (dynamic p in props)
        {
            try
            {
                string propName = p.Name?.ToString() ?? "";
                if (string.Equals(propName, name, StringComparison.OrdinalIgnoreCase))
                {
                    p.set_Value(value);
                    return;
                }
            }
            catch { }
        }
    }
    catch { }
}

static byte[] CreatePdf(byte[] imageBytes)
{
    var doc = Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(20);
            page.Content().Image(imageBytes).FitArea();
        });
    });

    return doc.GeneratePdf();
}

static Task<T> RunStaAsync<T>(Func<T> func)
{
    var tcs = new TaskCompletionSource<T>();

    var thread = new Thread(() =>
    {
        try
        {
            tcs.SetResult(func());
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();

    return tcs.Task;
}