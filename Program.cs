using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
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
                if (origin.StartsWith("https://www.regularizafarmaapp.com.br", StringComparison.OrdinalIgnoreCase)) return true;
                if (origin.StartsWith("https://regularizafarmaapp.com.br", StringComparison.OrdinalIgnoreCase)) return true;

                return false;
            })
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// ⚠️ UseCors tem que vir ANTES dos MapGet/MapPost
app.UseCors("AllowFlutterWeb");

app.MapGet("/health", () =>
{
    Log("GET /health");
    return Results.Ok(new { status = "ok" });
});

// =======================
// LISTA DISPOSITIVOS WIA (late binding – SEM using WIA)
// =======================
app.MapGet("/devices", () =>
{
    try
    {
        Log("GET /devices");

        dynamic manager = Activator.CreateInstance(
            Type.GetTypeFromProgID("WIA.DeviceManager")!
        );

        var devices = new List<object>();

        int count = manager.DeviceInfos.Count;
        Log($"Total de dispositivos WIA encontrados: {count}");

        for (int i = 1; i <= count; i++)
        {
            dynamic info = manager.DeviceInfos.Item(i);

            string id = "";
            string name = "";
            int type = 0;

            try { id = info.DeviceID?.ToString() ?? ""; } catch { }
            try { name = info.Properties["Name"].Value?.ToString() ?? ""; } catch { }
            try { type = (int)info.Type; } catch { }

            Log($"GET /devices | index={i} | type={type} | name='{name}' | id='{id}'");

            devices.Add(new { id, name, type });
        }

        return Results.Ok(devices);
    }
    catch (Exception ex)
    {
        Log($"Erro em GET /devices: {ex}");
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

        string? deviceId = ctx.Request.Query["deviceId"].ToString();

        Log($"POST /scan/pdf | forceUi={forceUi} | deviceId='{deviceId}'");

        byte[] imageBytes = await RunStaAsync(() => AcquireImagePngBytes(forceUi, deviceId));

        if (imageBytes.Length == 0)
        {
            Log("POST /scan/pdf | Scan cancelado ou sem imagem.");
            return Results.BadRequest("Scan cancelado.");
        }

        byte[] pdfBytes = CreatePdf(imageBytes);

        Log($"POST /scan/pdf | PDF gerado com sucesso | tamanho={pdfBytes.Length} bytes");

        return Results.File(
            pdfBytes,
            "application/pdf",
            $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
        );
    }
    catch (COMException ex)
    {
        Log($"Erro COM/WIA em POST /scan/pdf: {ex}");
        return Results.Problem($"Erro COM/WIA: {ex.Message}");
    }
    catch (Exception ex)
    {
        Log($"Erro em POST /scan/pdf: {ex}");
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

        string? deviceId = ctx.Request.Query["deviceId"].ToString();

        Log($"POST /scan/png | forceUi={forceUi} | deviceId='{deviceId}'");

        byte[] imageBytes = await RunStaAsync(() => AcquireImagePngBytes(forceUi, deviceId));

        if (imageBytes.Length == 0)
        {
            Log("POST /scan/png | Scan cancelado ou sem imagem.");
            return Results.BadRequest("Scan cancelado.");
        }

        Log($"POST /scan/png | PNG gerado com sucesso | tamanho={imageBytes.Length} bytes");

        return Results.File(
            imageBytes,
            "image/png",
            $"scan_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        );
    }
    catch (COMException ex)
    {
        Log($"Erro COM/WIA em POST /scan/png: {ex}");
        return Results.Problem($"Erro COM/WIA: {ex.Message}");
    }
    catch (Exception ex)
    {
        Log($"Erro em POST /scan/png: {ex}");
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

Log("Scanner Connector iniciado em http://127.0.0.1:4545");

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
        Log($"Erro ao testar /health pelo menu: {ex}");
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
        Log("Usuário clicou em Sair.");
        await app.StopAsync();
    }
    catch (Exception ex)
    {
        Log($"Erro ao parar app pelo menu Sair: {ex}");
    }

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
        Log($"Erro ao testar /health no duplo clique: {ex}");
        MessageBox.Show(ex.Message, "Erro");
    }
};

// 3) Segura o app vivo sem abrir janela
Application.ApplicationExit += async (_, __) =>
{
    try
    {
        Log("ApplicationExit chamado. Parando servidor.");
        await app.StopAsync();
    }
    catch (Exception ex)
    {
        Log($"Erro no ApplicationExit: {ex}");
    }
};

Application.Run();

// 4) Cleanup (quando sair)
notify.Visible = false;
Log("Scanner Connector finalizado.");

// =======================
// MÉTODOS AUXILIARES
// =======================

static byte[] AcquireImagePngBytes(bool forceUi, string? deviceId = null)
{
    const int ScannerDeviceType = 1; // WiaDeviceType.ScannerDeviceType
    const string WiaFormatPng = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}"; // wiaFormatPNG

    Log($"AcquireImagePngBytes iniciado | forceUi={forceUi} | deviceId='{deviceId}'");

    // Se pediu UI, só abre a janela do WIA quando NÃO veio scanner específico.
    // Com deviceId, o ShowAcquireImage pode ignorar a escolha e acionar outro scanner.
    if (forceUi)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            Log("Erro: forceUi=true com deviceId informado. Modo UI não suporta seleção direta.");
            throw new Exception("Modo com janela do scanner não suporta seleção direta por deviceId.");
        }

        Log("Abrindo scanner com janela WIA.");
        return AcquireWithUiDialog(WiaFormatPng);
    }

    dynamic deviceManager = Activator.CreateInstance(
        Type.GetTypeFromProgID("WIA.DeviceManager")!
    );

    var candidates = new List<(dynamic info, string name)>();

    int count = deviceManager.DeviceInfos.Count;
    Log($"Total de dispositivos WIA encontrados dentro do scan: {count}");

    for (int i = 1; i <= count; i++)
    {
        dynamic info = deviceManager.DeviceInfos.Item(i);

        int type = 0;
        try { type = (int)info.Type; } catch { }

        string id = "";
        try { id = info.DeviceID?.ToString() ?? ""; } catch { }

        string name = "";
        try { name = info.Properties["Name"].Value?.ToString() ?? ""; } catch { }

        Log($"Dispositivo encontrado | index={i} | type={type} | name='{name}' | id='{id}'");

        if (type != ScannerDeviceType)
        {
            Log($"Dispositivo ignorado porque não é scanner | index={i} | type={type}");
            continue;
        }

        candidates.Add((info, name));
    }

    Log($"Total de scanners candidatos: {candidates.Count}");

    if (candidates.Count == 0)
    {
        Log("Nenhum scanner encontrado.");
        throw new Exception("Nenhum scanner encontrado.");
    }

    dynamic selectedInfo = null!;

    // Se veio um deviceId do Flutter, usa ele
    if (!string.IsNullOrWhiteSpace(deviceId))
    {
        Log($"Tentando selecionar scanner pelo deviceId recebido do Flutter: '{deviceId}'");

        foreach (var c in candidates)
        {
            string id = "";
            try { id = c.info.DeviceID?.ToString() ?? ""; } catch { }

            string name = "";
            try { name = c.info.Properties["Name"].Value?.ToString() ?? ""; } catch { }

            Log($"Comparando scanner candidato | name='{name}' | id='{id}'");

            if (string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                selectedInfo = c.info;
                Log($"Scanner encontrado pelo deviceId | name='{name}' | id='{id}'");
                break;
            }
        }

        if (selectedInfo == null)
        {
            Log($"Scanner selecionado não encontrado para deviceId='{deviceId}'");
            throw new Exception("Scanner selecionado não encontrado.");
        }
    }
    else
    {
        // Se não veio deviceId, mantém comportamento antigo
        Log("Nenhum deviceId veio do Flutter. Usando seleção automática antiga.");

        var selected = candidates.FirstOrDefault(c =>
            (c.name ?? "").Contains("USB", StringComparison.OrdinalIgnoreCase)
        );

        selectedInfo = selected.info ?? candidates[0].info;
    }

    try
    {
        string selectedId = selectedInfo.DeviceID?.ToString() ?? "";
        string selectedName = selectedInfo.Properties["Name"].Value?.ToString() ?? "";

        Log($"Scanner selecionado | name='{selectedName}' | id='{selectedId}'");
    }
    catch (Exception ex)
    {
        Log($"Erro ao registrar scanner selecionado: {ex.Message}");
    }

    dynamic selectedDevice = selectedInfo.Connect();

    Log("Scanner conectado. Tentando escanear sem janela.");

    // 1) tenta SILENT (sem UI)
    try
    {
        var bytes = AcquireSilent(selectedDevice, WiaFormatPng);

        Log($"Resultado AcquireSilent | bytes={bytes.Length}");

        if (bytes.Length > 0)
            return bytes;
    }
    catch (Exception ex)
    {
        Log($"AcquireSilent falhou: {ex.Message}");
        // cai pro fallback
    }

    // 2) fallback COM UI somente quando NÃO foi escolhido scanner específico.
    // Se veio deviceId, não abre a janela do WIA para evitar acionar outro scanner.
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        Log("Sem deviceId. Abrindo fallback com janela WIA.");
        return AcquireWithUiDialog(WiaFormatPng);
    }

    Log("Falha ao escanear com deviceId específico. Não será aberto fallback UI para evitar acionar outro scanner.");
    throw new Exception("Não foi possível escanear com o scanner selecionado. Tente selecionar outro scanner.");
}

