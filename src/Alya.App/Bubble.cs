using Avalonia.Layout;
using Avalonia.Media;

namespace Alya.App;

public sealed class Bubble
{
    public string Text { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
    public HorizontalAlignment Alignment { get; init; }
    public IBrush Background { get; init; } = Brushes.Transparent;
}
