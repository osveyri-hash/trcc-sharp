using System.Runtime.InteropServices;

namespace TrccSharp.Core.Imaging;

/// <summary>
/// Растровый холст BGRA32 (как отдаёт WPF RenderTargetBitmap).
/// Повороты/отражения — поэлементно, без System.Drawing.
/// Пиксели копируются как uint, а не через Buffer.BlockCopy по 4 байта:
/// на кадр 240×320 это в разы меньше вызовов и заметно быстрее.
/// </summary>
public sealed class Surface
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Байты BGRA32, строка за строкой, без выравнивания.</summary>
    public byte[] Bgra { get; }

    public Surface(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Bgra = new byte[width * height * 4];
    }

    private Surface(int width, int height, byte[] bgra)
    {
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    /// <summary>Холст из готового BGRA-буфера (например, из RenderTargetBitmap в WPF).</summary>
    public static Surface FromBgra(int width, int height, byte[] bgra)
    {
        if (bgra.Length != width * height * 4)
            throw new ArgumentException($"Ожидалось {width * height * 4} байт, получено {bgra.Length}", nameof(bgra));

        return new Surface(width, height, bgra);
    }

    /// <summary>Таблица «яркость → канал»: 256 значений, одно умножение на пиксель не нужно.</summary>
    public static byte[] BrightnessLut(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        var lut = new byte[256];

        for (var i = 0; i < lut.Length; i++)
            lut[i] = (byte)(i * p / 100);

        return lut;
    }

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        var i = (y * Width + x) * 4;
        Bgra[i] = b;
        Bgra[i + 1] = g;
        Bgra[i + 2] = r;
        Bgra[i + 3] = a;
    }

    public void FillRect(int x, int y, int w, int h, (byte R, byte G, byte B) color)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(Width, x + w);
        var y1 = Math.Min(Height, y + h);

        for (var py = y0; py < y1; py++)
            for (var px = x0; px < x1; px++)
                SetPixel(px, py, color.R, color.G, color.B);
    }

    public void Fill((byte R, byte G, byte B) color) => FillRect(0, 0, Width, Height, color);

    /// <summary>Поворот на 90° по часовой: dst(x,y) = src(y, h-1-x).</summary>
    public Surface Rotate90Cw()
    {
        var dst = new Surface(Height, Width);
        Rotate90CwInto(dst);
        return dst;
    }

    public Surface Rotate180()
    {
        var dst = new Surface(Width, Height);
        Rotate180Into(dst);
        return dst;
    }

    public Surface Rotate270Cw()
    {
        var dst = new Surface(Height, Width);
        Rotate270CwInto(dst);
        return dst;
    }

    public Surface FlipHorizontal()
    {
        var dst = new Surface(Width, Height);
        var src = Pixels(this);
        var target = Pixels(dst);

        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            for (var x = 0; x < Width; x++)
                target[row + x] = src[row + Width - 1 - x];
        }

        return dst;
    }

    public Surface FlipVertical()
    {
        var dst = new Surface(Width, Height);
        var src = Pixels(this);
        var target = Pixels(dst);

        for (var y = 0; y < Height; y++)
            src.Slice((Height - 1 - y) * Width, Width).CopyTo(target.Slice(y * Width, Width));

        return dst;
    }

    public Surface Rotate(int degrees) => degrees switch
    {
        0 => this,
        90 => Rotate90Cw(),
        180 => Rotate180(),
        270 => Rotate270Cw(),
        _ => throw new ArgumentOutOfRangeException(nameof(degrees), "Только 0/90/180/270"),
    };

    /// <summary>
    /// Поворот в готовый буфер: при потоке кадров незачем каждый раз выделять
    /// новый массив под тот же самый растр.
    /// </summary>
    public Surface RotateInto(int degrees, ref Surface? cache)
    {
        if (degrees == 0) return this;

        var (width, height) = degrees is 90 or 270 ? (Height, Width) : (Width, Height);

        if (cache is null || cache.Width != width || cache.Height != height)
            cache = new Surface(width, height);

        switch (degrees)
        {
            case 90:
                Rotate90CwInto(cache);
                break;
            case 180:
                Rotate180Into(cache);
                break;
            case 270:
                Rotate270CwInto(cache);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(degrees), "Только 0/90/180/270");
        }

        return cache;
    }

    private void Rotate90CwInto(Surface dst)
    {
        var src = Pixels(this);
        var target = Pixels(dst);
        var dstWidth = dst.Width;

        for (var y = 0; y < dst.Height; y++)
        {
            var row = y * dstWidth;
            var column = (Height - 1) * Width + y;

            for (var x = 0; x < dstWidth; x++)
                target[row + x] = src[column - x * Width];
        }
    }

    private void Rotate180Into(Surface dst)
    {
        var src = Pixels(this);
        var target = Pixels(dst);
        var last = src.Length - 1;

        for (var i = 0; i < src.Length; i++)
            target[i] = src[last - i];
    }

    private void Rotate270CwInto(Surface dst)
    {
        var src = Pixels(this);
        var target = Pixels(dst);
        var dstWidth = dst.Width;

        for (var y = 0; y < dst.Height; y++)
        {
            var row = y * dstWidth;
            var column = Width - 1 - y;

            for (var x = 0; x < dstWidth; x++)
                target[row + x] = src[column + x * Width];
        }
    }

    /// <summary>
    /// Кодирование в RGB565. <paramref name="lut"/> — таблица яркости из
    /// <see cref="BrightnessLut"/>: яркость применяется прямо здесь, без второго прохода.
    /// </summary>
    public byte[] ToRgb565(bool bigEndian = false, byte[]? lut = null)
    {
        var result = new byte[Width * Height * 2];
        WriteRgb565(result, bigEndian, lut);
        return result;
    }

    /// <summary>Кодирование в готовый буфер (длина — Width * Height * 2).</summary>
    public void WriteRgb565(Span<byte> destination, bool bigEndian = false, byte[]? lut = null)
    {
        if (destination.Length < Width * Height * 2)
            throw new ArgumentException($"Нужно {Width * Height * 2} байт", nameof(destination));

        var p = 0;

        for (var i = 0; i < Bgra.Length; i += 4)
        {
            byte b = Bgra[i], g = Bgra[i + 1], r = Bgra[i + 2];

            if (lut is not null)
            {
                b = lut[b];
                g = lut[g];
                r = lut[r];
            }

            var value = (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));

            if (bigEndian)
            {
                destination[p++] = (byte)(value >> 8);
                destination[p++] = (byte)value;
            }
            else
            {
                destination[p++] = (byte)value;
                destination[p++] = (byte)(value >> 8);
            }
        }
    }

    private static Span<uint> Pixels(Surface surface) =>
        MemoryMarshal.Cast<byte, uint>(surface.Bgra.AsSpan());
}
