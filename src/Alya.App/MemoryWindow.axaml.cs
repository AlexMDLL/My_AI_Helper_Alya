using Avalonia.Controls;
using Avalonia.Media;

namespace Alya.App;

public partial class MemoryWindow : Window
{
    public MemoryWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        RefreshButton.Click += (_, _) => Load();
        Load();
    }

    private void Load()
    {
        var memory = MemoryStore.Load();
        FactsText.Text = memory.Facts.Count == 0 ? "Пока ничего не запоминала." : string.Join("\n• ", new[] { "" }.Concat(memory.Facts)).Trim();
        RulesText.Text = memory.Rules.Count == 0 ? "Правил нет." : string.Join("\n• ", new[] { "" }.Concat(memory.Rules)).Trim();
        LinksText.Text = BuildLinks(memory);
    }

    private static string BuildLinks(MemoryStore memory)
    {
        var text = string.Join(" ", memory.Facts).ToLowerInvariant();
        var parts = new List<string>();
        if (text.Contains("зовут") || text.Contains("александр")) parts.Add("• Пользователь — Александр");
        if (text.Contains("сыктывкар")) parts.Add("• Живёт/бывает в Сыктывкаре");
        if (text.Contains("люб") || text.Contains("гитар")) parts.Add("• Есть увлечения: гитара/музыка");
        if (parts.Count == 0) return "Связей пока нет — накапливаю.";
        parts.Add("• Город по умолчанию: Сыктывкар (для погоды)");
        return string.Join("\n", parts.Distinct());
    }
}
