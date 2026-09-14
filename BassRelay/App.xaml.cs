using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using BassRelay.Audio;
using BassRelay.Models;
using BassRelay.Services;
using Localization = BassRelay.Services.Localization;
using Forms = System.Windows.Forms;

namespace BassRelay;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activationWait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    private bool _ownsMutex;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _smokeTest;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayOpenItem;
    private Forms.ToolStripMenuItem? _trayPauseItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private Icon? _icon;
    private IAudioEngine? _audio;
    private AppSettings _settings = new();
    private readonly SettingsStore _store = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Localization.Apply("system");
        if (e.Args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(RequestExistingInstanceExit());
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write("UI exception", args.Exception);
            MessageBox.Show(Localization.Text("UiError", args.Exception.Message), "Bass Relay", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            ExitApplication();
        };
        _smokeTest = e.Args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase);
        if (!_smokeTest && !AcquireSingleInstance()) { Shutdown(); return; }
        try
        {
            _settings = _smokeTest ? new AppSettings { StartWithWindows = false, IsPaused = e.Args.Contains("--paused", StringComparer.OrdinalIgnoreCase) } : _store.Load();
            if (_smokeTest)
                _settings.Language = e.Args.SkipWhile(a => !a.Equals("--language", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "system";
            Localization.Apply(_settings.Language);
            _audio = new AudioEngine(simHubPort: _settings.SimHubPort);
            var window = new MainWindow(_settings, _audio, SaveSettings);
            MainWindow = window;
            window.ExitRequested += ExitApplication;
            window.PauseStateChanged += UpdateTrayPause;
            window.Closing += WindowClosing;
            CreateTray();
            window.SourceInitialized += (_, _) =>
            {
                if (_icon is not null) window.Icon = Imaging.CreateBitmapSourceFromHIcon(_icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            };
            _audio.Configure(_settings.Shakers, _settings.PrioritizeExternalAudio, _settings.IsPaused);
            if (!_smokeTest) SaveSettings();
            bool hiddenStartup = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase) && _settings.CloseToTray;
            if (!hiddenStartup) window.Show();
            if (_store.LoadWarning is not null) ShowNotification(_store.LoadWarning);
            if (_smokeTest) _ = RunSmokeTest(window, e.Args);
        }
        catch (Exception ex)
        {
            AppLog.Write("Startup exception", ex);
            if (!_smokeTest) MessageBox.Show(ex.Message, Localization.Text("StartupError"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private bool AcquireSingleInstance()
    {
        _mutex = new Mutex(true, @"Local\BassRelay.Application", out _ownsMutex);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BassRelay.Activate");
        if (!_ownsMutex) { _activateEvent.Set(); return false; }
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BassRelay.Exit");
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => { if (!_exiting) Dispatcher.BeginInvoke(ExitApplication); }, null, Timeout.Infinite, false);
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent,
            (_, _) => { if (!_exiting) Dispatcher.BeginInvoke(ShowWindow); }, null, Timeout.Infinite, false);
        return true;
    }

    // Setup uses this command to stop the tray application before updating or
    // removing files. The helper never loads settings or changes startup entries.
    private static int RequestExistingInstanceExit()
    {
        try
        {
            if (!Mutex.TryOpenExisting(@"Local\BassRelay.Application", out var instance)) return 0;
            using (instance)
            {
                bool acquired = false;
                try
                {
                    try { acquired = instance.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (acquired) return 0;
                    if (!EventWaitHandle.TryOpenExisting(@"Local\BassRelay.Exit", out var exitEvent)) return 1;
                    using (exitEvent) exitEvent.Set();
                    try { acquired = instance.WaitOne(TimeSpan.FromSeconds(10)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    return acquired ? 0 : 1;
                }
                finally { if (acquired) instance.ReleaseMutex(); }
            }
        }
        catch (UnauthorizedAccessException) { return 1; }
        catch (System.Threading.WaitHandleCannotBeOpenedException) { return 1; }
    }

    private void CreateTray()
    {
        var iconResource = GetResourceStream(new Uri("pack://application:,,,/Assets/BassRelay.ico"))
            ?? throw new InvalidOperationException(Localization.Text("MissingAppIcon"));
        using (iconResource.Stream) _icon = new Icon(iconResource.Stream, 32, 32);
        var menu = new Forms.ContextMenuStrip();
        _trayOpenItem = new Forms.ToolStripMenuItem(Localization.Text("OpenApp"), null, (_, _) => Dispatcher.Invoke(ShowWindow));
        _trayPauseItem = new Forms.ToolStripMenuItem(Localization.Text(_settings.IsPaused ? "Resume" : "Pause"), null,
            (_, _) => Dispatcher.Invoke(() => (MainWindow as BassRelay.MainWindow)?.TogglePause()));
        _trayExitItem = new Forms.ToolStripMenuItem(Localization.Text("Exit"), null, (_, _) => Dispatcher.Invoke(ExitApplication));
        menu.Items.Add(_trayOpenItem);
        menu.Items.Add(_trayPauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_trayExitItem);
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = Localization.Text("TrayTitle"), ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        Localization.Changed += UpdateTrayLanguage;
    }

    private void UpdateTrayLanguage()
    {
        if (_tray is null) return;
        if (_trayOpenItem is not null) _trayOpenItem.Text = Localization.Text("OpenApp");
        if (_trayExitItem is not null) _trayExitItem.Text = Localization.Text("Exit");
        UpdateTrayPause();
        _tray.Text = Localization.Text("TrayTitle");
    }

    private void UpdateTrayPause()
    {
        if (_trayPauseItem is not null) _trayPauseItem.Text = Localization.Text(_settings.IsPaused ? "Resume" : "Pause");
    }

    private void SaveSettings()
    {
        if (_smokeTest) return;
        try
        {
            StartupService.Apply(_settings.StartWithWindows);
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Write("Save settings/startup", ex);
            MessageBox.Show(Localization.Text("SaveSettingsError", ex.Message), "Bass Relay", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (_settings.CloseToTray)
        {
            e.Cancel = true;
            MainWindow.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ShowNotification(Localization.Text("TrayHint"));
            }
        }
        else ExitApplication();
    }

    private void ShowWindow()
    {
        if (_exiting || MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void ShowNotification(string message)
    {
        if (_smokeTest || _tray is null) return;
        _tray.ShowBalloonTip(3500, "Bass Relay", message, Forms.ToolTipIcon.Info);
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        Shutdown();
    }

    private async Task RunSmokeTest(Window window, string[] args)
    {
        try
        {
            await Task.Delay(1800);
            bool blockedRequested = args.Contains("--blocked", StringComparer.OrdinalIgnoreCase);
            bool? blockedInEngine = null, blockedInGui = null;
            bool? warningIndicatorShown = null, warningTooltipPresent = null, warningTooltipOnlyOnIndicator = null;
            if (blockedRequested)
            {
                var cards = (System.Windows.Controls.StackPanel)window.FindName("ShakerCards");
                var card = (UI.ShakerCard)cards.Children[0];
                var selector = (System.Windows.Controls.ComboBox)card.FindName("DeviceBox");
                selector.SelectedValue = _audio!.Snapshot.SourceDeviceId;
                await Task.Delay(500);
                blockedInEngine = _audio.Snapshot.Shakers.FirstOrDefault()?.IsBlocked == true;
                var processing = (FrameworkElement)card.FindName("ProcessingControls");
                var warning = (FrameworkElement)card.FindName("WarningIndicator");
                warningIndicatorShown = warning.IsEnabled && warning.IsVisible;
                warningTooltipPresent = warning.ToolTip is string explanation && !string.IsNullOrWhiteSpace(explanation);
                warningTooltipOnlyOnIndicator = processing.ToolTip is null && card.ToolTip is null;
                blockedInGui = !processing.IsEnabled && warningIndicatorShown == true &&
                    warningTooltipPresent == true && warningTooltipOnlyOnIndicator == true;
            }
            if (args.Contains("--multiple", StringComparer.OrdinalIgnoreCase))
            {
                var add = (System.Windows.Controls.Button)window.FindName("AddShakerButton");
                add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            }
            window.UpdateLayout();
            double renderedWidth = window.ActualWidth, renderedHeight = window.ActualHeight;
            string output = args.SkipWhile(a => !a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
                ?? Path.Combine(AppContext.BaseDirectory, "smoke-test.json");
            var snapshot = _audio!.Snapshot;
            var fullPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var content = (FrameworkElement)window.Content;
            int imageWidth = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
            int imageHeight = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(window.Background, null, new Rect(0, 0, imageWidth, imageHeight));
                context.DrawRectangle(new System.Windows.Media.VisualBrush(content), null,
                    new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
            }
            var visual = new System.Windows.Media.Imaging.RenderTargetBitmap(imageWidth, imageHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            visual.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(visual));
            using (var file = File.Create(Path.ChangeExtension(fullPath, ".png"))) encoder.Save(file);
            var shakerRows = (System.Windows.Controls.StackPanel)window.FindName("ShakerCards");
            int previousRows = shakerRows.Children.Count;
            var addButton = (System.Windows.Controls.Button)window.FindName("AddShakerButton");
            addButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            bool addWorks = shakerRows.Children.Count == previousRows + 1;
            // Remove the first of the now-multiple rows so the remaining placeholders
            // must be renumbered, including in the ordinary one-row smoke run.
            var extraRow = (UI.ShakerCard)shakerRows.Children[0];
            // Populate the selector without opening a route: use the protected default
            // endpoint, or a deliberately unavailable ID when Windows has no output.
            extraRow.Settings.DeviceId = snapshot.SourceDeviceId ?? "__smoke-test-unavailable-device__";
            extraRow.Settings.DeviceName = snapshot.SourceDeviceName;
            extraRow.ApplySnapshot(snapshot);
            ((System.Windows.Controls.ComboBox)extraRow.FindName("DeviceBox")).SelectedValue = "";
            bool noneRemovesExtraSelectedRow = shakerRows.Children.Count == previousRows &&
                !_settings.Shakers.Contains(extraRow.Settings);
            addButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var extraEmptyRow = (UI.ShakerCard)shakerRows.Children[shakerRows.Children.Count - 1];
            ((System.Windows.Controls.ComboBox)extraEmptyRow.FindName("DeviceBox")).SelectedValue = "";
            bool noneRemovesExtraEmptyRow = shakerRows.Children.Count == previousRows &&
                !_settings.Shakers.Contains(extraEmptyRow.Settings);
            bool placeholdersRenumbered = shakerRows.Children.Cast<UI.ShakerCard>().Select((card, index) =>
                ((System.Windows.Controls.TextBlock)card.FindName("DevicePlaceholder")).Text ==
                Localization.Text("SelectShakerDevice", index + 1)).All(correct => correct);
            // Reduce a --multiple preview to its last row, checking that deletion
            // remains available until only one row is left.
            bool rowsReduceToOne = true;
            while (shakerRows.Children.Count > 1)
            {
                int count = shakerRows.Children.Count;
                var removable = (UI.ShakerCard)shakerRows.Children[count - 1];
                ((System.Windows.Controls.ComboBox)removable.FindName("DeviceBox")).SelectedValue = "";
                if (shakerRows.Children.Count != count - 1) { rowsReduceToOne = false; break; }
            }
            var lastRow = (UI.ShakerCard)shakerRows.Children[0];
            Guid lastRowId = lastRow.Settings.Id;
            var lastSelector = (System.Windows.Controls.ComboBox)lastRow.FindName("DeviceBox");
            var placeholder = (System.Windows.Controls.TextBlock)lastRow.FindName("DevicePlaceholder");
            lastRow.Settings.DeviceId = snapshot.SourceDeviceId ?? "__smoke-test-unavailable-device__";
            lastRow.Settings.DeviceName = snapshot.SourceDeviceName;
            lastRow.ApplySnapshot(snapshot);
            window.UpdateLayout();
            bool lastRowWasSelected = lastSelector.SelectedIndex >= 0 && placeholder.Visibility != Visibility.Visible;
            lastSelector.SelectedValue = "";
            window.UpdateLayout();
            bool noneClearsLastSelectedRow = lastRowWasSelected && shakerRows.Children.Count == 1 &&
                ReferenceEquals(shakerRows.Children[0], lastRow) && lastRow.Settings.Id == lastRowId &&
                lastRow.Settings.DeviceId is null && lastRow.Settings.DeviceName is null && lastSelector.SelectedIndex == -1;
            bool placeholderVisibleAfterClearing = placeholder.IsVisible &&
                placeholder.Text == Localization.Text("SelectShakerDevice", 1);
            var lastWarning = (FrameworkElement)lastRow.FindName("WarningIndicator");
            bool warningHiddenAfterClearing = !lastWarning.IsVisible && lastWarning.Visibility == Visibility.Collapsed;
            lastSelector.SelectedValue = "";
            window.UpdateLayout();
            bool noneKeepsLastEmptyRow = shakerRows.Children.Count == 1 && _settings.Shakers.Count == 1 &&
                ReferenceEquals(shakerRows.Children[0], lastRow) && lastRow.Settings.Id == lastRowId &&
                lastRow.Settings.DeviceId is null && lastRow.Settings.DeviceName is null &&
                lastSelector.SelectedIndex == -1 && placeholder.IsVisible;
            var (newUiFeatures, uiChecks) = await VerifyNewUiFeaturesAsync(window, lastRow);
            window.Close();
            bool closeHides = !window.IsVisible && !_exiting;
            ShowWindow();
            bool reopenWorks = window.IsVisible;
            var result = new { WindowLoaded = window.IsLoaded, Language = Localization.CurrentLanguage, NewUiFeatures = newUiFeatures, UiChecks = uiChecks, Width = renderedWidth, Height = renderedHeight,
                CloseHidesToTray = closeHides, ReopenWorks = reopenWorks, TrayCreated = _tray?.Visible == true,
                AddWorks = addWorks, NoneRemovesExtraSelectedRow = noneRemovesExtraSelectedRow,
                NoneRemovesExtraEmptyRow = noneRemovesExtraEmptyRow, RowsReduceToOne = rowsReduceToOne,
                PlaceholdersRenumbered = placeholdersRenumbered,
                NoneClearsLastSelectedRow = noneClearsLastSelectedRow, NoneKeepsLastEmptyRow = noneKeepsLastEmptyRow,
                PlaceholderVisibleAfterClearing = placeholderVisibleAfterClearing,
                WarningIndicatorShown = warningIndicatorShown, WarningTooltipPresent = warningTooltipPresent,
                WarningTooltipOnlyOnIndicator = warningTooltipOnlyOnIndicator, WarningHiddenAfterClearing = warningHiddenAfterClearing,
                DefaultDeviceBlockedInEngine = blockedInEngine, DefaultDeviceBlockedInGui = blockedInGui, Audio = snapshot };
            File.WriteAllText(fullPath, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (!newUiFeatures || !closeHides || !reopenWorks || !addWorks || !noneRemovesExtraSelectedRow || !noneRemovesExtraEmptyRow ||
                !rowsReduceToOne || !placeholdersRenumbered || !noneClearsLastSelectedRow || !noneKeepsLastEmptyRow || !placeholderVisibleAfterClearing || !warningHiddenAfterClearing ||
                (blockedRequested && (blockedInEngine != true || blockedInGui != true))) { Shutdown(1); return; }
            // Exercise real close-to-exit as well as hide-to-tray in this isolated run.
            _settings.CloseToTray = false;
            window.Close();
            ExitApplication();
        }
        catch (Exception ex) { AppLog.Write("Smoke test", ex); Shutdown(1); }
    }

    private async Task<(bool Passed, object Details)> VerifyNewUiFeaturesAsync(Window window, UI.ShakerCard lastRow)
    {
        var low = (System.Windows.Controls.TextBox)lastRow.FindName("LowCutBox");
        var high = (System.Windows.Controls.TextBox)lastRow.FindName("HighCutBox");
        var validation = (System.Windows.Controls.TextBlock)lastRow.FindName("ValidationLabel");
        bool InputRejected(System.Windows.Controls.TextBox box, string text)
        {
            box.SelectAll();
            var composition = new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current, box, text);
            var input = new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, composition)
                { RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputEvent };
            box.RaiseEvent(input);
            return input.Handled;
        }
        bool PasteRejected(System.Windows.Controls.TextBox box, string text)
        {
            box.SelectAll();
            var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, text), false, DataFormats.UnicodeText);
            box.RaiseEvent(paste);
            return paste.CommandCancelled;
        }
        void PressEnter()
        {
            var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.Enter)
                { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
            high.RaiseEvent(key);
        }
        bool boundsEnforced = InputRejected(low, "-1") && InputRejected(high, "201") &&
            PasteRejected(low, "-1") && PasteRejected(high, "201") && !InputRejected(high, "200") && !PasteRejected(low, "19,5");
        low.Text = "0";
        high.Text = "200";
        PressEnter();
        bool boundariesSaved = lastRow.Settings.LowCutHz == 0 && lastRow.Settings.HighCutHz == 200;
        high.Text = "201";
        PressEnter();
        bool invalidCannotSave = lastRow.Settings.HighCutHz == 200 && validation.Visibility == Visibility.Visible;
        low.Text = "40";
        high.Text = "90";
        PressEnter();
        bool frequencyHelp = low.ToolTip is string help && help == Localization.Text("FrequencyHelp") && high.ToolTip as string == help;

        var languageMenu = (System.Windows.Controls.MenuItem)window.FindName("LanguageMenu");
        var settingsButton = (System.Windows.Controls.Button)window.FindName("SettingsButton");
        async Task OpenSettingsAsync()
        {
            settingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        string savedLanguage = _settings.Language;
        bool languagesSwitch = true;
        var languageChecks = new System.Collections.Generic.List<object>();
        foreach (string language in new[] { "ru", "en", "pt-BR", "es" })
        {
            // A ContextMenu is a detached popup. Exercise the real opening/selection
            // flow so its DynamicResource values refresh as they do for the user.
            await OpenSettingsAsync();
            languageMenu.IsSubmenuOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var item = languageMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => i.Tag as string == language);
            item.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            languageMenu.IsSubmenuOpen = false;
            settingsButton.ContextMenu.IsOpen = false;
            await OpenSettingsAsync();
            var add = (System.Windows.Controls.Button)window.FindName("AddShakerButton");
            var autoPause = (System.Windows.Controls.MenuItem)window.FindName("PrioritizeExternalAudioBox");
            var introduction = (System.Windows.Controls.TextBlock)window.FindName("IntroductionLabel");
            var simHubFeature = (System.Windows.Controls.TextBlock)window.FindName("SimHubFeatureLabel");
            languageChecks.Add(new { Language = language, CurrentLanguage = Localization.CurrentLanguage, Setting = _settings.Language,
                Add = add.Content as string, AutoPause = autoPause.Header as string, AutoPauseHelp = autoPause.ToolTip as string,
                MainHelp = introduction.ToolTip as string, VisibleSimHubFeature = simHubFeature.IsVisible, SimHubFeature = simHubFeature.Text,
                Placeholder = ((System.Windows.Controls.TextBlock)lastRow.FindName("DevicePlaceholder")).Text,
                Tray = _tray?.ContextMenuStrip?.Items[0].Text });
            languagesSwitch &= Localization.CurrentLanguage == language && _settings.Language == language &&
                add.Content as string == Localization.Text("AddShaker") &&
                autoPause.Header as string == Localization.Text("PrioritizeExternalAudio") &&
                autoPause.ToolTip as string == Localization.Text("PriorityHelp") &&
                introduction.ToolTip as string == Localization.Text("PriorityMainHelp") &&
                simHubFeature.IsVisible && simHubFeature.Text == Localization.Text("SimHubFeature") &&
                ((System.Windows.Controls.TextBlock)lastRow.FindName("DevicePlaceholder")).Text == Localization.Text("SelectShakerDevice", 1) &&
                _tray?.ContextMenuStrip?.Items[0].Text == Localization.Text("OpenApp");
            settingsButton.ContextMenu.IsOpen = false;
        }
        await OpenSettingsAsync();
        languageMenu.IsSubmenuOpen = true;
        languageMenu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => i.Tag as string == savedLanguage)
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        languageMenu.IsSubmenuOpen = false;
        settingsButton.ContextMenu.IsOpen = false;

        var first = new ShakerSettings { DeviceId = "__ui-test-target-1__", Gain = 0.37 };
        var second = new ShakerSettings { DeviceId = "__ui-test-target-2__", Gain = 0.64 };
        var cards = new[] { new UI.ShakerCard(first, () => { }, _ => { }), new UI.ShakerCard(second, () => { }, _ => { }) };
        string priorityReason = Localization.Text("SimHubGameRunning");
        var paused = new AudioEngineSnapshot("__ui-test-source__", "Test", [],
            cards.Select(c => new ShakerStatus(c.Settings.Id, priorityReason, true, false)).ToArray());
        bool priorityUi = true;
        foreach (var card in cards)
        {
            card.ApplySnapshot(paused);
            var controls = (FrameworkElement)card.FindName("ProcessingControls");
            var warning = (FrameworkElement)card.FindName("WarningIndicator");
            priorityUi &= !controls.IsEnabled && warning.IsEnabled && warning.Visibility == Visibility.Visible && warning.ToolTip as string == priorityReason;
            card.ApplySnapshot(paused with { Shakers = [new ShakerStatus(card.Settings.Id, "", false, true)] });
            priorityUi &= controls.IsEnabled && warning.Visibility == Visibility.Collapsed;
        }
        var priority = (System.Windows.Controls.MenuItem)window.FindName("PrioritizeExternalAudioBox");
        priorityUi &= first.Gain == 0.37 && second.Gain == 0.64 && _settings.PrioritizeExternalAudio && priority.IsChecked;
        bool wasPaused = _settings.IsPaused;
        var pauseButton = (System.Windows.Controls.Button)window.FindName("PauseButton");
        if (wasPaused) _trayPauseItem!.PerformClick();
        pauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        bool manualPauseUi = _settings.IsPaused && pauseButton.Content as string == Localization.Text("Resume") &&
            _trayPauseItem!.Text == Localization.Text("Resume");
        _audio!.Refresh();
        manualPauseUi &= _settings.IsPaused;
        _trayPauseItem!.PerformClick();
        manualPauseUi &= !_settings.IsPaused && pauseButton.Content as string == Localization.Text("Pause") &&
            _trayPauseItem.Text == Localization.Text("Pause");
        if (wasPaused) pauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        var details = new { BoundsEnforced = boundsEnforced, BoundariesSaved = boundariesSaved, InvalidCannotSave = invalidCannotSave,
            FrequencyHelp = frequencyHelp, LanguagesSwitch = languagesSwitch, LanguageChecks = languageChecks,
            SimHubPauseUi = priorityUi, ManualPauseUi = manualPauseUi };
        return (boundsEnforced && boundariesSaved && invalidCannotSave && frequencyHelp && languagesSwitch && priorityUi && manualPauseUi, details);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        base.OnSessionEnding(e);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _activationWait?.Unregister(null);
        Localization.Changed -= UpdateTrayLanguage;
        _exitWait?.Unregister(null);
        _audio?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _icon?.Dispose();
        _activateEvent?.Dispose();
        _exitEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

}
