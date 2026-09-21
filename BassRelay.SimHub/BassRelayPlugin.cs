using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BassRelay.Audio;
using BassRelay.Models;
using BassRelay.Services;
using GameReaderCommon;
using SimHub.Plugins;
using WoteverLocalization;
using Localization = BassRelay.Services.Localization;
using SettingsControl = BassRelaySimHub.SettingsControl;

namespace BassRelay.SimHub;

[PluginName("Bass Relay")]
[PluginAuthor("Lord-of-the-Bots")]
[PluginDescription("Movies and music through your cockpit shakers, with automatic pause during games.")]
public sealed class BassRelayPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
{
    private readonly object _settingsGate = new();
    private AppSettings _settings = new();
    private SimHubStateMonitor? _monitor;
    private IAudioEngine? _audio;
    private SettingsControl? _control;
    private Dispatcher? _dispatcher;
    private LocalizationProvider? _languageProvider;
    private string? _error;
    private int _ended = 1;

    public PluginManager PluginManager { get; set; } = null!;
    public string LeftMenuTitle => "Bass Relay";
    public ImageSource PictureIcon { get; } = CreateIcon();

    public void Init(PluginManager pluginManager)
    {
        if (Volatile.Read(ref _ended) == 0) End(PluginManager);
        PluginManager = pluginManager;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _error = null;
        Interlocked.Exchange(ref _ended, 0);
        try
        {
            _settings = this.ReadCommonSettings("GeneralSettings", () => new AppSettings()) ?? new AppSettings();
            SettingsNormalizer.Normalize(_settings);
            if (_settings.Shakers.Count == 0) _settings.Shakers.Add(new ShakerSettings());
            _languageProvider = LocalizationProvider.Instance;
            _languageProvider.LanguageChanged += HostLanguageChanged;
            ApplyHostLanguage();
            _monitor = new SimHubStateMonitor(pluginManager.LastData?.GameRunning);
            _monitor.Changed += GameStateChanged;
            _audio = new StandalonePriorityAudioEngine(_monitor, Localization.Text, Log);
            _audio.Configure(_settings.Shakers, _settings.PrioritizeExternalAudio, _settings.IsPaused);
            this.AddAction("Pause", (a, b) => Post(() => SetPaused(true)));
            this.AddAction("Resume", (a, b) => Post(() => SetPaused(false)));
            this.AddAction("TogglePause", (a, b) => Post(() => SetPaused(!_settings.IsPaused)));
            this.AttachDelegate("ManuallyPaused", () => _settings.IsPaused);
            this.AttachDelegate("AutoPaused", () => _settings.PrioritizeExternalAudio && (_monitor?.State.GameRunning ?? true));
            this.AttachDelegate("ActiveOutputs", () => _audio?.Snapshot.Shakers.Count(s => s.IsRunning) ?? 0);
            Log("SimHub plugin started", null);
        }
        catch (Exception exception)
        {
            _error = Localization.Text("PluginStartError", exception.Message);
            DetachLanguageProvider();
            StopAudio();
            Log("Start SimHub plugin", exception);
        }
    }

    public void DataUpdate(PluginManager pluginManager, ref GameData data)
    {
        if (Volatile.Read(ref _ended) != 0 || data is null) return;
        _monitor?.Update(data.GameRunning);
    }

    public Control GetWPFSettingsControl(PluginManager pluginManager)
    {
        var audio = _audio;
        if (audio is not null && Volatile.Read(ref _ended) == 0)
        {
            try
            {
                if (_control is not null) return _control;
                var control = new SettingsControl(_settings, audio, SaveSettings, SetPaused, HostCulture,
                    _monitor?.State ?? new SimHubGameState(true, false));
                if (Volatile.Read(ref _ended) == 0) return _control = control;
                control.Detach();
            }
            catch (Exception exception)
            {
                _error = Localization.Text("PluginStartError", exception.Message);
                Log("Create SimHub settings page", exception);
            }
        }
        return new UserControl { Content = new TextBlock
        {
            Text = _error ?? Localization.Text("PluginStopped"), Margin = new Thickness(20),
            TextWrapping = TextWrapping.Wrap
        } };
    }

