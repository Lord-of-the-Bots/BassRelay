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
            shaker.ChannelMappings ??= new List<ChannelMappingSettings>();
            var speakers = new HashSet<int>();
            var numberedChannels = new HashSet<int>();
            shaker.ChannelMappings.RemoveAll(mapping => mapping is null ||
                mapping.ChannelIndex is < 0 or >= 32 ||
                (mapping.SpeakerMask != 0 && (mapping.SpeakerMask < 0 ||
                    (mapping.SpeakerMask & ~0x3ffff) != 0 ||
                    (mapping.SpeakerMask & (mapping.SpeakerMask - 1)) != 0)) ||
                !(mapping.SpeakerMask == 0 ? numberedChannels.Add(mapping.ChannelIndex) : speakers.Add(mapping.SpeakerMask)));
            if (shaker.ChannelMappings.Count > 32)
                shaker.ChannelMappings.RemoveRange(32, shaker.ChannelMappings.Count - 32);
            foreach (ChannelMappingSettings mapping in shaker.ChannelMappings)
            {
                if (!FrequencyRange.IsValidBand(mapping.LowCutHz, mapping.HighCutHz))
                {
                    mapping.LowCutHz = shaker.LowCutHz;
                    mapping.HighCutHz = shaker.HighCutHz;
                }
            }
        }
    }
}
