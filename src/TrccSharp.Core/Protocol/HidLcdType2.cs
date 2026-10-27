using System.Buffers.Binary;
using TrccSharp.Core.Hid;

namespace TrccSharp.Core.Protocol;

/// <summary>Результат опознания панели.</summary>
public sealed record HandshakeResult(
    int Pm,
    int Sub,
    int Fbl,
    string Serial,
    PanelProfile Profile,
    byte[] RawResponse,
    int ReportIdStripped)
{
    public bool IsKnown => VendorTables.IsKnownFbl(Fbl);

    public string Describe() =>
        $"PM={Pm} SUB={Sub} FBL={Fbl} {(IsKnown ? "(каталог)" : "(НЕ из каталога — размер может быть неверным)")}\n" +
        $"  панель: {Profile}\n" +
        (string.IsNullOrEmpty(Serial) ? "" : $"  серийник: {Serial}\n");
}

/// <summary>
/// LCD Type 2 (Nuvoton HID LCD, 0416:5302).
/// Handshake DA/DB/DC/DD, кадр — 20-байтный заголовок + RGB565,
/// отправка потоком 512-байтных отчётов.
/// </summary>
public sealed class HidLcdType2
{
    private static readonly byte[] Magic = { 0xDA, 0xDB, 0xDC, 0xDD };

    private readonly ILcdLink _link;

    // Переиспользуемые буферы кадра: 512-выровненный пакет и RGB565-полезная нагрузка.
    private byte[] _packet = Array.Empty<byte>();

    public long FramesSent { get; private set; }

    public HidLcdType2(ILcdLink link)
    {
        _link = link;
    }

    /// <summary>512-байтный init-пакет: magic + cmd=1 (DEV_INFO).</summary>
    public static byte[] BuildInitPacket()
    {
        var packet = new byte[512];
        Magic.CopyTo(packet, 0);
        packet[12] = 0x01;
        return packet;
    }

    /// <summary>Handshake с ретраями.</summary>
    public HandshakeResult Handshake(int attempts = 4, int readTimeoutMs = 400)
    {
        var init = BuildInitPacket();
        byte[]? lastRaw = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            _link.WriteChunk(init);

            var report = _link.TryRead(readTimeoutMs);
            if (report is { Length: > 0 })
            {
                lastRaw = report;
                var (data, stripped) = Normalize(report);
                if (IsValid(data))
                    return Parse(data, report, stripped);
            }

            Thread.Sleep(150);
        }

        throw new HandshakeException(lastRaw,
            lastRaw is null
                ? $"Панель не ответила на handshake за {attempts} попыток (проверь, не занимает ли порт TRCC.exe)"
                : $"Ответ не похож на HID LCD Type 2: {Convert.ToHexString(lastRaw)}");
    }

    /// <summary>
    /// В ответе HID первым байтом идёт Report ID. Если магии нет с нуля,
    /// но она есть со смещения 1 — срезаем байт отчёта.
    /// </summary>
    private static (byte[] Data, int Stripped) Normalize(byte[] report)
    {
        if (report.Length >= 4 && report.AsSpan(0, 4).SequenceEqual(Magic))
            return (report, 0);

        if (report.Length >= 5 && report.AsSpan(1, 4).SequenceEqual(Magic))
            return (report[1..], 1);

        return (report, 0);
    }

    private static bool IsValid(byte[] data)
    {
        if (data.Length < 6) return false;
        if (!data.AsSpan(0, 4).SequenceEqual(Magic)) return false;

        // Короткий ответ (firmware 4.07) принимаем по магии,
        // полный — ещё и по признаку успеха в байте 12.
        return data.Length < 20 || data[12] == 0x01;
    }

    public static HandshakeResult Parse(byte[] data, byte[] raw, int stripped = 0)
    {
        var pm = data[5];
        var sub = data[4];
        var hasSerial = data.Length >= 36 && data[16] == 0x10;
        var serial = hasSerial ? Convert.ToHexString(data.AsSpan(20, 16)) : "";

        var fbl = VendorTables.PmToFbl(pm, sub);
        var profile = VendorTables.GetProfile(fbl, pm, sub);

        return new HandshakeResult(pm, sub, fbl, serial, profile, raw, stripped);
    }

    /// <summary>
    /// 20-байтный заголовок кадра RGB565. Для RGB565 вендорская прошивка
    /// жёстко пишет 240x320 независимо от реального размера панели.
    /// </summary>
    public static byte[] BuildFrameHeader(int payloadLength, int declaredWidth = 240, int declaredHeight = 320)
    {
        var header = new byte[20];
        Magic.CopyTo(header, 0);
        header[4] = 0x02;   // cmd_type = PICTURE
        header[6] = 0x01;   // RGB565, не JPEG
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)declaredWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)declaredHeight);
        header[12] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)payloadLength);
        return header;
    }

    /// <summary>Полный пакет кадра, выровненный по 512 байт.</summary>
    public static byte[] BuildFramePacket(ReadOnlySpan<byte> rgb565, int declaredWidth = 240, int declaredHeight = 320)
    {
        var header = BuildFrameHeader(rgb565.Length, declaredWidth, declaredHeight);
        var raw = header.Length + rgb565.Length;
        var aligned = (raw + 511) / 512 * 512;

        var packet = new byte[aligned];
        header.CopyTo(packet, 0);
        rgb565.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    /// <summary>
    /// Отправка кадра. Критично: панель ловит картинку только когда она
    /// приходит последовательностью 512-байтных отчётов, а не одним блоком.
    /// Буфер пакета переиспользуется: кадр — это ~300 отчётов, и выделять
    /// 150 КБ заново на каждый кадр незачем.
    /// </summary>
    public void SendFrame(ReadOnlySpan<byte> rgb565, int declaredWidth = 240, int declaredHeight = 320)
    {
        var header = BuildFrameHeader(rgb565.Length, declaredWidth, declaredHeight);
        var raw = header.Length + rgb565.Length;
        var aligned = (raw + 511) / 512 * 512;

        if (_packet.Length < aligned)
            _packet = new byte[aligned];

        header.CopyTo(_packet, 0);
        rgb565.CopyTo(_packet.AsSpan(header.Length));

        // Выравнивающий хвост должен быть нулевым — в переиспользуемом буфере
        // там может лежать конец прошлого кадра.
        _packet.AsSpan(raw, aligned - raw).Clear();

        for (var offset = 0; offset < aligned; offset += 512)
            _link.WriteChunk(_packet.AsSpan(offset, Math.Min(512, aligned - offset)));

        Thread.Sleep(1);
        FramesSent++;
    }
}

public sealed class HandshakeException : Exception
{
    public byte[]? RawResponse { get; }

    public HandshakeException(byte[]? rawResponse, string message) : base(message)
    {
        RawResponse = rawResponse;
    }
}
