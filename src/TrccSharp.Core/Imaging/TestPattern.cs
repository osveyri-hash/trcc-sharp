namespace TrccSharp.Core.Imaging;

/// <summary>
/// Тестовая картинка для проверки ориентации и цветопередачи на живой панели.
/// Углы залиты разными цветами, поэтому поворот/зеркало видно сразу:
///   левый верхний — красный, правый верхний — зелёный,
///   левый нижний — синий, правый нижний — белый.
/// </summary>
public static class TestPattern
{
    private static readonly (byte R, byte G, byte B) Red = (220, 30, 30);
    private static readonly (byte R, byte G, byte B) Green = (30, 200, 60);
    private static readonly (byte R, byte G, byte B) Blue = (40, 70, 220);
    private static readonly (byte R, byte G, byte B) White = (245, 245, 245);
    private static readonly (byte R, byte G, byte B) Black = (10, 10, 10);

    /// <summary>
    /// Картинка для определения ориентации: полосы по краям, каждая своего цвета.
    /// Сверху — красная, слева — зелёная, снизу — синяя, справа — жёлтая.
    /// Ни с каким поворотом/зеркалом эта раскладка не совпадает сама с собой.
    /// </summary>
    public static Surface BuildEdgeBars(int width, int height)
    {
        var surface = new Surface(width, height);
        surface.Fill((20, 20, 20));

        var bar = Math.Max(6, Math.Min(width, height) / 10);
        surface.FillRect(0, 0, width, bar, (230, 20, 20));                    // верх — красный
        surface.FillRect(0, 0, bar, height, (20, 200, 40));                   // лево — зелёный
        surface.FillRect(0, height - bar, width, bar, (30, 60, 230));         // низ — синий
        surface.FillRect(width - bar, 0, bar, height, (240, 210, 30));        // право — жёлтый

        return surface;
    }

    public static Surface Build(int width, int height)
    {
        var surface = new Surface(width, height);
        var halfW = width / 2;
        var halfH = height / 2;

        surface.FillRect(0, 0, halfW, halfH, Red);
        surface.FillRect(halfW, 0, width - halfW, halfH, Green);
        surface.FillRect(0, halfH, halfW, height - halfH, Blue);
        surface.FillRect(halfW, halfH, width - halfW, height - halfH, White);

        // Рамка — видно, если панель обрезает края
        var border = Math.Max(2, Math.Min(width, height) / 80);
        surface.FillRect(0, 0, width, border, Black);
        surface.FillRect(0, height - border, width, border, Black);
        surface.FillRect(0, 0, border, height, Black);
        surface.FillRect(width - border, 0, border, height, Black);

        // Центральный крест — проверка геометрии без поворота
        var midX = width / 2;
        var midY = height / 2;
        surface.FillRect(midX - 1, 0, 2, height, Black);
        surface.FillRect(0, midY - 1, width, 2, Black);

        // Метка «верх» — треугольник у верхней кромки по центру
        for (var i = 0; i < height / 10; i++)
            surface.FillRect(midX - i, 6 + i, i * 2 + 1, 1, Black);

        // Шкала серого вдоль нижней кромки
        var steps = 16;
        var stepW = width / steps;
        for (var i = 0; i < steps; i++)
        {
            var v = (byte)(i * 255 / (steps - 1));
            surface.FillRect(i * stepW, height - height / 12, stepW, height / 12, (v, v, v));
        }

        return surface;
    }
}
