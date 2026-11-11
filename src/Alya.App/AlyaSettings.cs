using System.Text.Json;

namespace Alya.App;

public sealed class AlyaSettings
{
    public string Model { get; set; } = "qwen2.5:7b-instruct";
    public string Speaker { get; set; } = "xenia";
    public double Pitch { get; set; } = 1.2;
    public int Fps { get; set; } = 120;
    public int ChatAutoHideSeconds { get; set; } = 20;
    public bool Autostart { get; set; }
    public bool Bubbles { get; set; } = true;
    public bool Care { get; set; } = true;
    public double Temperature { get; set; } = 0.8;
    public double Opacity { get; set; } = 1.0;
    public string Accent { get; set; } = "#38BDF8";

    public static string PathFile =>
        global::System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Alya", "settings.json");

    public static AlyaSettings Load()
    {
        try
        {
            var file = PathFile;
            if (File.Exists(file))
            {
                var json = File.ReadAllText(file);
                return JsonSerializer.Deserialize<AlyaSettings>(json) ?? new AlyaSettings();
            }
        }
        catch
        {
            // битый файл — берём дефолты
        }
        return new AlyaSettings();
    }

    public void Save()
    {
        var file = PathFile;
        Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
