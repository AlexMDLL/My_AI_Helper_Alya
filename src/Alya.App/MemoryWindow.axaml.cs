using Avalonia.Controls;

namespace Alya.App;

public partial class MemoryWindow : Window
{
    private GraphNode? _selected;

    public MemoryWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        DeleteButton.IsVisible = false;
        DeleteButton.Click += (_, _) => DeleteSelected();
        Graph.SelectionChanged += OnSelected;
        Load();
    }

    private void Load()
    {
        var memory = MemoryStore.Load();
        var settings = AlyaSettings.Load();
        var items = new List<(string Label, string Kind, string Content)>
        {
            (settings.UserName, "user", settings.UserName),
            (settings.City, "city", settings.City)
        };
        foreach (var fact in memory.Facts)
        {
            items.Add((fact, "fact", fact));
        }
        foreach (var rule in memory.Rules)
        {
            items.Add((rule, "rule", rule));
        }
        Graph.SetData(items);
        NodeTitle.Text = "Нажми на кружок";
        NodeKind.Text = string.Empty;
        ConnectionsList.ItemsSource = null;
        DeleteButton.IsVisible = false;
    }

    private void OnSelected(GraphNode? node)
    {
        _selected = node;
        if (node is null)
        {
            NodeTitle.Text = "Нажми на кружок";
            NodeKind.Text = string.Empty;
            ConnectionsList.ItemsSource = null;
            DeleteButton.IsVisible = false;
            return;
        }

        NodeTitle.Text = node.Content;
        NodeKind.Text = node.Kind switch
        {
            "user" => "пользователь",
            "city" => "город",
            "rule" => "правило",
            _ => "факт"
        };
        var neighbors = Graph.NeighborLabels(node);
        ConnectionsList.ItemsSource = neighbors.Count == 0
            ? new List<string> { "пока ни с чем не связано" }
            : neighbors;
        DeleteButton.IsVisible = node.Kind is "fact" or "rule";
    }

    private void DeleteSelected()
    {
        if (_selected is null || _selected.Kind is not ("fact" or "rule")) return;
        var memory = MemoryStore.Load();
        if (_selected.Kind == "fact") memory.Facts.RemoveAll(fact => fact == _selected.Content);
        else memory.Rules.RemoveAll(rule => rule == _selected.Content);
        memory.Save();
        Load();
    }
}
