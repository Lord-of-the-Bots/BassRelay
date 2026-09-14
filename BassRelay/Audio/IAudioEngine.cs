using System;
using System.Collections.Generic;
using BassRelay.Models;

namespace BassRelay.Audio;

public sealed record AudioDeviceInfo(string Id, string Name);
public sealed record ShakerStatus(Guid Id, string Message, bool IsBlocked, bool IsRunning);
public sealed record AudioEngineSnapshot(
    string? SourceDeviceId,
    string SourceDeviceName,
    IReadOnlyList<AudioDeviceInfo> Devices,
    IReadOnlyList<ShakerStatus> Shakers,
    string? Error = null);

public interface IAudioEngine : IDisposable
{
    event Action<AudioEngineSnapshot>? SnapshotChanged;
    AudioEngineSnapshot Snapshot { get; }
    void Configure(IReadOnlyList<ShakerSettings> shakers, bool pauseForSimHub = true, bool isPaused = false);
    void Refresh();
}
