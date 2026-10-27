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

    public PanelWidget Clone() => new() { Type = Type, X = X, Y = Y };
}
