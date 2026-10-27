namespace TrccSharp.Core.Protocol;

/// <summary>
/// Геометрия и способ кодирования панели, выведенные из FBL/PM/SUB байтов handshake.
/// Порт таблиц <c>core/protocol.py</c> из реверса TRCC.
/// </summary>
public sealed record PanelProfile(
    int Width,
    int Height,
    bool Jpeg,
    bool BigEndian,
    bool Rotate,
    bool Widescreen = false,
    int EncodeBase = 0,
    bool EncodeInvert = true,
    int Sub = 0)
{
    /// <summary>Размер кадра в байтах для RGB565.</summary>
    public int Rgb565Size => Width * Height * 2;

    /// <summary>Угол поворота композиции на провод при ориентации 0.</summary>
    public int WireAngle => Rotate ? EncodeBase : 0;

    public override string ToString() =>
        $"{Width}x{Height} {(Jpeg ? "JPEG" : "RGB565")} {(BigEndian ? "BE" : "LE")} " +
        $"rotate={Rotate} wire={WireAngle}°";
}

/// <summary>
/// Таблицы FBL/PM/SUB. Значения перенесены из декомпиляции TRCC 2.1.8
/// (FormCZTV.cs) через реверс-документацию; менять только по факту handshake.
/// </summary>
public static class VendorTables
{
    private static readonly PanelProfile DefaultProfile = new(320, 320, false, true, false) { EncodeBase = 0 };

    /// <summary>FBL → профиль. Комментарии — исходные из реверса.</summary>
    private static readonly Dictionary<int, PanelProfile> FblProfiles = new()
    {
        [36] = new(240, 240, false, false, false) { EncodeBase = 0 },
        [37] = new(240, 240, false, false, false) { EncodeBase = 0 },
        [49] = new(320, 240, false, true, true),   // HID Type 2 → SPIMode=2
        [50] = new(320, 240, false, false, true),
        [51] = new(320, 240, false, false, true),  // HID Type 2 → SPIMode=1
        [52] = new(320, 240, false, false, true),  // BA120 Vision
        [53] = new(320, 240, false, false, true),  // HID Type 2 → SPIMode=1
        [54] = new(360, 360, true, false, false) { EncodeBase = 0 },   // CORE VISION
        [58] = new(320, 240, false, false, true),  // Frozen Warframe SE
        [59] = new(640, 172, false, false, true) { EncodeBase = 270 },
        [60] = new(320, 176, false, false, true),
        [64] = new(640, 480, false, false, true) { EncodeBase = 0 },
        [72] = new(480, 480, false, false, false) { EncodeBase = 0 },
        [100] = new(320, 320, false, true, false) { EncodeBase = 0 },
        [101] = new(320, 320, false, true, false) { EncodeBase = 0 },
        [102] = new(320, 320, false, true, false) { EncodeBase = 0 },
        [114] = new(1600, 720, true, false, true) { EncodeBase = 180, Widescreen = true },
        [128] = new(1280, 480, true, false, true) { EncodeBase = 0, Widescreen = true },
        [129] = new(480, 480, false, false, false) { EncodeBase = 0 },
        [192] = new(1920, 462, true, false, true) { EncodeBase = 180, Widescreen = true },
        [224] = new(854, 480, true, false, true) { EncodeBase = 0, EncodeInvert = false, Widescreen = true },
    };

    /// <summary>PM → FBL для устройств, где байты не совпадают.</summary>
    private static readonly Dictionary<int, int> PmToFblOverrides = new()
    {
        [5] = 50, [7] = 64, [9] = 224, [10] = 224, [11] = 224, [12] = 224, [13] = 224,
        [14] = 64, [15] = 224, [16] = 224, [17] = 224, [18] = 224, [20] = 224,
        [32] = 100, [50] = 50, [63] = 114, [64] = 114, [65] = 192, [66] = 192,
        [68] = 192, [69] = 192,
    };

    /// <summary>(PM, SUB) → FBL для составных случаев.</summary>
    private static readonly Dictionary<(int Pm, int Sub), int> PmSubToFbl = new()
    {
        [(1, 48)] = 114,
        [(1, 49)] = 192,
    };

