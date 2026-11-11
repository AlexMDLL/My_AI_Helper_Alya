using Avalonia.Controls;
using Avalonia.Layout;
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
        Build(FactsPanel, memory.Facts, isFact: true);
        Build(RulesPanel, memory.Rules, isFact: false);
        LinksText.Text = BuildLinks(memory);
    }

    private void Build(StackPanel panel, List<string> items, bool isFact)
    {
        panel.Children.Clear();
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = isFact ? "Пока ничего не запоминала." : "Правил нет.",
                Foreground = new SolidColorBrush(Color.Parse("#9FB2C9")),
                FontSize = 12
            });
            return;
        }
        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = "• " + item,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#E6EDF5")),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            });
            var remove = new Button
            {
                Content = "✕",
                Width = 28,
                Height = 24,
                Padding = new Avalonia.Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.Parse("#FF7B8C"))
            };
            Grid.SetColumn(remove, 1);
            var value = item;
            remove.Click += (_, _) =>
            {
                var current = MemoryStore.Load();
                if (isFact) current.Facts.RemoveAll(f => f == value);
                else current.Rules.RemoveAll(r => r == value);
                current.Save();
                Load();
            };
            row.Children.Add(remove);
            panel.Children.Add(row);
        }
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