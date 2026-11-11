using System.Text.Json;
using Alya.Core;

namespace Alya.App;

/// <summary>История диалога между запусками.</summary>
public static class HistoryStore
{
    private static string FilePath =>
        global::System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Alya", "history.json");

    public static List<ChatMessage> Load()
    {
        try
        {
            var file = FilePath;
            if (!File.Exists(file)) return new List<ChatMessage>();
            var json = File.ReadAllText(file);
            var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new List<ChatMessage>();
            return messages.TakeLast(40).ToList();
        }
        catch
        {
            return new List<ChatMessage>();
        }
    }

    public static void Save(IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            var file = FilePath;
            Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(messages.TakeLast(40), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // история не критична
        }
    }
}
