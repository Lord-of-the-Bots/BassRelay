using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BassRelay.Models;

namespace BassRelay.Services;

public sealed class SettingsStore
{
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "Data");
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private bool _loadFailed;
    public string? LoadWarning => _loadFailed ? Localization.Text("LoadSettingsWarning") : null;

    public SettingsStore(string? path = null) => _path = path ?? Path.Combine(DataDirectory, "settings.json");

    public AppSettings Load()
    {
        _loadFailed = false;
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? throw new JsonException("Empty settings");
            Normalize(settings);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _loadFailed = true;
            AppLog.Write("Load settings", ex);
            // Keep the unreadable original for recovery before a new save replaces it.
            try { File.Copy(_path, _path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); }
            catch (Exception backupError) { AppLog.Write("Backup settings", backupError); }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, _path, true);
    }

    public static void Normalize(AppSettings settings)
    {
        if (settings.SimHubPort is < 1 or > 65535) settings.SimHubPort = 8888;
        settings.Shakers ??= [];
        settings.Shakers.RemoveAll(s => s is null);
        var ids = new HashSet<Guid>();
        foreach (var shaker in settings.Shakers)
        {
            if (shaker.Id == Guid.Empty || !ids.Add(shaker.Id))
            {
                shaker.Id = Guid.NewGuid();
                ids.Add(shaker.Id);
            }
            if (!FrequencyRange.IsValidBand(shaker.LowCutHz, shaker.HighCutHz))
            {
                shaker.LowCutHz = 40;
                shaker.HighCutHz = 90;
            }
            // Legacy disabled rows stay silent with the new single 0–100% control.
            if (!shaker.Enabled) { shaker.Gain = 0; shaker.Enabled = true; }
            shaker.Gain = double.IsFinite(shaker.Gain) ? Math.Clamp(shaker.Gain, 0, 1) : 1;
            if (string.IsNullOrWhiteSpace(shaker.DeviceId)) shaker.DeviceId = null;
        }
    }
}
