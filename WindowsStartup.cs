using Microsoft.Win32;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RegularizaFarmaScannerLocal";

    public static object GetStatus()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Não foi possível identificar o executável em uso.");
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        string? command = key?.GetValue(ValueName) as string;
        bool configured = !string.IsNullOrWhiteSpace(command);
        return new
        {
            configured,
            pointsToCurrentExe = configured && string.Equals(command, Quote(executable), StringComparison.OrdinalIgnoreCase),
            executablePath = executable,
            configuredCommand = command
        };
    }

    public static object SetEnabled(bool enabled)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Não foi possível identificar o executável em uso.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Não foi possível acessar a inicialização do Windows.");
        if (enabled)
            key.SetValue(ValueName, Quote(executable), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, false);
        return GetStatus();
    }

    private static string Quote(string path) => $"\"{path}\"";
}
