using System;

namespace BassRelay.Audio;

public sealed record SimHubGameState(bool GameRunning, bool IsAvailable);

/// <summary>The host supplies game state; the audio engine owns and disposes this monitor.</summary>
public interface ISimHubGameMonitor : IDisposable
{
    event Action? Changed;
    SimHubGameState State { get; }
    void SetEnabled(bool enabled);
}
