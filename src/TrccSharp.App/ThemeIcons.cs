using System.Windows;
using System.Windows.Media;

namespace TrccSharp.App;

/// <summary>
/// Значки меню оформления: простые фигуры в квадрате 16×16.
/// Возвращается пара геометрий — заливка и обводка, поэтому в разметке
/// строки достаточно двух Path.
/// </summary>
internal static class ThemeIcons
{
    public static (Geometry? Fill, Geometry? Stroke) Create(string icon) => icon switch
    {
        "sun" => (Circle(8, 8, 2.7), Rays()),
        "moon" => (Moon(), null),
        "wave" => (null, Waves()),
        "layers" => (Layers(), null),
        "blossom" => (Blossom(), null),
        "matrix" => (Dots(), null),
        "tree" => (Tree(), null),
        "oled" => (HalfDisc(), Circle(8, 8, 5.7)),
        _ => (Circle(8, 8, 4.2), null)
    };

    private static Geometry Circle(double x, double y, double r) => Frozen(new EllipseGeometry(new Point(x, y), r, r));

    private static Geometry Rays()
    {
        var group = new GeometryGroup();

        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            var figure = new PathFigure
            {
                StartPoint = new Point(8 + Math.Cos(angle) * 4.7, 8 + Math.Sin(angle) * 4.7),
                IsFilled = false,
                IsClosed = false
            };
            figure.Segments.Add(new LineSegment(new Point(8 + Math.Cos(angle) * 6.9, 8 + Math.Sin(angle) * 6.9), true));

            var line = new PathGeometry();
            line.Figures.Add(figure);
            group.Children.Add(line);
        }

        return Frozen(group);
    }

    // Полумесяц: из большого круга вырезан круг поменьше со сдвигом вверх-вправо.
    private static Geometry Moon() =>
        Frozen(Geometry.Combine(Circle(8, 8, 5.6), Circle(11.3, 6.1, 4.9), GeometryCombineMode.Exclude, null));

    private static Geometry Waves()
    {
        var group = new GeometryGroup();

        foreach (var y in new[] { 5.8, 10.4 })
        {
            var figure = new PathFigure { StartPoint = new Point(2.2, y), IsFilled = false, IsClosed = false };
            figure.Segments.Add(new BezierSegment(new Point(4.2, y - 2), new Point(6.4, y - 2), new Point(8.1, y), true));
            figure.Segments.Add(new BezierSegment(new Point(9.8, y + 2), new Point(12, y + 2), new Point(13.8, y), true));

            var wave = new PathGeometry();
            wave.Figures.Add(figure);
            group.Children.Add(wave);
        }

        return Frozen(group);
    }

    private static Geometry Layers()
    {
        var group = new GeometryGroup();

        for (var i = 0; i < 3; i++)
            group.Children.Add(new RectangleGeometry(new Rect(2.2, 2.4 + i * 4.2, 11.6, 2.8), 1.2, 1.2));

        return Frozen(group);
    }

    private static Geometry Blossom()
    {
        var group = new GeometryGroup();

        for (var i = 0; i < 5; i++)
        {
            var angle = -Math.PI / 2 + i * 2 * Math.PI / 5;
            group.Children.Add(Circle(8 + Math.Cos(angle) * 3.3, 8 + Math.Sin(angle) * 3.3, 2.2));
        }

        group.Children.Add(Circle(8, 8, 1.7));
        return Frozen(group);
    }

    private static Geometry Dots()
    {
        var group = new GeometryGroup();

        foreach (var y in new[] { 4.0, 8.0, 12.0 })
            foreach (var x in new[] { 4.0, 8.0, 12.0 })
                group.Children.Add(Circle(x, y, 1.05));

        return Frozen(group);
    }

    private static Geometry Tree()
    {
        var crown = new PathFigure { StartPoint = new Point(8, 1.8), IsClosed = true, IsFilled = true };
        crown.Segments.Add(new LineSegment(new Point(13.2, 9.6), true));
        crown.Segments.Add(new LineSegment(new Point(2.8, 9.6), true));

        var triangle = new PathGeometry();
        triangle.Figures.Add(crown);

        var group = new GeometryGroup();
        group.Children.Add(triangle);
        group.Children.Add(new RectangleGeometry(new Rect(7.1, 10, 1.8, 4.2), 0.6, 0.6));

        return Frozen(group);
    }

    // Круг с закрашенной правой половиной — знак контраста.
    private static Geometry HalfDisc()
    {
        var figure = new PathFigure { StartPoint = new Point(8, 2.4), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(new Point(8, 13.6), new Size(5.6, 5.6), 0, false, SweepDirection.Clockwise, true));

        var half = new PathGeometry();
        half.Figures.Add(figure);
        return Frozen(half);
    }

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }
}
