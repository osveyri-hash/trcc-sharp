using TrccSharp.Core.WinUsb;

namespace TrccSharp.Core.Protocol;

/// <summary>
/// Один канал до панели: отправка порций по 512 байт и чтение ответов.
/// Реализации — HID-интерфейс (MI_00) и vendor WinUSB (MI_01).
/// </summary>
public interface ILcdLink : IDisposable
{
    string Description { get; }

    /// <summary>Отправка одной порции протокола (не больше 512 байт).</summary>
    void WriteChunk(ReadOnlySpan<byte> data);

    /// <summary>Чтение ответа с таймаутом; null — ответа не было.</summary>
    byte[]? TryRead(int timeoutMs);
}

/// <summary>Канал через vendor WinUSB-интерфейс: bulk EP02 OUT / EP01 IN.</summary>
public sealed class WinUsbLink : ILcdLink
{
    private const byte EpOut = 0x02;
    private const byte EpIn = 0x01;

    /// <summary>GUID_DEVCLASS_USBDEVICE — класс WinUSB-устройств с MS OS 2.0 descriptors.</summary>
    public static readonly Guid UsbDeviceClassGuid = new("88BAE032-5A81-49F0-BC3D-A4FF138216D6");

    private readonly WinUsbDevice _device;

    private WinUsbLink(WinUsbDevice device) => _device = device;

    public string Description =>
        $"WinUSB {_device.VendorId:X4}:{_device.ProductId:X4} MI_{_device.InterfaceNumber:D2}, " +
        $"пайпы: {string.Join(", ", _device.Pipes.Values)}";

    public static WinUsbLink Open(ushort vendorId = 0x0416, ushort productId = 0x5302)
    {
        var nodes = WinUsbClassEnumerator.Enumerate();

        var node = nodes.FirstOrDefault(n => n.VendorId == vendorId && n.ProductId == productId)
                   ?? throw new InvalidOperationException(
                       $"WinUSB-интерфейс {vendorId:X4}:{productId:X4} не найден");

        var device = WinUsbDevice.Open(node);
        var link = new WinUsbLink(device);

        if (!device.Pipes.ContainsKey(EpOut) || !device.Pipes.ContainsKey(EpIn))
        {
            device.Dispose();
            throw new InvalidOperationException(
                $"У интерфейса нет ожидаемых пайпов 0x{EpIn:X2}/0x{EpOut:X2}: " +
                string.Join(", ", device.Pipes.Values));
        }

        return link;
    }

    public void WriteChunk(ReadOnlySpan<byte> data)
    {
        var sent = _device.Write(EpOut, data);
        if (sent < data.Length)
            throw new IOException($"Короткая запись: {sent} из {data.Length}");
    }

    public byte[]? TryRead(int timeoutMs)
    {
        var buffer = new byte[1024];
        var read = _device.Read(EpIn, buffer, buffer.Length);
        return read <= 0 ? null : buffer[..read];
    }

    public void Dispose() => _device.Dispose();
}

/// <summary>Перечисление WinUSB-устройств по классу USBDevice.</summary>
public static class WinUsbClassEnumerator
{
    public static IReadOnlyList<UsbDeviceNode> Enumerate()
    {
        var result = new List<UsbDeviceNode>();
        foreach (var path in SetupApi.EnumerateInterfacePaths(WinUsbLink.UsbDeviceClassGuid))
            if (UsbEnumerator.TryParsePath(path, out var node))
                result.Add(node);
        return result;
    }
}
