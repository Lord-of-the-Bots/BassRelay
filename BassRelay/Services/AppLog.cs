using System;
using System.IO;

namespace BassRelay.Services;

public static class AppLog
{
    private static readonly object Gate = new();
    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                var path = Path.Combine(SettingsStore.DataDirectory, "BassRelay.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message} {exception}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never interrupt audio or application shutdown. */ }
    }
}
