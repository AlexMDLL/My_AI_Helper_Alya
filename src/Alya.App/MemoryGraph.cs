using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Alya.App;

public sealed class GraphNode
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Kind { get; set; } = "fact"; // user, city, fact, rule, topic
    public string Content { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double VX { get; set; }
    public double VY { get; set; }
    public List<string> Links { get; } = new();
}

/// <summary>Сетевой граф памяти: узлы, связи, зум и выбор.</summary>
public sealed class MemoryGraph : Control
{
    private readonly List<GraphNode> _nodes = new();
    private readonly List<(string A, string B)> _edges = new();
    private double _zoom = 1;
    private Point _pan;
    private Point _lastPointer;
    private bool _dragging;
    private GraphNode? _selected;
    private GraphNode? _zoomTarget;
    private double _targetZoom = 1;
    private Point _targetPan;
    private DispatcherTimer? _timer;
    private double _time;
    private double _appear;
    private GraphNode? _hover;
    private bool _autoZoomed;

    public event Action<GraphNode?>? SelectionChanged;

    public MemoryGraph()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Frame();
        _timer.Start();
    }

    public void SetData(List<(string Label, string Kind, string Content)> items)
    {
        _nodes.Clear();
        _edges.Clear();
        _zoom = 1;
        _pan = default;
        _selected = null;
        var index = 0;
        foreach (var (label, kind, content) in items)
        {
            _nodes.Add(new GraphNode { Id = $"n{index++}", Label = label, Kind = kind, Content = content });
        }
        // связи: всё связано с пользователем, плюс по ключевым темам
        var user = _nodes.FirstOrDefault(n => n.Kind == "user");
        var city = _nodes.FirstOrDefault(n => n.Kind == "city");
        foreach (var node in _nodes)
        {
            if (node == user) continue;
            if (user is not null && node.Kind is "fact" or "rule")
            {
                _edges.Add((user.Id, node.Id));
                user.Links.Add(node.Id);
                node.Links.Add(user.Id);
            }
            if (city is not null && node.Kind == "fact" && node.Content.Contains(city.Label, StringComparison.OrdinalIgnoreCase))
            {
                _edges.Add((city.Id, node.Id));
                city.Links.Add(node.Id);
                node.Links.Add(city.Id);
            }
        }
        Layout();
        _appear = 0;
    }

    private void Layout()
    {
        if (_nodes.Count == 0) return;
        var random = new Random(42);
        var radius = Math.Max(90, _nodes.Count * 12);
        for (var i = 0; i < _nodes.Count; i++)
        {
            var angle = Math.PI * 2 * i / _nodes.Count;
            _nodes[i].X = Math.Cos(angle) * radius + random.NextDouble() * 20;
            _nodes[i].Y = Math.Sin(angle) * radius + random.NextDouble() * 20;
        }
        for (var iteration = 0; iteration < 260; iteration++)
        {
            foreach (var a in _nodes)
            {
                foreach (var b in _nodes)
                {
                    if (ReferenceEquals(a, b)) continue;
                    var dx = a.X - b.X;
                    var dy = a.Y - b.Y;
                    var distance = Math.Max(20, Math.Sqrt(dx * dx + dy * dy));
                    var force = 4200 / (distance * distance);
                    a.VX += dx / distance * force;
                    a.VY += dy / distance * force;
                }
            }
            foreach (var (aId, bId) in _edges)
            {
                var a = _nodes.First(n => n.Id == aId);
                var b = _nodes.First(n => n.Id == bId);
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var distance = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                var force = (distance - 120) * 0.012;
                a.VX += dx / distance * force;
                a.VY += dy / distance * force;
                b.VX -= dx / distance * force;
                b.VY -= dy / distance * force;
            }
            foreach (var node in _nodes)
            {
                node.X += Math.Clamp(node.VX, -8, 8);
                node.Y += Math.Clamp(node.VY, -8, 8);
                node.VX *= 0.72;
                node.VY *= 0.72;
            }
        }
    }

    public IReadOnlyList<string> NeighborLabels(GraphNode node)
    {
        return node.Links
            .Select(id => _nodes.FirstOrDefault(candidate => candidate.Id == id)?.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .ToList();
    }
    private Point ToScreen(double x, double y) => new(x * _zoom + _pan.X, y * _zoom + _pan.Y);

    private GraphNode? HitTest(Point point)
    {
        foreach (var node in _nodes)
        {
            var center = ToScreen(node.X, node.Y);
            var radius = 20 * _zoom;
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            if (dx * dx + dy * dy <= radius * radius) return node;
        }
        return null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var background = new SolidColorBrush(Color.Parse("#C4070B14"));
        context.FillRectangle(background, new Rect(Bounds.Size));
        if (_nodes.Count == 0)
        {
            var empty = new FormattedText("Память пуста", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, new SolidColorBrush(Color.Parse("#7C8CA6")));
            context.DrawText(empty, new Point(20, 20));
            return;
        }

        var linkPen = new Pen(new SolidColorBrush(Color.Parse("#2A5C96")), 1.1);
        var edgeIndex = 0;
        foreach (var (aId, bId) in _edges)
        {
            var a = _nodes.First(n => n.Id == aId);
            var b = _nodes.First(n => n.Id == bId);
            var start = ToScreen(a.X, a.Y);
            var end = ToScreen(b.X, b.Y);
            var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var amplitude = (4.5 + 3.5 * Math.Sin(_time * 1.7 + edgeIndex * 0.8)) * Math.Min(1.4, _zoom);
            var normalX = -dy / length * amplitude;
            var normalY = dx / length * amplitude;
            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(start, false);
                stream.QuadraticBezierTo(new Point(mid.X + normalX, mid.Y + normalY), end);
                stream.EndFigure(false);
            }
            context.DrawGeometry(null, linkPen, geometry);
            edgeIndex++;
        }

        foreach (var node in _nodes)
        {
            var center = ToScreen(node.X, node.Y);
            var isHot = ReferenceEquals(node, _selected) || ReferenceEquals(node, _hover);
            var pulse = 1 + (isHot ? 0.10 : 0.035) * Math.Sin(_time * 2.2 + node.X * 0.05);
            var baseRadius = ReferenceEquals(node, _selected) ? 21 : ReferenceEquals(node, _hover) ? 17 : 13;
            var radius = baseRadius * _zoom * (0.6 + 0.4 * _appear) * pulse;
            var color = node.Kind switch
            {
                "user" => "#FFD166",
                "city" => "#7CE7FF",
                "rule" => "#FF7B8C",
                _ => "#2FA8FF"
            };
            var glow = new SolidColorBrush(Color.Parse(color)) { Opacity = (isHot ? 0.38 : 0.20) * _appear };
            context.DrawEllipse(glow, null, center, radius * (isHot ? 2.1 : 1.7), radius * (isHot ? 2.1 : 1.7));
            var brush = new SolidColorBrush(Color.Parse(color)) { Opacity = _appear };
            var pen = ReferenceEquals(node, _selected) ? new Pen(Brushes.White, 2) : null;
            context.DrawEllipse(brush, pen, center, radius, radius);

            var shortLabel = node.Label.Length > 26 ? node.Label[..26] + "…" : node.Label;
            var text = new FormattedText(shortLabel, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), Math.Clamp(9.5 * _zoom, 8, 13), new SolidColorBrush(Colors.White) { Opacity = _appear });
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y + radius + 4));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this).Position;
        var node = HitTest(point);
        if (node is not null)
        {
            _selected = node;
            SelectionChanged?.Invoke(node);
            if (!_autoZoomed)
            {
                _zoomTarget = node;
                _targetZoom = 1.9;
                _autoZoomed = true;
            }
            _timer?.Start();
        }
        else
        {
            _dragging = true;
            _lastPointer = point;
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var point = e.GetCurrentPoint(this).Position;
        _pan += point - _lastPointer;
        _lastPointer = point;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.12 : 0.89), 0.35, 3.2);
        var point = e.GetCurrentPoint(this).Position;
        _pan = new Point(
            point.X - (point.X - _pan.X) * (_zoom / oldZoom),
            point.Y - (point.Y - _pan.Y) * (_zoom / oldZoom));
        _zoomTarget = null;
        InvalidateVisual();
    }

    private void Frame()
    {
        _time += 0.033;
        if (_appear < 1) _appear = Math.Min(1, _appear + 0.06);
        if (_zoomTarget is not null) AnimateZoom();
        InvalidateVisual();
    }
    private void AnimateZoom()
    {
        if (_zoomTarget is null) return;
        var target = new Point(Bounds.Width / 2 - _zoomTarget.X * _targetZoom, Bounds.Height / 2 - _zoomTarget.Y * _targetZoom);
        _zoom += (_targetZoom - _zoom) * 0.07;
        _pan += (target - _pan) * 0.07;
        if (Math.Abs(_zoom - _targetZoom) < 0.01 && Math.Abs(target.X - _pan.X) + Math.Abs(target.Y - _pan.Y) < 1)
        {
            _zoom = _targetZoom;
            _pan = target;
            _zoomTarget = null;
        }
        InvalidateVisual();
    }
}
