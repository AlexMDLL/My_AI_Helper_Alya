using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Layout;
using Avalonia.Media;

namespace Alya.App;

public sealed class Bubble : INotifyPropertyChanged
{
    private double _opacity;

    public string Text { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
    public HorizontalAlignment Alignment { get; init; }
    public IBrush Background { get; init; } = Brushes.Transparent;

    public double Opacity
    {
        get => _opacity;
        set
        {
            if (Math.Abs(_opacity - value) < 0.001) return;
            _opacity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Opacity)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
