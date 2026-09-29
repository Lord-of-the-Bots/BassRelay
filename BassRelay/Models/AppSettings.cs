using System;
using System.Collections.Generic;

namespace BassRelay.Models;

public sealed class AppSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    // Keep the serialized name so an explicitly disabled option survives upgrades.
    public bool PrioritizeExternalAudio { get; set; } = true;
    public int SimHubPort { get; set; } = 8888;
    public bool IsPaused { get; set; }
    public string Language { get; set; } = "system";
    public List<ShakerSettings> Shakers { get; set; } = [new()];
}

public sealed class ShakerSettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public double LowCutHz { get; set; } = 40;
    public double HighCutHz { get; set; } = 90;
    public double Gain { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool CustomChannelMapping { get; set; }
    public List<ChannelMappingSettings> ChannelMappings { get; set; } = [];

    public ShakerSettings Copy()
    {
        var copy = (ShakerSettings)MemberwiseClone();
        copy.ChannelMappings = new List<ChannelMappingSettings>();
        if (ChannelMappings is not null)
            foreach (ChannelMappingSettings mapping in ChannelMappings)
                if (mapping is not null) copy.ChannelMappings.Add(mapping.Copy());
        return copy;
    }
}

public sealed class ChannelMappingSettings
{
    // SpeakerMask is a single WAVEFORMATEXTENSIBLE speaker bit, or zero when the
    // endpoint exposes numbered channels without their speaker positions.
    public int ChannelIndex { get; set; }
    public int SpeakerMask { get; set; }
    public bool Enabled { get; set; }
    public double LowCutHz { get; set; } = 40;
    public double HighCutHz { get; set; } = 90;

    public ChannelMappingSettings Copy() => (ChannelMappingSettings)MemberwiseClone();
}
