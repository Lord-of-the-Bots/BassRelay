using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BassRelay.Audio;
using BassRelay.Models;
using BassRelay.Services;
using BassRelay.SimHub;
using BassRelay.UI;
using Localization = BassRelay.Services.Localization;

namespace BassRelaySimHub;

public partial class SettingsControl : UserControl
{
    private readonly AppSettings _settings;
    private readonly IAudioEngine _audio;
    private readonly Action _saveSettings;
    private readonly Action<bool> _setPaused;
    private readonly List<ShakerCard> _cards = new();
    private AudioEngineSnapshot _snapshot;
    private SimHubGameState _gameState;
    private string? _interactionError;
    private bool _ready;
    private bool _detached;

    public SettingsControl(AppSettings settings, IAudioEngine audio, Action saveSettings, Action<bool> setPaused,
        CultureInfo culture, SimHubGameState? gameState = null)
    {
        _settings = settings;
        _audio = audio;
        _saveSettings = saveSettings;
        _setPaused = setPaused;
        _snapshot = audio.Snapshot;
        _gameState = gameState ?? new SimHubGameState(true, false);
        InitializeComponent();
        Localization.Apply(culture, Resources);
        AutoPauseBox.IsChecked = settings.PrioritizeExternalAudio;
        RefreshPauseState();
        if (settings.Shakers.Count == 0)
            settings.Shakers.Add(new ShakerSettings());
        foreach (var shaker in settings.Shakers)
            AddCard(shaker);
        UpdateCardNumbers();
        _audio.SnapshotChanged += AudioSnapshotChanged;
        ApplySnapshot(_audio.Snapshot);
        _ready = true;
    }

    public void RefreshPauseState()
    {
        if (_detached) return;
        PauseButton.SetResourceReference(ContentControl.ContentProperty, _settings.IsPaused ? "ResumeRelay" : "PauseRelay");
        PauseButton.SetResourceReference(ToolTipProperty, _settings.IsPaused ? "ResumeHelp" : "ManualPauseHelp");
        UpdatePlaybackStatus();
    }

    public void RefreshGameState(SimHubGameState state)
    {
        if (_detached) return;
        _gameState = state;
        UpdatePlaybackStatus();
    }

    public void RefreshLanguage(CultureInfo culture)
    {
        if (_detached) return;
        Localization.Apply(culture, Resources);
        RefreshPauseState();
        foreach (var card in _cards)
            card.RefreshLanguage();
        UpdateCardNumbers();
        UpdateSourceLabel();
        if (_ready)
            _audio.Refresh();
    }

    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        _ready = false;
        _audio.SnapshotChanged -= AudioSnapshotChanged;
        IsEnabled = false;
    }

    private void AddCard(ShakerSettings shaker)
    {
        var card = new ShakerCard(shaker, RoutesChanged, RemoveCard,
            (DataTemplate)Resources["DeviceNameTemplate"]) { Style = null };
        _cards.Add(card);
        ShakerCards.Children.Add(card);
        card.ApplySnapshot(_snapshot);
    }

    private void AddShakerClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var shaker = new ShakerSettings();
        _settings.Shakers.Add(shaker);
        AddCard(shaker);
        UpdateCardNumbers();
        RoutesChanged();
        _cards[_cards.Count - 1].FocusDevice();
    }

    private void RemoveCard(ShakerCard card)
    {
        if (!_ready) return;
        if (_cards.Count == 1)
        {
            card.ClearDevice();
            RoutesChanged();
            return;
        }
        _settings.Shakers.Remove(card.Settings);
        _cards.Remove(card);
        ShakerCards.Children.Remove(card);
        UpdateCardNumbers();
        RoutesChanged();
    }

    private void UpdateCardNumbers()
    {
        for (int index = 0; index < _cards.Count; index++)
        {
            _cards[index].SetNumber(index + 1);
            _cards[index].Tag = Localization.Text("ShakerNumber", index + 1);
        }
    }

    private void RoutesChanged()
    {
        if (!_ready) return;
        _audio.Configure(_settings.Shakers.Select(shaker => shaker.Copy()).ToArray(),
            _settings.PrioritizeExternalAudio, _settings.IsPaused);
        SafeSave();
        foreach (var card in _cards)
            card.ApplySnapshot(_snapshot);
        UpdatePlaybackStatus();
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.PrioritizeExternalAudio = AutoPauseBox.IsChecked == true;
        RoutesChanged();
    }

    private void PauseClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // The plugin owns the paused state so SDK actions and this button stay in sync.
        try
        {
            _setPaused(!_settings.IsPaused);
            _interactionError = null;
        }
        catch (Exception exception)
        {
            _interactionError = exception.Message;
        }
        finally
        {
            RefreshPauseState();
            UpdateError();
        }
    }

    private void AudioSnapshotChanged(AudioEngineSnapshot snapshot)
    {
        if (_detached || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess())
            ApplySnapshot(snapshot);
        else
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => { if (!_detached) ApplySnapshot(snapshot); }));
            }
            catch (InvalidOperationException) when (_detached || Dispatcher.HasShutdownStarted)
            {
                // The host can shut down between the dispatcher check and this callback.
            }
        }
    }

    private void ApplySnapshot(AudioEngineSnapshot snapshot)
    {
        _snapshot = snapshot;
        UpdateError();
        foreach (var card in _cards)
            card.ApplySnapshot(snapshot);
        UpdatePlaybackStatus();
        UpdateSourceLabel();
    }

    private void UpdatePlaybackStatus()
    {
        string key;
        if (WaitingForStandalone) key = "StatusWaitingTakeover";
        else if (_settings.IsPaused) key = "StatusManualPause";
        else if (!_settings.Shakers.Any(shaker => !string.IsNullOrWhiteSpace(shaker.DeviceId))) key = "StatusChooseDevice";
        else if (_settings.PrioritizeExternalAudio && !_gameState.IsAvailable) key = "StatusWaitingGame";
        else if (_settings.PrioritizeExternalAudio && _gameState.GameRunning) key = "StatusGamePause";
        else if (_snapshot.Shakers.Any(shaker => shaker.IsRunning)) key = "StatusPlaying";
        else key = "StatusWaitingOutput";
        PlaybackStatus.Text = Localization.Text(key);
    }

    private bool WaitingForStandalone => (_audio as IStandalonePriorityStatus)?.WaitingForStandalone == true;

    private void UpdateSourceLabel()
    {
        SourceLabel.Visibility = WaitingForStandalone ? Visibility.Collapsed : Visibility.Visible;
        SourceLabel.Text = Localization.Text("SourceOutput", _snapshot.SourceDeviceName);
    }

    private void SafeSave()
    {
        try
        {
            _saveSettings();
            _interactionError = null;
        }
        catch (Exception exception)
        {
            _interactionError = exception.Message;
        }
        UpdateError();
    }

    private void UpdateError()
    {
        string? error = _interactionError ?? _snapshot.Error;
        ErrorLabel.Text = error ?? "";
        ErrorLabel.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
    }
}
