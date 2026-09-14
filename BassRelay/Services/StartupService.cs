using System;
using System.IO;
using Microsoft.Win32;

namespace BassRelay.Services;

public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string BuildCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"'))
            throw new ArgumentException(Localization.Text("InvalidAppPath"), nameof(executablePath));
        return $"\"{Path.GetFullPath(executablePath)}\" --startup";
    }

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException(Localization.Text("StartupSettingsUnavailable"));
        if (!enabled) { key.DeleteValue("BassRelay", false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException(Localization.Text("AppPathUnknown"));
        // dotnet run should not register the SDK host as the login application.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(AppContext.BaseDirectory, "BassRelay.exe");
        string command = BuildCommand(executable);
        if (!string.Equals(key.GetValue("BassRelay") as string, command, StringComparison.Ordinal))
            key.SetValue("BassRelay", command, RegistryValueKind.String);
    }
}
