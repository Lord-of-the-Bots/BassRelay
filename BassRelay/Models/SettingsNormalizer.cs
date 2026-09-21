using System;
using System.Collections.Generic;

namespace BassRelay.Models;

public static class SettingsNormalizer
{
    public static void Normalize(AppSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        if (settings.SimHubPort is < 1 or > 65535) settings.SimHubPort = 8888;
        settings.Shakers ??= new List<ShakerSettings>();
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
            if (!shaker.Enabled) { shaker.Gain = 0; shaker.Enabled = true; }
            shaker.Gain = double.IsNaN(shaker.Gain) || double.IsInfinity(shaker.Gain)
                ? 1 : Math.Max(0, Math.Min(1, shaker.Gain));
            if (string.IsNullOrWhiteSpace(shaker.DeviceId)) shaker.DeviceId = null;
        }
    }
}
