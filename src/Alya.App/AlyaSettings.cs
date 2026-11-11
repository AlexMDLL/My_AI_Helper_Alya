using System.Text.Json;

namespace Alya.App;

public sealed class AlyaSettings
{
    public string Model { get; set; } = "qwen2.5:7b-instruct";
    public string Persona { get; set; } = "Ты — Аля, живая спутница на ПК. Говори по-русски, коротко и живо, с лёгкой вредностью, но по-доброму.";
    public string Speaker { get; set; } = "xenia";
    public double Pitch { get; set; } = 1.2;
    public int Fps { get; set; } = 120;
    public int ChatAutoHideSeconds { get; set; } = 20;
    public bool Autostart { get; set; }
    public bool Bubbles { get; set; } = true;
    public bool Care { get; set; } = true;
    public bool TtsEnabled { get; set; } = true;
    public double PersonaScale { get; set; } = 1.0;
    public int PersonaOffset { get; set; } = 6;
    public bool DragEnabled { get; set; } = true;
    public bool FallEnabled { get; set; } = true;
    public bool PetEnabled { get; set; } = true;
    public bool AlwaysListen { get; set; } = true;
    public bool Fullscreen { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;
    public bool HideInGames { get; set; } = false;
    public bool HideInYoutube { get; set; } = false;
    public bool HideInVisualStudio { get; set; } = false;
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
