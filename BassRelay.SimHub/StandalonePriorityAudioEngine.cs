using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BassRelay.Audio;
using BassRelay.Models;

namespace BassRelay.SimHub;

public interface IStandalonePriorityStatus
{
    bool WaitingForStandalone { get; }
}

/// <summary>
/// Gives this enabled plugin priority over the published standalone app. A dedicated
/// thread owns its legacy lifetime mutex, including while no sound is being routed.
/// Settings remain usable during an asynchronous, cooperative handover.
/// </summary>
public sealed class StandalonePriorityAudioEngine : IAudioEngine, IStandalonePriorityStatus
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private readonly Func<IAudioEngine> _createEngine;
    private readonly Action<IAudioEngine> _waitForShutdown;
    private readonly Func<string, object[], string> _text;
    private readonly Action<string, Exception?> _log;
    private readonly string _mutexName, _exitEventName;
    private readonly TimeSpan _retryInterval, _failureDelay;
    private ShakerSettings[] _desired = new ShakerSettings[0];
    private bool _pauseForGame = true, _paused, _takeoverFailed;
    private string? _startError;
    private IAudioEngine? _inner;
    private AudioEngineSnapshot _snapshot;
    private int _disposed, _ownsStandalone, _notificationPending;

    public StandalonePriorityAudioEngine(ISimHubGameMonitor monitor,
        Func<string, object[], string> text, Action<string, Exception?> log)
        : this(() => new AudioEngine(monitor, text, log, guardDesktopInstance: false),
            engine => ((AudioEngine)engine).WaitForShutdown(), text, log,
            @"Local\BassRelay.Application", @"Local\BassRelay.Exit",
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)) { }

    // Alternate backends and synchronization names share the same handover lifecycle.
    internal StandalonePriorityAudioEngine(Func<IAudioEngine> createEngine,
        Action<IAudioEngine> waitForShutdown, Func<string, object[], string> text,
        Action<string, Exception?> log, string mutexName, string exitEventName,
        TimeSpan retryInterval, TimeSpan failureDelay)
    {
        _createEngine = createEngine ?? throw new ArgumentNullException(nameof(createEngine));
        _waitForShutdown = waitForShutdown ?? throw new ArgumentNullException(nameof(waitForShutdown));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _mutexName = mutexName ?? throw new ArgumentNullException(nameof(mutexName));
        _exitEventName = exitEventName ?? throw new ArgumentNullException(nameof(exitEventName));
        _retryInterval = retryInterval > TimeSpan.Zero && retryInterval.TotalMilliseconds <= int.MaxValue
            ? retryInterval : throw new ArgumentOutOfRangeException(nameof(retryInterval));
        _failureDelay = failureDelay >= TimeSpan.Zero ? failureDelay : throw new ArgumentOutOfRangeException(nameof(failureDelay));
        _snapshot = WaitingSnapshot();
        _worker = new Thread(Run) { IsBackground = true, Name = "BassRelay standalone handover" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public event Action<AudioEngineSnapshot>? SnapshotChanged;
    public AudioEngineSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool WaitingForStandalone => Volatile.Read(ref _ownsStandalone) == 0 && Volatile.Read(ref _disposed) == 0;

    public void Configure(IReadOnlyList<ShakerSettings> shakers, bool pauseForSimHub = true, bool isPaused = false)
    {
        if (shakers is null) throw new ArgumentNullException(nameof(shakers));
        AudioEngineSnapshot? waiting = null;
        lock (_gate)
        {
            if (_disposed != 0) return;
            _desired = shakers.Select(shaker => shaker.Copy()).ToArray();
            _pauseForGame = pauseForSimHub;
            _paused = isPaused;
            if (_inner is not null) _inner.Configure(_desired, _pauseForGame, _paused);
            else waiting = WaitingSnapshot();
        }
        if (waiting is not null) Publish(waiting, onlyWithoutEngine: true);
    }

    public void Refresh()
    {
        AudioEngineSnapshot? waiting = null;
        lock (_gate)
        {
            if (_disposed != 0) return;
            if (_inner is not null) _inner.Refresh();
            else waiting = WaitingSnapshot();
        }
        if (waiting is not null) Publish(waiting, onlyWithoutEngine: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IAudioEngine? inner;
        lock (_gate) inner = _inner;
        // Silence immediately. The coordinator, not this caller, waits for full cleanup
        // and releases the mutex even if a driver's Dispose exceeds the UI wait limit.
        try { inner?.Dispose(); }
        catch (Exception exception) { Log("Stop plugin audio", exception); }
        Signal();
    }

    private void Run()
    {
        Mutex? ownership = null;
        IAudioEngine? engine = null;
        Action<AudioEngineSnapshot>? forward = null;
        bool owned = false;
        var waiting = Stopwatch.StartNew();
        string? lastFailure = null;
        try
        {
            while (Volatile.Read(ref _disposed) == 0 && !owned)
            {
                try
                {
                    // Keep this handle while waiting. The released standalone uses
                    // createdNew for single-instance detection, so retaining the object
                    // prevents a replacement copy stealing the gap during handover.
                    ownership ??= new Mutex(false, _mutexName);
                    try { owned = ownership.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (owned) break;
                    if (EventWaitHandle.TryOpenExisting(_exitEventName, out var exit))
                        using (exit) exit.Set();
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException ||
                    exception is WaitHandleCannotBeOpenedException || exception is System.IO.IOException)
                {
                    string failure = exception.GetType().Name + ":" + exception.HResult;
                    if (lastFailure != failure) Log("Request standalone shutdown", exception);
                    lastFailure = failure;
                    waiting = Stopwatch.StartNew();
                    lock (_gate) _takeoverFailed = true;
                }
                AudioEngineSnapshot snapshot;
                lock (_gate)
                {
                    if (waiting.Elapsed >= _failureDelay) _takeoverFailed = true;
                    snapshot = WaitingSnapshot();
                }
                Publish(snapshot);
                _wake.WaitOne(_retryInterval);
            }
            if (!owned || Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _ownsStandalone, 1);
            while (Volatile.Read(ref _disposed) == 0 && engine is null)
            {
                try
                {
                    engine = _createEngine();
                    var forwardingEngine = engine;
                    forward = snapshot => Publish(snapshot, expectedEngine: forwardingEngine);
                    engine.SnapshotChanged += forward;
                    lock (_gate)
                    {
                        if (_disposed != 0) break;
                        _inner = engine;
                        _startError = null;
                        engine.Configure(_desired, _pauseForGame, _paused);
                    }
                    Publish(engine.Snapshot);
                }
                catch (Exception exception)
                {
                    Log("Start plugin audio after standalone shutdown", exception);
                    lock (_gate) _inner = null;
                    if (engine is not null)
                    {
                        if (forward is not null) engine.SnapshotChanged -= forward;
                        StopAndWait(engine);
                        engine = null;
                    }
                    AudioEngineSnapshot snapshot;
                    lock (_gate)
                    {
                        _inner = null;
                        _startError = exception.Message;
                        snapshot = WaitingSnapshot();
                    }
                    Publish(snapshot);
                    _wake.WaitOne(_retryInterval);
                }
            }
            while (Volatile.Read(ref _disposed) == 0) _wake.WaitOne();
        }
        catch (Exception exception)
        {
            // Retain ownership until disabled even when initialization unexpectedly fails.
            Log("Plugin standalone handover", exception);
            lock (_gate) _startError = exception.Message;
            Publish(WaitingSnapshot());
            while (Volatile.Read(ref _disposed) == 0) _wake.WaitOne();
        }
        finally
        {
            if (engine is not null)
            {
                if (forward is not null) engine.SnapshotChanged -= forward;
                // This may wait indefinitely for a broken driver. Releasing the lifetime
                // mutex early would let the old standalone open the same hardware.
                StopAndWait(engine);
            }
            lock (_gate) _inner = null;
            if (owned) ownership!.ReleaseMutex();
            ownership?.Dispose();
            Volatile.Write(ref _ownsStandalone, 0);
            _wake.Dispose();
        }
    }

    private AudioEngineSnapshot WaitingSnapshot()
    {
        string? error = _startError is not null ? _text("PluginStartError", new object[] { _startError })
            : _takeoverFailed ? _text("TakeoverFailed", new object[0]) : null;
        string message = error ?? _text("StatusWaitingTakeover", new object[0]);
        return new AudioEngineSnapshot(null, _text("FindingDefaultDevice", new object[0]),
            new AudioDeviceInfo[0], _desired.Select(shaker => new ShakerStatus(shaker.Id, message, true, false)).ToArray(), error);
    }

    private void StopAndWait(IAudioEngine engine)
    {
        try { engine.Dispose(); }
        catch (Exception exception) { Log("Dispose plugin audio", exception); }
        bool reported = false;
        while (true)
        {
            try { _waitForShutdown(engine); return; }
            catch (Exception exception)
            {
                // An unconfirmed cleanup is not permission to let another app use the
                // devices. Keep the owner thread alive and retry instead of abandoning
                // its mutex through an unhandled background exception.
                if (!reported) Log("Wait for plugin audio cleanup", exception);
                reported = true;
                _wake.WaitOne(_retryInterval);
            }
        }
    }

    private void Publish(AudioEngineSnapshot snapshot, bool onlyWithoutEngine = false, IAudioEngine? expectedEngine = null)
    {
        lock (_gate)
        {
            if (_disposed != 0 || (onlyWithoutEngine && _inner is not null) ||
                (expectedEngine is not null && !ReferenceEquals(_inner, expectedEngine))) return;
            Volatile.Write(ref _snapshot, snapshot);
        }
        if (Interlocked.CompareExchange(ref _notificationPending, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            AudioEngineSnapshot delivered;
            do
            {
                delivered = Snapshot;
                var handlers = SnapshotChanged;
                if (Volatile.Read(ref _disposed) == 0 && handlers is not null)
                    foreach (Action<AudioEngineSnapshot> handler in handlers.GetInvocationList())
                        try { handler(delivered); } catch { /* Settings pages cannot interrupt handover. */ }
                Interlocked.Exchange(ref _notificationPending, 0);
            } while (!ReferenceEquals(delivered, Snapshot) && Interlocked.CompareExchange(ref _notificationPending, 1, 0) == 0);
        });
    }

    private void Signal()
    {
        try { _wake.Set(); } catch (ObjectDisposedException) { }
    }

    private void Log(string message, Exception? exception)
    {
        try { _log(message, exception); } catch { }
    }
}
