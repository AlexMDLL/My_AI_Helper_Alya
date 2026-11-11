using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Layout;
using Avalonia.Media;

namespace Alya.App;

public sealed class Bubble : INotifyPropertyChanged
{
    private double _opacity;

    private string _text = string.Empty;

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }
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
