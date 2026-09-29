using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BassRelay.Audio;
using BassRelay.Models;
using Localization = BassRelay.Services.Localization;

#if NETFRAMEWORK
namespace BassRelaySimHub.UI;
#else
namespace BassRelay.UI;
#endif

public partial class ChannelMapperWindow : Window
{
    private readonly AudioDeviceInfo _device;
    private readonly Func<AudioDeviceInfo?> _currentDevice;
    private readonly List<ChannelRow> _rows;
    private bool _layoutChanged;
    private string? _errorKey;

    public List<ChannelMappingSettings>? Result { get; private set; }

    public ChannelMapperWindow(FrameworkElement resourceScope, AudioDeviceInfo device, ShakerSettings settings,
        Func<AudioDeviceInfo?> currentDevice)
    {
        _device = device;
        _currentDevice = currentDevice;
        InitializeComponent();
        Owner = GetWindow(resourceScope);
        // Plugin strings live on SettingsControl; a separate window has no logical parent there.
        // Retain the dictionaries so subsequent host language changes update dynamic resources too.
        var resourceDictionaries = new Stack<ResourceDictionary>();
        for (DependencyObject? current = resourceScope; current is not null;)
        {
            if (current is FrameworkElement element &&
                (element.Resources.Count > 0 || element.Resources.MergedDictionaries.Count > 0))
                resourceDictionaries.Push(element.Resources);
            current = LogicalTreeHelper.GetParent(current) ??
                (current is Visual ? VisualTreeHelper.GetParent(current) : null);
        }
        foreach (var dictionary in resourceDictionaries)
            Resources.MergedDictionaries.Add(dictionary);
        DeviceNameLabel.Text = device.Name;
        _rows = CreateMappings(device, settings).Select(mapping => new ChannelRow(mapping)).ToList();
        foreach (var row in _rows) row.PropertyChanged += RowChanged;
        ChannelRows.ItemsSource = _rows;
        UpdateSilenceLabel();
        MaxHeight = Math.Max(280, Math.Min(MaxHeight, SystemParameters.WorkArea.Height - 40));
    }

    internal static List<ChannelMappingSettings> CreateMappings(AudioDeviceInfo device, ShakerSettings settings)
    {
        bool firstMapping = settings.ChannelMappings.Count == 0 && !settings.CustomChannelMapping;
        return AudioChannelLayout.GetChannels(device.ChannelCount, device.ChannelMask).Select(channel =>
        {
            var saved = AudioChannelLayout.FindMapping(channel, settings.ChannelMappings);
            return new ChannelMappingSettings
            {
                ChannelIndex = channel.Index,
                SpeakerMask = channel.SpeakerMask,
                Enabled = saved?.Enabled ?? firstMapping,
                LowCutHz = saved?.LowCutHz ?? settings.LowCutHz,
                HighCutHz = saved?.HighCutHz ?? settings.HighCutHz
            };
        }).ToList();
    }

    public void ApplyDeviceSnapshot(AudioDeviceInfo? device)
    {
        if (_layoutChanged) return;
        if (device is not null && string.Equals(device.Id, _device.Id, StringComparison.OrdinalIgnoreCase) &&
            device.ChannelCount == _device.ChannelCount && device.ChannelMask == _device.ChannelMask) return;
        // Never save labels from an old channel layout, even if the endpoint reconnects later.
        _layoutChanged = true;
        ChannelRows.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ShowError("ChannelMapperDeviceChanged");
    }