    /// <summary>FBL 224 делят 5 разрешений — PM уточняет.</summary>
    private static readonly Dictionary<int, (int W, int H)> Fbl224ByPm = new()
    {
        [9] = (854, 480), [10] = (960, 540), [11] = (854, 480), [12] = (800, 480),
        [13] = (960, 320), [15] = (640, 172), [16] = (960, 540), [17] = (960, 320),
        [18] = (960, 320), [20] = (854, 480),
    };

    /// <summary>FBL 192 делят 3 разрешения — PM уточняет.</summary>
    private static readonly Dictionary<int, (int W, int H)> Fbl192ByPm = new()
    {
        [1] = (1920, 462), [65] = (1920, 462), [66] = (1920, 462),
        [68] = (1280, 480), [69] = (1920, 440),
    };

    /// <summary>
    /// PM → FBL. По умолчанию PM == FBL (конвенция SCSI poll-byte).
    /// </summary>
    public static int PmToFbl(int pm, int sub = 0)
        => PmSubToFbl.TryGetValue((pm, sub), out var compound) ? compound
           : PmToFblOverrides.TryGetValue(pm, out var fbl) ? fbl
           : pm;

    /// <summary>Профиль панели по байтам handshake.</summary>
    public static PanelProfile GetProfile(int fbl, int pm = 0, int sub = 0)
    {
        var known = FblProfiles.TryGetValue(fbl, out var profile);
        profile ??= DefaultProfile;

        (int W, int H)? shared = fbl switch
        {
            192 when Fbl192ByPm.TryGetValue(pm, out var r) => r,
            224 when Fbl224ByPm.TryGetValue(pm, out var r) => r,
            192 => (1920, 462),
            224 => (854, 480),
            _ => null,
        };

        if (shared is { } s)
            profile = profile with { Width = s.W, Height = s.H };

        // FBL 54 при SUB=4 уходит в RGB565 вместо JPEG (вентиляторный хаб)
        var jpeg = profile.Jpeg && !(fbl == 54 && sub == 4);

        var (baseAngle, invert) = ResolveEncodeRotation(profile.Width, profile.Height, jpeg, sub);

        return profile with
        {
            Jpeg = jpeg,
            EncodeBase = baseAngle,
            EncodeInvert = invert,
            Sub = sub,
        };
    }

    /// <summary>
    /// Базовый угол поворота кадра на провод (ветки ImageTo565 / ImageToJpg).
    /// </summary>
    private static (int Base, bool Invert) ResolveEncodeRotation(int w, int h, bool jpeg, int sub)
    {
        if (jpeg)
        {
            return (w, h) switch
            {
                (320, 320) => (0, true),
                (480, 480) => (0, true),
                (320, 240) => (0, true),
                (1600, 720) => (sub == 3 ? 0 : 180, true),
                (854, 480) => (0, false),
                (800, 480) => (0, false),
                (1280, 480) => (sub == 2 ? 90 : 0, true),
                (960, 320) => (sub <= 4 ? 0 : 180, true),
                (960, 540) => (sub is 5 or 7 ? 180 : 0, true),
                (1920, 462) => (sub is 2 or 3 or 4 ? 0 : 180, true),
                (1920, 440) => (sub is 2 or 3 or 4 ? 0 : 180, true),
                (640, 480) => (0, true),
                (360, 360) => (0, true),
                (640, 172) => (0, true),
                _ => (90, true),   // default-ветка обоих switch
            };
        }

        return (w, h) switch
        {
            (240, 240) => (0, true),
            (320, 320) => (0, true),
            (360, 360) => (0, true),
            (480, 480) => (0, true),
            (640, 172) => (sub == 5 ? 90 : 270, true),
            _ => (90, true),       // 320x240 семейство Warframe
        };
    }

    /// <summary>Известен ли панели этот FBL — для honest-диагностики в probe.</summary>
    public static bool IsKnownFbl(int fbl) => FblProfiles.ContainsKey(fbl);
}
