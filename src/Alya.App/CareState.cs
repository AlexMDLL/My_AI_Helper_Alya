using System.Text.Json;

namespace Alya.App;

/// <summary>Состояние «заботы»: ночные напоминания, утренний вопрос, режим «ушёл».</summary>
public sealed class CareState
{
    public string LastNightNotice { get; set; } = string.Empty;
    public string LastMorningAsk { get; set; } = string.Empty;
    public string MorningAnswer { get; set; } = string.Empty;
    public bool Away { get; set; }
    public DateTime AwaySince { get; set; }
    public bool AwaitingReturn { get; set; }
    public DateTime LastReturnAsk { get; set; }

    private static string PathFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alya", "care.json");

    public static CareState Load()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                return JsonSerializer.Deserialize<CareState>(File.ReadAllText(PathFile)) ?? new CareState();
            }
        }
        catch
        {
            // повреждённый файл — начинаем заново
        }
        return new CareState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this));
        }
        catch
        {
            // не критично
        }
    }

    public static bool IsDayOff(DateTime day) => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
