using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

[assembly: InternalsVisibleTo("BassRelay.Tests")]

namespace BassRelay.Audio;

/// <summary>A small seam for deterministic capture-lifecycle tests without opening hardware.</summary>
internal interface ILoopbackCaptureBackend : IDisposable
{
    WaveFormat WaveFormat { get; }
    int PollIntervalMilliseconds { get; }
    void Start();
    void Drain(Action<byte[], int> onData);
    void Stop();
}

/// <summary>
/// Owns the capture thread so even errors from AudioClient.Stop during an audio-service
/// restart are reported to the engine. NAudio 2.2.1 WasapiCapture calls Stop outside its
/// exception guard; an exception there would otherwise terminate the application.
/// </summary>
internal sealed class GuardedLoopbackCapture : IDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly ILoopbackCaptureBackend _backend;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private int _stopRequested, _disposeRequested, _cleanupDone;

    public GuardedLoopbackCapture(MMDevice device) : this(new WasapiLoopbackBackend(device)) { }

    internal GuardedLoopbackCapture(ILoopbackCaptureBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public WaveFormat WaveFormat => _backend.WaveFormat;
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposeRequested) != 0) throw new ObjectDisposedException(nameof(GuardedLoopbackCapture));
            if (_thread is not null) throw new InvalidOperationException("Захват уже запущен.");
            var thread = new Thread(CaptureLoop) { IsBackground = true, Name = "BassRelay loopback capture" };
            thread.SetApartmentState(ApartmentState.MTA);
            _thread = thread;
            try { thread.Start(); }
            catch
            {
                _thread = null;
                throw;
            }
        }
    }

    private void CaptureLoop()
    {
        Exception? failure = null;
        try
        {
            if (Volatile.Read(ref _stopRequested) == 0)
            {
                _backend.Start();
                while (Volatile.Read(ref _stopRequested) == 0)
                {
                    _wake.WaitOne(Numeric.Clamp(_backend.PollIntervalMilliseconds, 1, 100));
                    if (Volatile.Read(ref _stopRequested) != 0) break;
                    _backend.Drain(OnData);
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            // Stop can itself fail after a USB disconnect or Windows Audio service restart.
            // Keep the original capture error when both operations fail.
            try { _backend.Stop(); }
            catch (Exception exception) { failure ??= exception; }

            if (RecordingStopped is { } handlers)
            {
                var args = new StoppedEventArgs(failure);
                foreach (EventHandler<StoppedEventArgs> handler in handlers.GetInvocationList())
                    try { handler(this, args); } catch { /* Never let an observer kill the capture thread. */ }
            }
            // Dispose may be called by an observer on this thread. Cleanup is deferred until
            // Stop and completion callbacks have finished in that case.
            if (Volatile.Read(ref _disposeRequested) != 0) Cleanup();
        }
    }

    private void OnData(byte[] buffer, int bytes)
    {
        if (Volatile.Read(ref _stopRequested) == 0 && bytes > 0)
            DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycleGate)
        {
            Interlocked.Exchange(ref _disposeRequested, 1);
            Interlocked.Exchange(ref _stopRequested, 1);
            thread = _thread;
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }
        // No lock is held while waiting for callbacks. If an observer requests disposal from
        // this thread, its finally block will perform cleanup instead of joining itself.
        if (thread == Thread.CurrentThread) return;
        thread?.Join();
        Cleanup();
    }

    private void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanupDone, 1) != 0) return;
        try { _backend.Dispose(); } catch { }
        _wake.Dispose();
    }

    private sealed class WasapiLoopbackBackend : ILoopbackCaptureBackend
    {
        private readonly AudioClient _audioClient;
        private readonly AudioCaptureClient _captureClient;
        private byte[] _buffer = [];

        public WasapiLoopbackBackend(MMDevice device)
        {
            _audioClient = device.AudioClient;
            try
            {
                WaveFormat = _audioClient.MixFormat;
                _audioClient.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback,
                    20 * 10000L, 0, WaveFormat, Guid.Empty);
                _captureClient = _audioClient.AudioCaptureClient;
                _buffer = new byte[checked(_audioClient.BufferSize * WaveFormat.BlockAlign)];
            }
            catch
            {
                try { _audioClient.Dispose(); } catch { }
                throw;
            }
        }

        public WaveFormat WaveFormat { get; }
        public int PollIntervalMilliseconds => 10;
        public void Start() => _audioClient.Start();
        public void Stop() => _audioClient.Stop();
        public void Dispose() => _audioClient.Dispose();

        public void Drain(Action<byte[], int> onData)
        {
            // Bound one polling pass so shutdown still gets a chance if a driver continually
            // advertises packets. A normal endpoint supplies only one or two packets per pass.
            for (int packet = 0; packet < 64 && _captureClient.GetNextPacketSize() > 0; packet++)
            {
                IntPtr data = _captureClient.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                int bytes;
                try
                {
                    bytes = checked(frames * WaveFormat.BlockAlign);
                    if (_buffer.Length < bytes) _buffer = new byte[bytes];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_buffer, 0, bytes);
                    else if (bytes > 0) Marshal.Copy(data, _buffer, 0, bytes);
                }
                finally
                {
                    // Always release an acquired native buffer, even when copying fails.
                    _captureClient.ReleaseBuffer(frames);
                }
                // Managed subscribers never execute while holding a WASAPI buffer.
                if (bytes > 0) onData(_buffer, bytes);
            }
        }
    }
}