    public void RefreshLanguage()
    {
        foreach (var row in _rows) row.RefreshLabel();
        if (_errorKey is not null) ShowError(_errorKey);
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelRow.Enabled)) UpdateSilenceLabel();
        if (!_layoutChanged && _rows.All(row => !row.HasError))
        {
            _errorKey = null;
            ErrorLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSilenceLabel() =>
        SilenceLabel.Visibility = _rows.Any(row => row.Enabled) ? Visibility.Collapsed : Visibility.Visible;

    private void ShowError(string key)
    {
        _errorKey = key;
        ErrorLabel.Text = Localization.Text(key);
        ErrorLabel.Visibility = Visibility.Visible;
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        ApplyDeviceSnapshot(_currentDevice());
        if (_layoutChanged) return;
        bool valid = true;
        var result = new List<ChannelMappingSettings>();
        foreach (var row in _rows)
        {
            bool lowValid = TryNumber(row.LowText, out double low);
            bool highValid = TryNumber(row.HighText, out double high);
            bool validBand = lowValid && highValid && FrequencyRange.IsValidBand(low, high);
            row.HasError = row.Enabled && !validBand;
            valid &= !row.HasError;
            var mapping = row.Original.Copy();
            mapping.Enabled = row.Enabled;
            if (validBand)
            {
                mapping.LowCutHz = low;
                mapping.HighCutHz = high;
            }
            result.Add(mapping);
        }
        if (!valid)
        {
            ShowError("InvalidFrequencyBand");
            return;
        }
        Result = result;
        DialogResult = true;
    }

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
        if (text.Length > box.MaxLength || text.Any(c => (c < '0' || c > '9') && c != '.' && c != ',')) return false;
        if (text.Count(c => c == '.' || c == ',') > 1) return false;
        if (text is "" or "." or ",") return true;
        return TryNumber(text, out double value) && value >= FrequencyRange.Minimum && value <= FrequencyRange.Maximum;
    }

    private static bool TryNumber(string value, out double number) =>
        double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out number) && !double.IsNaN(number) && !double.IsInfinity(number);

    private sealed class ChannelRow : INotifyPropertyChanged
    {
        private bool _enabled;
        private bool _hasError;
        private string _lowText;
        private string _highText;
        public event PropertyChangedEventHandler? PropertyChanged;
        public ChannelMappingSettings Original { get; }
        public string Label => ChannelLabel(Original.ChannelIndex, Original.SpeakerMask);
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; if (!value) HasError = false; Changed(nameof(Enabled)); }
        }
        public bool HasError
        {
            get => _hasError;
            set { _hasError = value; Changed(nameof(HasError)); }
        }
        public string LowText
        {
            get => _lowText;
            set { _lowText = value; HasError = false; Changed(nameof(LowText)); }
        }
        public string HighText
        {
            get => _highText;
            set { _highText = value; HasError = false; Changed(nameof(HighText)); }
        }
        public ChannelRow(ChannelMappingSettings mapping)
        {
            Original = mapping;
            _enabled = mapping.Enabled;
            _lowText = mapping.LowCutHz.ToString("0.##", CultureInfo.CurrentCulture);
            _highText = mapping.HighCutHz.ToString("0.##", CultureInfo.CurrentCulture);
        }
        public void RefreshLabel() => Changed(nameof(Label));
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private static string ChannelLabel(int index, int speakerMask)
    {
        string? key = speakerMask switch
        {
            0x1 => "ChannelFrontLeft", 0x2 => "ChannelFrontRight", 0x4 => "ChannelFrontCenter",
            0x8 => "ChannelLfe", 0x10 => "ChannelBackLeft", 0x20 => "ChannelBackRight",
            0x40 => "ChannelFrontLeftCenter", 0x80 => "ChannelFrontRightCenter", 0x100 => "ChannelBackCenter",
            0x200 => "ChannelSideLeft", 0x400 => "ChannelSideRight", 0x800 => "ChannelTopCenter",
            0x1000 => "ChannelTopFrontLeft", 0x2000 => "ChannelTopFrontCenter", 0x4000 => "ChannelTopFrontRight",
            0x8000 => "ChannelTopBackLeft", 0x10000 => "ChannelTopBackCenter", 0x20000 => "ChannelTopBackRight",
            _ => null
        };
        return key is null ? Localization.Text("ChannelUnknown", index + 1) :
            Localization.Text("ChannelNameFormat", index + 1, Localization.Text(key));
    }
}
