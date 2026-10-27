using TrccSharp.Core.Imaging;

namespace TrccSharp.Core.Protocol;

/// <summary>
/// Устройство панели: подключение, определение профиля и отправка кадров.
/// Порядок работы повторяет проверенный на живой панели сценарий:
/// handshake в отдельном канале → переоткрытие → пауза → кадры без init-пакета.
/// </summary>
public sealed class PanelDevice : IDisposable
{
    private readonly Func<ILcdLink> _openLink;
    private readonly int _bootSettleMs;

    private ILcdLink? _link;
    private HidLcdType2? _protocol;

    // Переиспользуемые буферы кадра: повёрнутый растр, RGB565 и таблица яркости.
    private Surface? _rotated;
    private byte[] _payload = Array.Empty<byte>();
    private volatile byte[]? _brightnessLut;

    private int _brightnessPercent = 100;
    private bool _disposed;

    public HandshakeResult Info { get; private set; } = null!;
    public PanelProfile Profile => Info.Profile;
    public long FramesSent { get; private set; }
    public DateTime? LastFrameAt { get; private set; }

    public PanelDevice(Func<ILcdLink> openLink, int bootSettleMs = 3000)
    {
        _openLink = openLink;
        _bootSettleMs = bootSettleMs;
    }

    /// <summary>
    /// Яркость 0–100 %. Как и в оригинальном TRCC, это программная подстройка:
    /// каналы умножаются до кодирования в RGB565, команд в панель не шлётся.
    /// </summary>
    public int Brightness
    {
        get => Volatile.Read(ref _brightnessPercent);
        set
        {
            var percent = Math.Clamp(value, 0, 100);
            Volatile.Write(ref _brightnessPercent, percent);
            _brightnessLut = percent >= 100 ? null : Surface.BrightnessLut(percent);
        }
    }

    /// <summary>Канал по умолчанию: HID MI_00 (единственный, что реально доступен).</summary>
    public static PanelDevice ConnectDefault(int bootSettleMs = 3000) =>
        new(() => Hid.HidChannel.Open(), bootSettleMs);

    /// <summary>
    /// Опознаёт панель и открывает рабочий канал.
    /// Init-пакет для этой прошивки (PM=58) вызывает перезагрузку, поэтому
    /// handshake делается отдельным подключением, а рабочий канал его не видит.
    /// </summary>
    public void Connect()
    {
        using (var probeLink = _openLink())
        {
            Info = new HidLcdType2(probeLink).Handshake();
        }

        _link = _openLink();
        _protocol = new HidLcdType2(_link);

        // Панель могла перезагрузиться от handshake — даём ей встать,
        // иначе первые кадры уходят в пустоту.
        if (_bootSettleMs > 0) Thread.Sleep(_bootSettleMs);
    }

    /// <summary>
    /// Отправка кадра. Композиция поворачивается на угол профиля и кодируется
    /// в RGB565 с порядком байт из профиля; яркость применяется здесь же,
    /// одной таблицей подстановки, без второго прохода по пикселям.
    /// </summary>
    public void Send(Surface surface)
    {
        if (_protocol is null) throw new InvalidOperationException("Панель не подключена — вызови Connect()");
        if (_disposed) throw new ObjectDisposedException(nameof(PanelDevice));

        var rotated = surface.RotateInto(Profile.WireAngle, ref _rotated);
        var size = rotated.Width * rotated.Height * 2;

        if (_payload.Length < size)
            _payload = new byte[size];

        rotated.WriteRgb565(_payload, Profile.BigEndian, _brightnessLut);
        _protocol.SendFrame(_payload.AsSpan(0, size));

        FramesSent++;
        LastFrameAt = DateTime.UtcNow;
    }

    /// <summary>Проверка, что канал ещё жив (панель могли выдернуть).</summary>
    public bool IsAlive
    {
        get
        {
            if (_link is null) return false;
            try
            {
                _link.WriteChunk(new byte[512]);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _link?.Dispose();
        _link = null;
        _protocol = null;
    }
}
