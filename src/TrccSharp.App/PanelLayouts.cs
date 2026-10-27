using TrccSharp.App.Rendering;

namespace TrccSharp.App;

/// <summary>
/// Стартовые раскладки видов: где стоят объекты, пока пользователь не подвинул их сам.
/// </summary>
public static class PanelLayouts
{
    /// <summary>Виды, которые собираются из объектов и потому двигаются по частям.</summary>
    public static bool IsObjectView(ThemeKind kind) =>
        kind is ThemeKind.Custom or ThemeKind.Monitor or ThemeKind.Minimal;

    public static List<PanelWidget> Defaults(ThemeKind kind) => kind switch
    {
        // «Монитор» при первом запуске пустой: на панели только фон,
        // показатели добавляются кнопкой «Добавить».
        ThemeKind.Monitor => new List<PanelWidget>(),
        ThemeKind.Minimal => new List<PanelWidget>
        {
            new() { Type = "cpu", X = 22, Y = 44, Scale = 3.2 },
            new() { Type = "ram", X = 22, Y = 176, Scale = 0.6 },
            new() { Type = "gpu", X = 140, Y = 176, Scale = 0.6 },
            new() { Type = "uptime", X = 240, Y = 176, Scale = 0.6 }
        },
        ThemeKind.Custom => new List<PanelWidget>
        {
            new() { Type = "cpu", X = 24, Y = 44 },
            new() { Type = "ram", X = 176, Y = 44 },
            new() { Type = "gpu", X = 24, Y = 116 },
            new() { Type = "uptime", X = 176, Y = 116 }
        },
        _ => new List<PanelWidget>()
    };
}
