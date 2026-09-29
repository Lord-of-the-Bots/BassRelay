using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BassRelay.Models;
using NAudio.Wave;

namespace BassRelay.Audio;

public sealed record AudioChannelInfo(int Index, int SpeakerMask);

/// <summary>WASAPI channel order follows the ascending bits in the native speaker mask.</summary>
public static class AudioChannelLayout
{
    private const int SpeakerMaskOffset = 20; // WAVEFORMATEX (18 bytes), valid bits (2 bytes).
    private const int KnownSpeakers = 0x3ffff;

    public static IReadOnlyList<AudioChannelInfo> GetChannels(int count, int mask)
    {
        if (count is < 0 or > 32) throw new ArgumentOutOfRangeException(nameof(count));
        var channels = new List<AudioChannelInfo>(count);
        if (mask > 0 && (mask & ~KnownSpeakers) == 0)
        {
            for (int bit = 1; bit <= 0x20000; bit <<= 1)
                if ((mask & bit) != 0) channels.Add(new(channels.Count, bit));
        }
        if (channels.Count != count)
        {
            // An absent or inconsistent mask must not invent physical speaker names.
            channels.Clear();
            for (int index = 0; index < count; index++) channels.Add(new(index, 0));
        }
        return channels;
    }

    public static int GetChannelMask(WaveFormat format)
    {
        if (format is null) throw new ArgumentNullException(nameof(format));
        if (format is not WaveFormatExtensible) return 0;
        IntPtr pointer = WaveFormat.MarshalToPtr(format);
        try { return Marshal.ReadInt32(pointer, SpeakerMaskOffset); }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public static WaveFormat CreateFloatFormat(int sampleRate, int channels, int channelMask)
    {
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        // Retain the exact format used by existing mono/stereo output sessions.
        if (channels <= 2) return WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        IntPtr pointer = WaveFormat.MarshalToPtr(new WaveFormatExtensible(sampleRate, 32, channels));
        try
        {
            // NAudio's constructor invents the first N speaker bits (e.g. 0xff for
            // eight channels). Preserve the endpoint's actual layout, e.g. 0x63f.
            Marshal.WriteInt32(pointer, SpeakerMaskOffset, channelMask);
            return WaveFormat.MarshalFromPtr(pointer);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public static ChannelMappingSettings? FindMapping(AudioChannelInfo channel, IReadOnlyList<ChannelMappingSettings>? mappings)
    {
        if (mappings is null) return null;
        foreach (ChannelMappingSettings mapping in mappings)
            if (mapping is not null && (channel.SpeakerMask != 0
                ? mapping.SpeakerMask == channel.SpeakerMask
                : mapping.SpeakerMask == 0 && mapping.ChannelIndex == channel.Index)) return mapping;
        // A changed layout never reassigns a named speaker to a different output.
        return null;
    }
}