static byte[] AcquireSilent(dynamic device, string wiaFormatPng)
{
    Log("AcquireSilent iniciado.");

    // item 1 geralmente é o flatbed
    dynamic item = device.Items[1];

    // Não force ShowUI=false aqui: alguns drivers quebram.
    // No máximo desliga progresso (e ainda assim pode ser ignorado).
    TrySetPropertyDynamic(item.Properties, "ShowProgress", false);

    dynamic image = item.Transfer(wiaFormatPng);

    if (image == null)
    {
        Log("AcquireSilent retornou imagem nula.");
        return Array.Empty<byte>();
    }

    byte[] bytes = (byte[])image.FileData.BinaryData;

    Log($"AcquireSilent concluído | bytes={bytes.Length}");

    return bytes;
}

static byte[] AcquireWithUiDialog(string wiaFormatPng, string? deviceId = null)
{
    Log($"AcquireWithUiDialog iniciado | deviceId='{deviceId}'");

    dynamic dialog = Activator.CreateInstance(
        Type.GetTypeFromProgID("WIA.CommonDialog")!
    );

    const int ScannerDeviceType = 1;
    const int ColorIntent = 1;
    const int MaxQuality = 2;

    bool alwaysSelectDevice = string.IsNullOrWhiteSpace(deviceId);

    Log($"ShowAcquireImage | alwaysSelectDevice={alwaysSelectDevice}");

    dynamic image = dialog.ShowAcquireImage(
        ScannerDeviceType,
        ColorIntent,
        MaxQuality,
        wiaFormatPng,
        alwaysSelectDevice,
        true,
        false
    );

    if (image == null)
    {
        Log("AcquireWithUiDialog retornou imagem nula.");
        return Array.Empty<byte>();
    }

    byte[] bytes = (byte[])image.FileData.BinaryData;

    Log($"AcquireWithUiDialog concluído | bytes={bytes.Length}");

    return bytes;
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
                    Log($"Propriedade WIA alterada | {name}={value}");
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
    Log($"CreatePdf iniciado | imagem={imageBytes.Length} bytes");

    var doc = Document.Create(container =>
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(20);
            page.Content().Image(imageBytes).FitArea();
        });
    });

    byte[] pdfBytes = doc.GeneratePdf();

    Log($"CreatePdf concluído | pdf={pdfBytes.Length} bytes");

    return pdfBytes;
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

static void Log(string mensagem)
{
    try
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scanner-log.txt");

        File.AppendAllText(
            path,
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {mensagem}{Environment.NewLine}"
        );
    }
    catch
    {
        // Não deixa o log quebrar o scanner
    }
}