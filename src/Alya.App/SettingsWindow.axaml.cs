using Avalonia.Controls;
using Avalonia.Input;
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
            "kseniya" => 2,
            "aidar" => 3,
            "eugene" => 4,
            _ => 0
        });

        NavGeneral.Click += (_, _) => SelectTab(0);
        NavChat.Click += (_, _) => SelectTab(1);
        NavView.Click += (_, _) => SelectTab(2);

        TitleBar.PointerPressed += (_, args) =>
        {
            if (args.Source is Button) return;
            BeginMoveDrag(args);
        };

        Opacity = 0;
        var started = DateTime.UtcNow;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp((DateTime.UtcNow - started).TotalMilliseconds / 220.0, 0, 1);
            Opacity = 1 - Math.Pow(1 - progress, 3);
            if (progress < 1) return;
            timer.Stop();
            Opacity = 1;
        };
        timer.Start();

        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
    }

    private void SelectTab(int index)
    {
        PanelGeneral.IsVisible = index == 0;
        PanelChat.IsVisible = index == 1;
        PanelView.IsVisible = index == 2;

        NavGeneral.Classes.Set("active", index == 0);
        NavChat.Classes.Set("active", index == 1);
        NavView.Classes.Set("active", index == 2);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

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
            2 => "kseniya",
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
