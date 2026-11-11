using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Alya.App;

public partial class SettingsWindow : Window
{
    private readonly AlyaSettings _settings;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = AlyaSettings.Load();
        ModelBox.Text = _settings.Model;
        AutoHideBox.Value = _settings.ChatAutoHideSeconds;
        FpsBox.Value = _settings.Fps;
        PitchSlider.Value = _settings.Pitch;
        AutostartBox.IsChecked = _settings.Autostart;
        SpeakerBox.SelectedIndex = Math.Max(0, _settings.Speaker switch
        {
            "baya" => 1,
            "kseniya2" => 2,
            "aidar" => 3,
            "eugene" => 4,
            _ => 0
        });

        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
    }

    private static void ApplyAutostart(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue("AlyaNext", "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue("AlyaNext", throwOnMissingValue: false);
            }
        }
        catch
        {
            // автозапуск не критичен
        }
    }

    private void Save()
    {
        _settings.Model = ModelBox.Text?.Trim() is { Length: > 0 } model ? model : "qwen2.5:7b-instruct";
        _settings.ChatAutoHideSeconds = (int)(AutoHideBox.Value ?? 20);
        _settings.Fps = (int)(FpsBox.Value ?? 120);
        _settings.Pitch = Math.Round(PitchSlider.Value, 2);
        _settings.Speaker = SpeakerBox.SelectedIndex switch
        {
            1 => "baya",
            2 => "kseniya2",
            3 => "aidar",
            4 => "eugene",
            _ => "xenia"
        };
        _settings.Autostart = AutostartBox.IsChecked == true;
        _settings.Save();
        ApplyAutostart(_settings.Autostart);
        StatusText.Text = "Сохранено";
        Close();
    }
}
