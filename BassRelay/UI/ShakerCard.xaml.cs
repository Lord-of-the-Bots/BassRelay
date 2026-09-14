using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BassRelay.Audio;
using BassRelay.Models;
using BassRelay.Services;
using Localization = BassRelay.Services.Localization;

namespace BassRelay.UI;

public partial class ShakerCard : UserControl
{
    private readonly Action _changed;
    private readonly Action<ShakerCard> _remove;
    private bool _updating;
    private bool _removing;
    private int _number = 1;
    private AudioEngineSnapshot? _snapshot;
    private static readonly Brush Invalid = new SolidColorBrush(Color.FromRgb(182, 51, 51));
    private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(201, 209, 218));

    public ShakerSettings Settings { get; }

    public ShakerCard(ShakerSettings settings, Action changed, Action<ShakerCard> remove)
    {
        Settings = settings;
        _changed = changed;
        _remove = remove;
        _updating = true;
        InitializeComponent();
        SetNumber(1);
        ShowSavedNumbers();
        GainSlider.Value = Math.Clamp(settings.Gain * 100, 0, 100);
        GainValueLabel.Text = $"{Math.Round(GainSlider.Value):0} %";
        _updating = false;
    }

    public void SetNumber(int number)
    {
        _number = number;
        DevicePlaceholder.Text = Localization.Text("SelectShakerDevice", number);
        System.Windows.Automation.AutomationProperties.SetName(DeviceBox, DevicePlaceholder.Text);
    }
    public void FocusDevice() => DeviceBox.Focus();

    public void RefreshLanguage()
    {
        SetNumber(_number);
        if (ValidationLabel.Visibility == Visibility.Visible)
            ValidationLabel.Text = Localization.Text("FrequencyValidation");
        if (_snapshot is not null) ApplySnapshot(_snapshot);
    }

    public void ClearDevice()
    {
        _removing = false;
        Settings.DeviceId = null;
        Settings.DeviceName = null;
        ShowSavedNumbers();
        ClearValidation();
        if (_snapshot is not null) ApplySnapshot(_snapshot);
    }

    public void ApplySnapshot(AudioEngineSnapshot snapshot)
    {
        if (_removing)
            return;
        _snapshot = snapshot;
        _updating = true;
        var devices = new List<AudioDeviceInfo> { new("", Localization.Text("NoneSelected")) };
        devices.AddRange(snapshot.Devices);
        if (!string.IsNullOrEmpty(Settings.DeviceId) && devices.All(d => !string.Equals(d.Id, Settings.DeviceId, StringComparison.OrdinalIgnoreCase)))
            devices.Add(new AudioDeviceInfo(Settings.DeviceId, Localization.Text("UnavailableDevice", Settings.DeviceName ?? Localization.Text("SoundCard"))));
        var current = DeviceBox.ItemsSource as IReadOnlyList<AudioDeviceInfo>;
        if (current is null || !current.SequenceEqual(devices))
            DeviceBox.ItemsSource = devices;
        if (string.IsNullOrEmpty(Settings.DeviceId))
            DeviceBox.SelectedIndex = -1;
        else
            DeviceBox.SelectedValue = devices.FirstOrDefault(d => string.Equals(d.Id, Settings.DeviceId, StringComparison.OrdinalIgnoreCase))?.Id;
        var status = snapshot.Shakers.FirstOrDefault(s => s.Id == Settings.Id);
        bool sameDevice = !string.IsNullOrEmpty(Settings.DeviceId) && string.Equals(Settings.DeviceId, snapshot.SourceDeviceId, StringComparison.OrdinalIgnoreCase);
        bool blocked = !string.IsNullOrEmpty(Settings.DeviceId) && (sameDevice || status?.IsBlocked == true);
        ProcessingControls.IsEnabled = !blocked;
        ProcessingControls.Opacity = ProcessingControls.IsEnabled ? 1 : 0.48;
        string? warning = null;
        if (sameDevice)
        {
            warning = Localization.Text("SameDeviceHelp");
        }
        else if (!string.IsNullOrEmpty(Settings.DeviceId) && status is { IsRunning: false } &&
                 !string.IsNullOrWhiteSpace(status.Message) && !Localization.IsText("ShakerDisabled", status.Message) &&
                 !Localization.IsText("ConnectingDevice", status.Message) &&
                 !Localization.IsText("SelectDevice", status.Message))
        {
            warning = status.Message;
        }
        WarningIndicator.ToolTip = warning;
        WarningIndicator.Visibility = string.IsNullOrWhiteSpace(warning) ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(WarningIndicator, warning ?? "");
        _updating = false;
    }

    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _removing || DeviceBox.SelectedItem is not AudioDeviceInfo device)
            return;
        if (string.IsNullOrEmpty(device.Id))
        {
            _removing = true;
            _remove(this);
            return;
        }
        Settings.DeviceId = device.Id;
        Settings.DeviceName = device.Name;
        Settings.Enabled = true;
        _changed();
        if (_snapshot is not null)
            ApplySnapshot(_snapshot);
    }

    private void GainValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating)
            GainValueLabel.Text = $"{Math.Round(GainSlider.Value):0} %";
    }

    private void GainMouseReleased(object sender, MouseButtonEventArgs e) => SaveGain();
    private void GainKeyReleased(object sender, KeyEventArgs e) => SaveGain();
    private void GainLostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveGain();

    private void SaveGain()
    {
        if (_updating || _removing)
            return;
        double gain = Math.Round(GainSlider.Value) / 100;
        if (Math.Abs(Settings.Gain - gain) < 0.000001)
            return;
        Settings.Gain = gain;
        _changed();
    }

    private void NumericLostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveNumbers();

    private void FrequencyTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is TextBox box) e.Handled = !CanInsertFrequencyText(box, e.Text);
    }

    private void FrequencyPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox box || !e.DataObject.GetDataPresent(DataFormats.UnicodeText) ||
            e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !CanInsertFrequencyText(box, text))
            e.CancelCommand();
    }

    private static bool CanInsertFrequencyText(TextBox box, string insertion)
    {
        string text = box.Text.Remove(box.SelectionStart, box.SelectionLength).Insert(box.SelectionStart, insertion);
        if (text.Length > box.MaxLength || text.Any(c => !char.IsAsciiDigit(c) && c != '.' && c != ',')) return false;
        if (text.Count(c => c == '.' || c == ',') > 1) return false;
        if (text is "" or "." or ",") return true;
        return TryNumber(text, out double value) && value >= FrequencyRange.Minimum && value <= FrequencyRange.Maximum;
    }

    private void NumericKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveNumbers();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ShowSavedNumbers();
            ClearValidation();
            e.Handled = true;
        }
    }

    private void SaveNumbers()
    {
        if (_updating || _removing)
            return;
        bool lowValid = TryNumber(LowCutBox.Text, out double low) && low >= FrequencyRange.Minimum && low <= FrequencyRange.Maximum;
        bool highValid = TryNumber(HighCutBox.Text, out double high) && high >= FrequencyRange.Minimum && high <= FrequencyRange.Maximum;
        bool rangeValid = lowValid && highValid && FrequencyRange.IsValidBand(low, high);
        LowCutBox.BorderBrush = lowValid && rangeValid ? NormalBorder : Invalid;
        HighCutBox.BorderBrush = highValid && rangeValid ? NormalBorder : Invalid;
        if (!rangeValid)
        {
            ValidationLabel.Text = Localization.Text("FrequencyValidation");
            ValidationLabel.Visibility = Visibility.Visible;
            return;
        }
        ClearValidation();
        if (Settings.LowCutHz == low && Settings.HighCutHz == high)
            return;
        Settings.LowCutHz = low;
        Settings.HighCutHz = high;
        _changed();
    }

    private static bool TryNumber(string value, out double number) =>
        double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    private void ShowSavedNumbers()
    {
        LowCutBox.Text = Settings.LowCutHz.ToString("0.##", CultureInfo.CurrentCulture);
        HighCutBox.Text = Settings.HighCutHz.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void ClearValidation()
    {
        ValidationLabel.Visibility = Visibility.Collapsed;
        LowCutBox.BorderBrush = NormalBorder;
        HighCutBox.BorderBrush = NormalBorder;
    }
}
