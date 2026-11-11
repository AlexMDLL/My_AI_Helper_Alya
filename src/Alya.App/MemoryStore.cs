using System.Text.Json;

namespace Alya.App;

/// <summary>Память: факты о пользователе и его правила.</summary>
public sealed class MemoryStore
{
    public List<string> Facts { get; set; } = new();
    public List<string> Rules { get; set; } = new();

    private static string FilePath =>
        global::System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Alya", "memory.json");

    public static MemoryStore Load()
    {
        try
        {
            var file = FilePath;
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<MemoryStore>(File.ReadAllText(file)) ?? new MemoryStore();
            }
        }
        catch
        {
            // битый файл — начинаем с чистого
        }
        return new MemoryStore();
    }

    public void Save()
    {
        var file = FilePath;
        Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void AddFact(string fact)
    {
        var value = fact.Trim();
        if (value.Length == 0) return;
        if (!Facts.Contains(value, StringComparer.OrdinalIgnoreCase)) Facts.Add(value);
        Save();
    }

    public void AddRule(string rule)
    {
        var value = rule.Trim();
        if (value.Length == 0) return;
        if (!Rules.Contains(value, StringComparer.OrdinalIgnoreCase)) Rules.Add(value);
        Save();
    }

    public string ToPrompt()
    {
        if (Facts.Count == 0 && Rules.Count == 0) return string.Empty;
        var parts = new List<string>();
        if (Facts.Count > 0) parts.Add("Что ты знаешь о пользователе: " + string.Join("; ", Facts));
        if (Rules.Count > 0) parts.Add("Правила пользователя: " + string.Join("; ", Rules));
        return string.Join(" ", parts);
    }
}
