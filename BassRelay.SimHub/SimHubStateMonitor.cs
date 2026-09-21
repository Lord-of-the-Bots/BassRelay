using System;
using System.Collections.Generic;
using System.Threading;
using BassRelay.Audio;

namespace BassRelay.SimHub;

/// <summary>Game callbacks only store state and wake a worker; no audio, disk or UI work.</summary>
public sealed class SimHubStateMonitor : ISimHubGameMonitor
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private readonly object _gate = new();
    private readonly Queue<bool> _transitions = new();
    // Unknown is intentionally paused: loading the plugin during a race must not emit bass.
    private SimHubGameState _state = new(true, false);
    private int _latest = -1;
    private int _disposed;

    public SimHubStateMonitor(bool? initialRunning = null)
    {
        if (initialRunning.HasValue)
        {
            _state = new SimHubGameState(initialRunning.Value, true);
            _latest = initialRunning.Value ? 1 : 0;
        }
        _worker = new Thread(Run) { IsBackground = true, Name = "BassRelay SimHub state" };
        _worker.Start();
    }

    public event Action? Changed;
    public SimHubGameState State => Volatile.Read(ref _state);

    public void Update(bool gameRunning)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            if (_disposed != 0) return;
            int value = gameRunning ? 1 : 0;
            if (_latest == value) return;
            _latest = value;
            // Preserve even a short start/stop so subscribers invalidate old audio buffers.
            _transitions.Enqueue(gameRunning);
        }
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    // Keep tracking while auto-pause is off, so enabling it during a race is immediate.
    public void SetEnabled(bool enabled) { }

    private void Run()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                _wake.WaitOne();
                if (Volatile.Read(ref _disposed) != 0) break;
                while (Volatile.Read(ref _disposed) == 0)
                {
                    bool running;
                    lock (_gate)
                    {
                        if (_transitions.Count == 0) break;
                        running = _transitions.Dequeue();
                    }
                    Volatile.Write(ref _state, new SimHubGameState(running, true));
                    var handlers = Changed;
                    if (handlers is null) continue;
                    foreach (Action handler in handlers.GetInvocationList())
                        try { handler(); } catch { /* A subscriber cannot interrupt SimHub telemetry. */ }
                }
            }
        }
        finally { _wake.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _wake.Set(); } catch (ObjectDisposedException) { }
        if (Thread.CurrentThread != _worker) _worker.Join(TimeSpan.FromMilliseconds(500));
    }
}
