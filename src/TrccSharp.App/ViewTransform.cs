namespace TrccSharp.App;

/// <summary>
/// Сдвиг и масштаб содержимого вида: пользователь тащит кадр мышью,
/// а ползунок меняет размер. Хранится отдельно для каждого вида.
/// </summary>
public sealed class ViewTransform
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Scale { get; set; } = 1;

    /// <summary>Ничего не сдвинуто и не увеличено — можно не трогать холст.</summary>
    public bool IsIdentity =>
        Math.Abs(X) < 0.01 && Math.Abs(Y) < 0.01 && Math.Abs(Scale - 1) < 0.001;

    public ViewTransform Clone() => new() { X = X, Y = Y, Scale = Scale };
}
