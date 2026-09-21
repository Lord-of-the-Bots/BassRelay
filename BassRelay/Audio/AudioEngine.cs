using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BassRelay.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BassRelay.Audio;

/// <summary>
/// Owns all COM/audio lifetimes on one MTA thread. WASAPI callbacks only push data or
/// signal that thread; they never stop/dispose an audio object or synchronously call the UI.
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    private readonly object _configurationGate = new();
    private readonly object _routingGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private readonly Dictionary<Guid, OutputSession> _outputs = new();
    private readonly Dictionary<Guid, (DateTime RetryAt, string Message)> _outputErrors = new();
    private readonly ISimHubGameMonitor _simHubMonitor;
    private readonly Func<string, object[], string> _text;
    private readonly Action<string, Exception?> _log;
    private readonly bool _guardDesktopInstance;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private bool _desiredSimHubEnabled = true;
    private bool _desiredPaused, _isPaused;
    private int _manualPauseRequested, _simHubPauseRequested;
    private ShakerSettings[] _desired = [];
    private OutputSession[] _publishedOutputs = [];
    private AudioEngineSnapshot _snapshot;
    private Mutex? _audioOwnership, _desktopOwnership;
    private MMDeviceEnumerator? _enumerator;
    private DeviceNotifications? _notifications;
    private CaptureSession? _capture;
    private DateTime _captureRetryAt;
    private string? _captureError;
    private long _configurationVersion, _appliedConfigurationVersion = -1;
    private int _sourceEpoch, _resetRequested, _disposed, _notificationPending;

    public AudioEngine(ISimHubGameMonitor simHubMonitor, Func<string, object[], string> text,
        Action<string, Exception?> log, bool guardDesktopInstance = false)
    {
        _simHubMonitor = simHubMonitor ?? throw new ArgumentNullException(nameof(simHubMonitor));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _guardDesktopInstance = guardDesktopInstance;
        _snapshot = new(null, Text("FindingDefaultDevice"), [], []);
        _simHubMonitor.Changed += SimHubStateChanged;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BassRelay audio control" };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public event Action<AudioEngineSnapshot>? SnapshotChanged;
    public AudioEngineSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Configure(IReadOnlyList<ShakerSettings> shakers, bool pauseForSimHub = true, bool isPaused = false)
    {
        if (shakers is null) throw new ArgumentNullException(nameof(shakers));
        if (Volatile.Read(ref _disposed) != 0) return;
        bool pauseChanged;
        lock (_configurationGate)
        {
            _desired = shakers.Select(shaker => shaker.Copy()).ToArray();
            _desiredSimHubEnabled = pauseForSimHub;
            pauseChanged = _desiredPaused != isPaused;
            _desiredPaused = isPaused;
            Volatile.Write(ref _manualPauseRequested, isPaused ? 1 : 0);
            _configurationVersion++;
        }
        _simHubMonitor.SetEnabled(pauseForSimHub);
        SimHubStateChanged();
        // Stop even if the worker is inside a driver call. A rapid pause/resume
        // still forces fresh providers instead of leaving existing ones muted.
        if (pauseChanged) InvalidateRouting();
        else Signal();
    }

    public void Refresh() => InvalidateRouting();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _simHubMonitor.Changed -= SimHubStateChanged;
        lock (_routingGate)
        {
            _sourceEpoch++;
            MuteOutputs();
        }
        try { _simHubMonitor.Dispose(); }
        catch (Exception exception) { Log("Dispose game monitor", exception); }
        Signal(allowDisposed: true);
        // A stalled driver must not freeze application shutdown forever. The background
        // worker retains ownership and completes cleanup when the driver returns.
        if (Thread.CurrentThread != _worker) _worker.Join(TimeSpan.FromSeconds(3));
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Waits for actual driver cleanup after Dispose. Only a background lifecycle owner
    /// may call this: an unresponsive audio driver can keep the wait pending indefinitely.
    /// </summary>
    public void WaitForShutdown()
    {
        if (Volatile.Read(ref _disposed) == 0)
            throw new InvalidOperationException("Dispose the audio engine before waiting for shutdown.");
        if (Thread.CurrentThread != _worker) _worker.Join();
    }

    private void WorkerLoop()
    {
        bool reconcileRequested = true;
        TimeSpan nextReconcile = TimeSpan.Zero;
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    if (_enumerator is null)
                    {
                        _enumerator = new MMDeviceEnumerator();
                        _notifications = new DeviceNotifications(this);
                        _enumerator.RegisterEndpointNotificationCallback(_notifications);
                    }
                    // The host's game monitor wakes this thread; no HTTP dependency lives here.
                    if (reconcileRequested || _uptime.Elapsed >= nextReconcile)
                    {
                        Reconcile();
                        nextReconcile = _uptime.Elapsed + TimeSpan.FromMilliseconds(1500);
                    }
                }
                catch (Exception exception)
                {
                    Log("Audio engine recovery", exception);
                    StopPipeline();
                    ReleaseEnumerator();
                    ShakerSettings[] shakers;
                    lock (_configurationGate) shakers = _desired;
                    string message = Text("AudioOpenError", Describe(exception));
                    Publish(new(null, Text("AudioServiceUnavailable"), [],
                        shakers.Select(s => new ShakerStatus(s.Id, message, false, false)).ToArray(), message));
                }
                if (Volatile.Read(ref _disposed) == 0)
                    reconcileRequested = _wake.WaitOne(1500);
            }
        }
        finally
        {
            StopPipeline();
            ReleaseEnumerator();
            _wake.Dispose();
        }
    }

    private void Reconcile()
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
        {
            StopPipeline();
            _captureRetryAt = DateTime.MinValue;
            _captureError = null;
            _outputErrors.Clear();
        }
        // Read the epoch after consuming a pending reset. Otherwise a notification between
        // those operations could leave newly registered outputs muted with no reset pending.
        int epoch;
        lock (_routingGate) epoch = _sourceEpoch;

        ShakerSettings[] settings;
        long configurationVersion;
        lock (_configurationGate)
        {
            settings = _desired;
            _isPaused = _desiredPaused;
            configurationVersion = _configurationVersion;
        }
        if (configurationVersion != _appliedConfigurationVersion)
        {
            _outputErrors.Clear();
            _captureRetryAt = DateTime.MinValue;
            _appliedConfigurationVersion = configurationVersion;
        }

        var devices = new List<AudioDeviceInfo>();
        foreach (MMDevice device in _enumerator!.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                try { devices.Add(new(device.ID, device.FriendlyName)); }
                catch { /* A just-unplugged endpoint should not hide other devices. */ }
            }
        }
        devices.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        var available = new HashSet<string>(devices.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

        string? sourceId = null;
        string sourceName = Text("DefaultDeviceMissing");
        try
        {
            using MMDevice source = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            sourceId = source.ID;
            sourceName = source.FriendlyName;
        }
        catch { /* No default output is normal when the last device is disconnected. */ }

        if (_capture is not null && (!SameDevice(_capture.DeviceId, sourceId) || _capture.HasFailed))
        {
            bool failed = _capture.HasFailed;
            string? failure = _capture.FailureMessage;
            StopPipeline();
            _captureError = failed ? failure ?? Text("CaptureStopped") : null;
            _captureRetryAt = failed ? DateTime.UtcNow.AddSeconds(3) : DateTime.MinValue;
        }

        var statuses = new Dictionary<Guid, ShakerStatus>();
        var candidates = new Dictionary<Guid, ShakerSettings>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ShakerSettings shaker in settings)
        {
            string? blockReason = RoutingPolicy.GetBlockReason(shaker, sourceId, claimed);
            if (blockReason is not null) statuses[shaker.Id] = new(shaker.Id, Text(blockReason), true, false);
            else if (!shaker.Enabled) statuses[shaker.Id] = new(shaker.Id, Text("ShakerDisabled"), false, false);
            else if (string.IsNullOrWhiteSpace(shaker.DeviceId)) statuses[shaker.Id] = new(shaker.Id, Text("SelectDevice"), false, false);
            else if (_isPaused) statuses[shaker.Id] = new(shaker.Id, Text("ManuallyPaused"), true, false);
            else if (Volatile.Read(ref _simHubPauseRequested) != 0)
                statuses[shaker.Id] = new(shaker.Id,
                    Text(_simHubMonitor.State.IsAvailable ? "SimHubGameRunning" : "WaitingSimHubState"), true, false);
            else if (!available.Contains(shaker.DeviceId!)) statuses[shaker.Id] = new(shaker.Id, Text("DeviceDisconnected"), false, false);
            else if (sourceId is null) statuses[shaker.Id] = new(shaker.Id, Text("WaitingDefaultDevice"), false, false);
            else if (!ValidBand(shaker)) statuses[shaker.Id] = new(shaker.Id, Text("InvalidFrequencyBand"), false, false);
            else if (!candidates.ContainsKey(shaker.Id)) candidates.Add(shaker.Id, shaker);
        }

        foreach (var pair in _outputs.ToArray())
        {
            if (!candidates.TryGetValue(pair.Key, out ShakerSettings? shaker) ||
                !SameDevice(pair.Value.DeviceId, shaker.DeviceId) || pair.Value.HasFailed)
            {
                if (pair.Value.HasFailed)
                    _outputErrors[pair.Key] = (DateTime.UtcNow.AddSeconds(3), pair.Value.FailureMessage ?? Text("PlaybackStopped"));
                RemoveOutput(pair.Key);
            }
        }

        if (candidates.Count == 0)
        {
            StopPipeline();
            _captureError = null;
        }
        else if (!TryAcquireAudioOwnership())
        {
            string message = Text("OtherInstanceOwnsAudio");
            foreach (ShakerSettings shaker in candidates.Values)
                statuses[shaker.Id] = new(shaker.Id, message, true, false);
            Publish(new(sourceId, sourceName, devices.ToArray(), settings.Select(s => statuses[s.Id]).ToArray(), message));
            return;
        }
        else if (_capture is null && DateTime.UtcNow >= _captureRetryAt)
        {
            try
            {
                var capture = new CaptureSession(_enumerator.GetDevice(sourceId!), OnCaptureData, Signal, this);
                _capture = capture;
                capture.Start();
                _captureError = null;
            }
            catch (Exception exception)
            {
                Log("Start system audio capture", exception);
                StopPipeline();
                _captureError = Text("CaptureError", Describe(exception));
                _captureRetryAt = DateTime.UtcNow.AddSeconds(3);
            }
        }

        foreach (ShakerSettings shaker in candidates.Values)
        {
            if (_capture is null)
            {
                statuses[shaker.Id] = new(shaker.Id, Text("RetryAutomatically", _captureError ?? Text("CaptureUnavailable")), false, false);
                continue;
            }
            if (_outputErrors.TryGetValue(shaker.Id, out var error) && DateTime.UtcNow < error.RetryAt)
            {
                statuses[shaker.Id] = new(shaker.Id, Text("RetryAutomatically", error.Message), false, false);
                continue;
            }
            try
            {
                if (!_outputs.TryGetValue(shaker.Id, out OutputSession? output))
                {
                    output = new OutputSession(_enumerator.GetDevice(shaker.DeviceId!), _capture.Format.SampleRate, shaker, Signal, this);
                    _outputs.Add(shaker.Id, output);
                    PublishOutputs();
                    // A default-device notification can arrive during Init. Registration and
                    // epoch validation keep a just-created target silent in that race.
                    lock (_routingGate)
                    {
                        if (_sourceEpoch == epoch && Volatile.Read(ref _simHubPauseRequested) == 0 && Volatile.Read(ref _manualPauseRequested) == 0 && Volatile.Read(ref _disposed) == 0)
                            output.Provider.SetMuted(false);
                    }
                    output.Start();
                    _outputErrors.Remove(shaker.Id);
                }
                else output.Provider.Configure(shaker.LowCutHz, shaker.HighCutHz, shaker.Gain);
                statuses[shaker.Id] = new(shaker.Id, Text("ShakerRunning", shaker.LowCutHz.ToString("0.#"), shaker.HighCutHz.ToString("0.#")), false, true);
            }
            catch (Exception exception)
            {
                Log("Start shaker output", exception);
                RemoveOutput(shaker.Id);
                string message = Text("OutputOpenError", Describe(exception));
                _outputErrors[shaker.Id] = (DateTime.UtcNow.AddSeconds(3), message);
                statuses[shaker.Id] = new(shaker.Id, Text("RetryAutomatically", message), false, false);
            }
        }
        Publish(new(sourceId, sourceName, devices.ToArray(), settings.Select(s => statuses[s.Id]).ToArray(), _captureError));
    }

    private void OnCaptureData(float[] mono, int count)
    {
        foreach (OutputSession output in Volatile.Read(ref _publishedOutputs)) output.Provider.Enqueue(mono, 0, count);
    }

    private void SimHubStateChanged()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        bool changed;
        lock (_configurationGate)
        {
            int requested = _desiredSimHubEnabled && _simHubMonitor.State.GameRunning ? 1 : 0;
            changed = Interlocked.Exchange(ref _simHubPauseRequested, requested) != requested;
        }
        // Immediately clear all buffered output, even during a slow driver call.
        // Both transitions invalidate the epoch: a quick true/false change must
        // reopen muted providers, and must never let an old output start late.
        if (changed) InvalidateRouting();
    }

    private void InvalidateRouting()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_routingGate)
        {
            _sourceEpoch++;
            MuteOutputs();
            Interlocked.Exchange(ref _resetRequested, 1);
        }
        Signal();
    }

    private void MuteOutputs()
    {
        foreach (OutputSession output in Volatile.Read(ref _publishedOutputs)) output.Provider.SetMuted(true);
    }

    private void PublishOutputs() => Volatile.Write(ref _publishedOutputs, _outputs.Values.ToArray());

    private void RemoveOutput(Guid id)
    {
        if (!_outputs.TryGetValue(id, out OutputSession? output)) return;
        _outputs.Remove(id);
        output.Provider.SetMuted(true);
        PublishOutputs();
        output.Dispose();
    }

    private void StopPipeline()
    {
        MuteOutputs();
        CaptureSession? capture = _capture;
        _capture = null;
        capture?.Dispose();
        foreach (Guid id in _outputs.Keys.ToArray()) RemoveOutput(id);
        // Both named mutexes belong to this worker. Never release them from Dispose's
        // caller after its bounded join: drivers may still be closing on this thread.
        ReleaseAudioOwnership();
    }

    private bool TryAcquireAudioOwnership()
    {
        if (_audioOwnership is not null) return true;
        Mutex? audio = null, desktop = null;
        try
        {
            audio = TryOwnMutex(@"Local\BassRelay.AudioPlayback");
            if (audio is null) return false;
            if (_guardDesktopInstance)
            {
                // The existing standalone app (including released 1.4.1) guards its
                // lifetime with this name. Owning it also prevents an old copy starting.
                desktop = TryOwnMutex(@"Local\BassRelay.Application");
                if (desktop is null) return false;
            }
            _audioOwnership = audio;
            _desktopOwnership = desktop;
            audio = desktop = null;
            return true;
        }
        finally
        {
            ReleaseMutex(desktop);
            ReleaseMutex(audio);
        }
    }

    private static Mutex? TryOwnMutex(string name)
    {
        var mutex = new Mutex(false, name);
        bool owned = false;
        try
        {
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            return owned ? mutex : null;
        }
        finally { if (!owned) mutex.Dispose(); }
    }

    private void ReleaseAudioOwnership()
    {
        ReleaseMutex(_desktopOwnership);
        _desktopOwnership = null;
        ReleaseMutex(_audioOwnership);
        _audioOwnership = null;
    }

    private static void ReleaseMutex(Mutex? mutex)
    {
        if (mutex is null) return;
        try { mutex.ReleaseMutex(); }
        finally { mutex.Dispose(); }
    }

    private void Signal() => Signal(false);

    private void Signal(bool allowDisposed)
    {
        if (!allowDisposed && Volatile.Read(ref _disposed) != 0) return;
        try { _wake.Set(); }
        catch (ObjectDisposedException) { }
    }

    private void ReleaseEnumerator()
    {
        if (_enumerator is null) return;
        try { if (_notifications is not null) _enumerator.UnregisterEndpointNotificationCallback(_notifications); }
        catch { }
        try { _enumerator.Dispose(); }
        catch { }
        _notifications = null;
        _enumerator = null;
    }

    private void Publish(AudioEngineSnapshot snapshot)
    {
        AudioEngineSnapshot previous = Snapshot;
        if (previous.SourceDeviceId == snapshot.SourceDeviceId && previous.SourceDeviceName == snapshot.SourceDeviceName &&
            previous.Error == snapshot.Error && previous.Devices.SequenceEqual(snapshot.Devices) && previous.Shakers.SequenceEqual(snapshot.Shakers)) return;
        Volatile.Write(ref _snapshot, snapshot);
        if (Interlocked.CompareExchange(ref _notificationPending, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            AudioEngineSnapshot delivered;
            do
            {
                delivered = Snapshot;
                if (Volatile.Read(ref _disposed) == 0 && SnapshotChanged is { } handlers)
                    foreach (Action<AudioEngineSnapshot> handler in handlers.GetInvocationList())
                        try { handler(delivered); } catch { /* UI shutdown/subscriber exceptions must not kill audio. */ }
                Interlocked.Exchange(ref _notificationPending, 0);
            } while (!ReferenceEquals(delivered, Snapshot) && Interlocked.CompareExchange(ref _notificationPending, 1, 0) == 0);
        });
    }

    private static bool SameDevice(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool ValidBand(ShakerSettings settings) => FrequencyRange.IsValidBand(settings.LowCutHz, settings.HighCutHz);
    private string Describe(Exception exception) => Text("ErrorCode", exception.HResult.ToString("X8"));
    private string Text(string key, params object[] args) => _text(key, args);
    private void Log(string message, Exception? exception)
    {
        try { _log(message, exception); }
        catch { /* Host logging must not interrupt audio cleanup or recovery. */ }
    }

    private sealed class DeviceNotifications(AudioEngine owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => owner.InvalidateRouting();
        public void OnDeviceAdded(string deviceId) => owner.InvalidateRouting();
        public void OnDeviceRemoved(string deviceId) => owner.InvalidateRouting();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            if (key.Equals(PropertyKeys.PKEY_AudioEngine_DeviceFormat) || key.Equals(PropertyKeys.PKEY_AudioEndpoint_PhysicalSpeakers))
                owner.InvalidateRouting();
            else owner.Signal();
        }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) owner.InvalidateRouting();
        }
    }

    private delegate void MonoDataHandler(float[] mono, int count);

    private sealed class CaptureSession : IDisposable
    {
        private readonly MMDevice _device;
        private readonly GuardedLoopbackCapture _capture;
        private readonly MonoDataHandler _onData;
        private readonly Action _signal;
        private readonly AudioEngine _owner;
        private float[] _mono = [];
        private int _failed, _stopping;
        private string? _failureMessage;

        public CaptureSession(MMDevice device, MonoDataHandler onData, Action signal, AudioEngine owner)
        {
            _device = device;
            _onData = onData;
            _signal = signal;
            _owner = owner;
            try
            {
                DeviceId = device.ID;
                _capture = new GuardedLoopbackCapture(device);
                // Preserve the actual native mix format, especially the surround channel mask.
                // The decoder accepts both floating-point and PCM capture formats.
                Format = _capture.WaveFormat;
                _capture.DataAvailable += DataAvailable;
                _capture.RecordingStopped += RecordingStopped;
            }
            catch
            {
                _capture?.Dispose();
                device.Dispose();
                throw;
            }
        }

        public string DeviceId { get; }
        public WaveFormat Format { get; }
        public bool HasFailed => Volatile.Read(ref _failed) != 0;
        public string? FailureMessage => Volatile.Read(ref _failureMessage);
        public void Start() => _capture.StartRecording();

        private void DataAvailable(object? sender, WaveInEventArgs args)
        {
            if (Volatile.Read(ref _stopping) != 0 || args.BytesRecorded == 0) return;
            try
            {
                int frames = args.BytesRecorded / Format.BlockAlign;
                if (_mono.Length < frames) _mono = new float[frames];
                int converted = AudioSampleConverter.ToMono(args.Buffer, args.BytesRecorded, Format, _mono);
                _onData(_mono, converted);
            }
            catch (Exception exception)
            {
                _failureMessage = _owner.Text("AudioProcessingError", _owner.Describe(exception));
                Interlocked.Exchange(ref _failed, 1);
                _signal();
            }
        }

        private void RecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            _failureMessage = _owner.Text("CaptureStopped") + (args.Exception is { } error ? " " + _owner.Describe(error) : "");
            Interlocked.Exchange(ref _failed, 1);
            _signal();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            _capture.DataAvailable -= DataAvailable;
            _capture.RecordingStopped -= RecordingStopped;
            try { _capture.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
        }
    }

    private sealed class OutputSession : IDisposable
    {
        private readonly MMDevice _device;
        private readonly WasapiOut _output;
        private readonly Action _signal;
        private readonly AudioEngine _owner;
        private int _failed, _stopping;
        private string? _failureMessage;

        public OutputSession(MMDevice device, int sampleRate, ShakerSettings settings, Action signal, AudioEngine owner)
        {
            _device = device;
            _signal = signal;
            _owner = owner;
            try
            {
                DeviceId = device.ID;
                _output = new WasapiOut(device, AudioClientShareMode.Shared, true, 30);
                int channels = _output.OutputWaveFormat.Channels;
                Provider = new ShakerSampleProvider(sampleRate, channels, settings.LowCutHz, settings.HighCutHz, settings.Gain);
                Provider.SetMuted(true);
                // Shared-mode WASAPI converts the capture sample rate to the target rate.
                // Mono bass is duplicated to every target channel before this conversion.
                _output.Init(new SampleToWaveProvider(Provider));
                _output.PlaybackStopped += PlaybackStopped;
            }
            catch
            {
                _output?.Dispose();
                device.Dispose();
                throw;
            }
        }

        public string DeviceId { get; }
        public ShakerSampleProvider Provider { get; }
        public bool HasFailed => Volatile.Read(ref _failed) != 0;
        public string? FailureMessage => Volatile.Read(ref _failureMessage);
        public void Start() => _output.Play();

        private void PlaybackStopped(object? sender, StoppedEventArgs args)
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            Provider.SetMuted(true);
            _failureMessage = _owner.Text("OutputStopped") + (args.Exception is { } error ? " " + _owner.Describe(error) : "");
            Interlocked.Exchange(ref _failed, 1);
            _signal();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            Provider.SetMuted(true);
            _output.PlaybackStopped -= PlaybackStopped;
            try { _output.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
        }
    }
}
