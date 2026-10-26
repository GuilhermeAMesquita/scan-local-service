using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class ScannerDiagnostics
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;
    private static readonly object Sync = new();
    private static readonly object LogSync = new();
    private static readonly string ResolvedLogPath = ResolveLogPath();
    private static long _requested;
    private static long _succeeded;
    private static long _failed;
    private static int _active;
    private static string? _lastResult;
    private static string? _lastError;
    private static DateTimeOffset? _lastScanAt;
    private static string? _lastFormat;
    private static long? _lastOutputBytes;
    private static int? _lastWidth;
    private static int? _lastHeight;
    private static int? _lastDpiX;
    private static int? _lastDpiY;

    public static string LogPath => ResolvedLogPath;

    public static void AppendLog(string message)
    {
        try
        {
            lock (LogSync)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Falhas ao escrever diagnóstico não podem interromper uma venda.
        }
    }

    private static string ResolveLogPath()
    {
        string previous = Path.Combine(AppContext.BaseDirectory, "scanner-log.txt");
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RegularizaFarma", "Scanner");
            Directory.CreateDirectory(directory);
            string current = Path.Combine(directory, "scanner-log.txt");
            if (!File.Exists(current) && File.Exists(previous) &&
                !string.Equals(current, previous, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(previous, current); } catch { }
            }
            return current;
        }
        catch
        {
            return previous;
        }
    }

    public static void ScanStarted(string format)
    {
        Interlocked.Increment(ref _requested);
        Interlocked.Increment(ref _active);
        lock (Sync) _lastFormat = format;
    }

    public static void ScanSucceeded(long outputBytes)
    {
        Interlocked.Increment(ref _succeeded);
        Interlocked.Decrement(ref _active);
        lock (Sync)
        {
            _lastResult = "Concluída";
            _lastError = null;
            _lastScanAt = DateTimeOffset.Now;
            _lastOutputBytes = outputBytes;
        }
    }

    public static void ScanFailed(string message)
    {
        Interlocked.Increment(ref _failed);
        Interlocked.Decrement(ref _active);
        lock (Sync)
        {
            _lastResult = "Falhou";
            _lastError = message;
            _lastScanAt = DateTimeOffset.Now;
            _lastOutputBytes = null;
        }
    }

    public static void CaptureMeasured(int width, int height)
    {
        lock (Sync)
        {
            _lastWidth = width;
            _lastHeight = height;
        }
    }

    public static void ResolutionMeasured(int x, int y)
    {
        lock (Sync)
        {
            _lastDpiX = x;
            _lastDpiY = y;
        }
    }

    public static object Snapshot()
    {
        lock (Sync)
        {
            using var process = Process.GetCurrentProcess();
            var log = new FileInfo(LogPath);
            return new
            {
                status = "online",
                version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                startedAt = StartedAt,
                now = DateTimeOffset.Now,
                uptimeSeconds = (long)(DateTimeOffset.Now - StartedAt).TotalSeconds,
                url = "http://127.0.0.1:4545",
                machine = Environment.MachineName,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                dotnet = RuntimeInformation.FrameworkDescription,
                processId = Environment.ProcessId,
                memoryBytes = process.WorkingSet64,
                wiaAvailable = Type.GetTypeFromProgID("WIA.DeviceManager") is not null,
                logPath = LogPath,
                logBytes = log.Exists ? log.Length : 0,
                startup = WindowsStartup.GetStatus(),
                scan = new
                {
                    requested = Interlocked.Read(ref _requested),
                    succeeded = Interlocked.Read(ref _succeeded),
                    failed = Interlocked.Read(ref _failed),
                    active = Volatile.Read(ref _active),
                    lastResult = _lastResult,
                    lastError = _lastError,
                    lastAt = _lastScanAt,
                    lastFormat = _lastFormat,
                    lastOutputBytes = _lastOutputBytes,
                    lastWidth = _lastWidth,
                    lastHeight = _lastHeight,
                    lastDpiX = _lastDpiX,
                    lastDpiY = _lastDpiY,
                    requestedDpi = 300,
                    fallbackDpi = 200
                }
            };
        }
    }
}
