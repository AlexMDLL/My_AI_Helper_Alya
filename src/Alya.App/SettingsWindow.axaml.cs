using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Alya.App;

public partial class SettingsWindow : Window
{
    private readonly AlyaSettings _settings;
    private string _accent;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = AlyaSettings.Load();
        _accent = _settings.Accent;
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
        OpacitySlider.Value = 1;
        Opacity = 1;
        RefreshAccent();
        UpdateRgbFromAccent();
        RefreshMemory();

        NavGeneral.Click += (_, _) => SelectTab(0);
        NavBrain.Click += (_, _) => SelectTab(1);
        NavMemory.Click += (_, _) => SelectTab(2);
        NavChat.Click += (_, _) => SelectTab(3);
        NavVoice.Click += (_, _) => SelectTab(4);
        NavView.Click += (_, _) => SelectTab(5);

        foreach (var swatch in new[] { SwatchTeal, SwatchBlue, SwatchPink, SwatchPurple, SwatchOrange })
        {
            swatch.Click += (_, _) =>
            {
                _accent = (string?)swatch.Tag ?? "#38BDF8";
                UpdateRgbFromAccent();
                RefreshAccent();
            };
        }

        OpacitySlider.PropertyChanged += (_, args) =>
        {
            if (args.Property == Slider.ValueProperty) Opacity = Math.Clamp(OpacitySlider.Value, 0.6, 1);
        };

        RedSlider.PropertyChanged += (_, args) => { if (args.Property == Slider.ValueProperty) UpdateAccentFromRgb(); };
        GreenSlider.PropertyChanged += (_, args) => { if (args.Property == Slider.ValueProperty) UpdateAccentFromRgb(); };
        BlueSlider.PropertyChanged += (_, args) => { if (args.Property == Slider.ValueProperty) UpdateAccentFromRgb(); };

        CheckModelButton.Click += async (_, _) => await CheckOllamaAsync();
        ClearMemoryButton.Click += (_, _) => ClearMemory();
        PreviewVoiceButton.Click += (_, _) => PreviewVoice();

        TitleBar.PointerPressed += (_, args) =>
        {
            if (args.Source is Button) return;
            BeginMoveDrag(args);
        };

        Opacity = 1;

        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
    }

    private void RefreshAccent()
    {
        var accent = Color.Parse(_accent);
        SaveButton.Background = new SolidColorBrush(accent);
        foreach (var swatch in new[] { SwatchTeal, SwatchBlue, SwatchPink, SwatchPurple, SwatchOrange })
        {
            var selected = string.Equals((string?)swatch.Tag, _accent, StringComparison.OrdinalIgnoreCase);
            swatch.Classes.Set("selected", selected);
        }
    }

    private void UpdateRgbFromAccent()
    {
        try
        {
            var color = Color.Parse(_accent);
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
        }
        catch
        {
            // некорректный цвет — оставляем текущие значения
        }
    }

    private void UpdateAccentFromRgb()
    {
        var red = (byte)Math.Clamp((int)RedSlider.Value, 0, 255);
        var green = (byte)Math.Clamp((int)GreenSlider.Value, 0, 255);
        var blue = (byte)Math.Clamp((int)BlueSlider.Value, 0, 255);
        _accent = $"#{red:X2}{green:X2}{blue:X2}";
        RedValue.Text = red.ToString();
        GreenValue.Text = green.ToString();
        BlueValue.Text = blue.ToString();
        AccentPreview.Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
        SaveButton.Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
    }

    private void RefreshMemory()
    {
        var memory = MemoryStore.Load();
        MemorySummary.Text = $"Фактов: {memory.Facts.Count} · Правил: {memory.Rules.Count}";
    }

    private void ClearMemory()
    {
        try
        {
            var file = global::System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alya", "memory.json");
            if (File.Exists(file)) File.Delete(file);
            MemorySummary.Text = "Память очищена";
        }
        catch
        {
            MemorySummary.Text = "Не удалось очистить";
        }
    }

    private async Task CheckOllamaAsync()
    {
        BrainStatus.Text = "Проверяю…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var tags = await http.GetStringAsync("http://127.0.0.1:11434/api/tags");
            BrainStatus.Text = tags.Contains(_settings.Model) ? $"Модель на связи: {_settings.Model}" : "Ollama отвечает, модель не найдена";
        }
        catch (Exception error)
        {
            BrainStatus.Text = $"Нет связи: {error.Message}";
        }
    }

    private void PreviewVoice()
    {
        var service = new SapiTtsService();
        _ = service.SpeakAsync("Привет! Так я звучать буду.");
    }

    private void SelectTab(int index)
    {
        PanelGeneral.IsVisible = index == 0;
        PanelBrain.IsVisible = index == 1;
        PanelMemory.IsVisible = index == 2;
        PanelChat.IsVisible = index == 3;
        PanelVoice.IsVisible = index == 4;
        PanelView.IsVisible = index == 5;

        NavGeneral.Classes.Set("active", index == 0);
        NavBrain.Classes.Set("active", index == 1);
        NavMemory.Classes.Set("active", index == 2);
        NavChat.Classes.Set("active", index == 3);
        NavVoice.Classes.Set("active", index == 4);
        NavView.Classes.Set("active", index == 5);
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
        _settings.Opacity = Math.Clamp(OpacitySlider.Value, 0.6, 1);
        _settings.Accent = _accent;
        _settings.Save();
        ApplyAutostart(_settings.Autostart);
        StatusText.Text = "Сохранено";
        Close();
    }
}
