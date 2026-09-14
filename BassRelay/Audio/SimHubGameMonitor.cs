using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BassRelay.Audio;

public sealed record SimHubGameState(bool GameRunning, bool IsAvailable);

public interface ISimHubGameMonitor : IDisposable
{
    event Action? Changed;
    SimHubGameState State { get; }
    void SetEnabled(bool enabled);
}

/// <summary>
/// Reads SimHub's GameRunning flag away from the audio/UI threads. Audio levels,
/// ShakeIt configuration, selected devices and GamePaused never determine this state.
/// </summary>
public sealed class SimHubGameMonitor : ISimHubGameMonitor
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly TimeSpan _pollInterval, _requestTimeout, _disconnectGrace;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _worker;
    private SimHubGameState _state = new(false, false);
    private bool _enabled, _disposed;
    private long _generation;
    private TimeSpan? _lastSuccess;

    public SimHubGameMonitor(int port = 8888, HttpMessageHandler? handler = null,
        TimeSpan? pollInterval = null, TimeSpan? requestTimeout = null, TimeSpan? disconnectGrace = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _pollInterval = Positive(pollInterval ?? TimeSpan.FromMilliseconds(500), nameof(pollInterval));
        _requestTimeout = Positive(requestTimeout ?? TimeSpan.FromSeconds(1), nameof(requestTimeout));
        _disconnectGrace = Positive(disconnectGrace ?? TimeSpan.FromSeconds(5), nameof(disconnectGrace));
        _endpoint = new Uri($"http://127.0.0.1:{port}/api/GetGamedata");
        _client = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        _worker = Task.Run(RunAsync);
    }

    public event Action? Changed;
    public SimHubGameState State => Volatile.Read(ref _state);

    public void SetEnabled(bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            _generation++;
            _lastSuccess = null;
            changed = SetState(new(false, false));
        }
        if (changed) NotifyChanged();
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                bool enabled;
                long generation;
                lock (_gate) { enabled = _enabled; generation = _generation; }
                if (enabled)
                {
                    bool? running = await ReadGameRunningAsync(_shutdown.Token).ConfigureAwait(false);
                    bool changed = false;
                    lock (_gate)
                    {
                        // A disabled/re-enabled monitor must not accept the old request.
                        if (!_disposed && _enabled && _generation == generation)
                        {
                            TimeSpan now = _clock.Elapsed;
                            if (running.HasValue)
                            {
                                _lastSuccess = now;
                                changed = SetState(new(running.Value, true));
                            }
                            else
                            {
                                bool retained = _state.GameRunning && _lastSuccess is { } last && now - last < _disconnectGrace;
                                changed = SetState(new(retained, false));
                            }
                        }
                    }
                    if (changed) NotifyChanged();
                }
                await _wake.WaitAsync(enabled ? _pollInterval : Timeout.InfiniteTimeSpan, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _client.Dispose();
            _wake.Dispose();
        }
    }

    private async Task<bool?> ReadGameRunningAsync(CancellationToken shutdown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(_endpoint, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            // SimHub serializes JSON as UTF-8. Read bytes so a broken HTTP charset
            // header cannot fault the monitor and leave an old game state stuck.
            byte[] json = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("GameRunning", out JsonElement value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException) { }
        return null;
    }

    // Called under _gate; callbacks run outside it and never touch HTTP/audio lifetimes.
    private bool SetState(SimHubGameState state)
    {
        if (_state == state) return false;
        Volatile.Write(ref _state, state);
        return true;
    }

    private void NotifyChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* A shutting-down subscriber cannot stop monitoring. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
        }
        _ = _worker.ContinueWith(_ => _shutdown.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        // Cancellation and final resource disposal happen on the background task.
        // Never wait for a web request from an audio callback or the UI thread.
        GC.SuppressFinalize(this);
    }

    private static TimeSpan Positive(TimeSpan value, string parameter) =>
        value > TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue ? value : throw new ArgumentOutOfRangeException(parameter);
}
