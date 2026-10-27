namespace TrccSharp.App;

/// <summary>
/// Один объект на панели: что показывать и где он лежит.
/// Координаты — в пикселях кадра 320×240, левый верхний угол объекта.
/// </summary>
public sealed class PanelWidget
{
    /// <summary>Тип объекта: cpu, ram, gpu, uptime или clock.</summary>
    public string Type { get; set; } = "cpu";

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>Множитель размера объекта: 1 — обычный.</summary>
    public double Scale { get; set; } = 1;

    /// <summary>Свой цвет текста объекта («#RRGGBB»); пусто — цвет кадра.</summary>
    public string? Ink { get; set; }

    /// <summary>Свой шрифт объекта; пусто — шрифт кадра.</summary>
    public string? Font { get; set; }

    /// <summary>Текст для объекта типа «text».</summary>
    public string? Text { get; set; }

    /// <summary>Своя подпись объекта (ЦП, ОЗУ…); пусто — стандартная.</summary>
    public string? Label { get; set; }

    public PanelWidget Clone() => new()
    {
        Type = Type,
        X = X,
        Y = Y,
        Scale = Scale,
        Ink = Ink,
        Font = Font,
        Text = Text,
        Label = Label
    };
}