    private void SetPaused(bool paused)
    {
        if (Volatile.Read(ref _ended) != 0) return;
        _settings.IsPaused = paused;
        _audio?.Configure(_settings.Shakers, _settings.PrioritizeExternalAudio, paused);
        try { SaveSettings(); }
        finally { _control?.RefreshPauseState(); }
    }

    private CultureInfo HostCulture => _languageProvider?.CurrentLanguage?.Culture ?? CultureInfo.GetCultureInfo("en");

    private void HostLanguageChanged(object? sender, LangageChangedEventArgs args) => Post(ApplyHostLanguage);

    private void GameStateChanged() => Post(() =>
        _control?.RefreshGameState(_monitor?.State ?? new SimHubGameState(true, false)));

    private void ApplyHostLanguage()
    {
        if (_control is null)
        {
            Localization.Apply(HostCulture);
            _audio?.Refresh();
        }
        else _control.RefreshLanguage(HostCulture);
    }

    private void DetachLanguageProvider()
    {
        var provider = _languageProvider;
        _languageProvider = null;
        if (provider is not null) provider.LanguageChanged -= HostLanguageChanged;
    }

    private void SaveSettings()
    {
        try
        {
            lock (_settingsGate)
            {
                SettingsNormalizer.Normalize(_settings);
                this.SaveCommonSettings("GeneralSettings", _settings);
            }
            _error = null;
        }
        catch (Exception exception)
        {
            Log("Save SimHub settings", exception);
            // Keep working settings in memory; the user must know persistence failed.
            throw new InvalidOperationException(Localization.Text("SaveSettingsError", exception.Message), exception);
        }
    }

    public void End(PluginManager pluginManager)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        DetachLanguageProvider();
        // Always stop audio first. A closed or blocked UI must not keep it playing.
        StopAudio();
        var control = _control;
        _control = null;
        if (control is not null)
        {
            Action detach = () =>
            {
                try { control.Detach(); }
                catch (Exception exception) { Log("Detach SimHub settings page", exception); }
            };
            try
            {
                if (control.Dispatcher.CheckAccess()) detach();
                else if (!control.Dispatcher.HasShutdownStarted) control.Dispatcher.BeginInvoke(detach);
            }
            catch (InvalidOperationException) { /* The host dispatcher has already stopped. */ }
        }
        Log("SimHub plugin stopped", null);
    }

    private void StopAudio()
    {
        var audio = Interlocked.Exchange(ref _audio, null);
        var monitor = Interlocked.Exchange(ref _monitor, null);
        if (monitor is not null) monitor.Changed -= GameStateChanged;
        try { audio?.Dispose(); }
        catch (Exception exception) { Log("Stop audio", exception); }
        try { monitor?.Dispose(); }
        catch (Exception exception) { Log("Stop game state monitor", exception); }
    }

    private void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || Volatile.Read(ref _ended) != 0) return;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (Volatile.Read(ref _ended) != 0) return;
                try { action(); } catch (Exception exception) { Log("SimHub action", exception); }
            }));
        }
        catch (InvalidOperationException) { }
    }

    private static void Log(string message, Exception? exception)
    {
        try
        {
            if (exception is null) global::SimHub.Logging.Current.Info("Bass Relay: " + message);
            else global::SimHub.Logging.Current.Error("Bass Relay: " + message, exception);
        }
        catch { /* Logging must not interrupt the host or audio. */ }
    }

    private static ImageSource CreateIcon()
    {
        var pen = new Pen(Brushes.White, 1.8);
        var drawing = new GeometryDrawing(null, pen,
            Geometry.Parse("M1,12 L6,12 9,3 13,21 17,7 20,12 23,12"));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
