namespace TrccSharp.Core.WinUsb;

/// <summary>Одно найденное USB-устройство и его путь для CreateFile.</summary>
public sealed record UsbDeviceNode(string DevicePath, ushort VendorId, ushort ProductId, byte InterfaceNumber)
{
    public string VidPid => $"{VendorId:X4}:{ProductId:X4}";

    public override string ToString() => $"{VidPid} (MI_{InterfaceNumber:D2}) {DevicePath}";
}

/// <summary>Перечисление интерфейсов через SetupAPI.</summary>
public static class UsbEnumerator
{
    /// <summary>
    /// Все интерфейсы USB-устройств, доступные приложению.
    /// Разбор VID/PID идёт из пути вида
    /// <c>\\?\usb#vid_0416&amp;pid_5302&amp;mi_01#...</c>.
    /// </summary>
    public static IReadOnlyList<UsbDeviceNode> Enumerate()
    {
        var result = new List<UsbDeviceNode>();

        foreach (var path in SetupApi.EnumerateInterfacePaths(WinUsbNative.UsbDeviceInterfaceGuid))
            if (TryParsePath(path, out var node))
                result.Add(node);

        return result;
    }

    internal static bool TryParsePath(string path, out UsbDeviceNode node)
    {
        node = null!;
        var lower = path.ToLowerInvariant();

        var vid = ExtractHex(lower, "vid_");
        var pid = ExtractHex(lower, "pid_");
        if (vid is null || pid is null) return false;

        var mi = ExtractHex(lower, "mi_") ?? 0;
        node = new UsbDeviceNode(path, (ushort)vid, (ushort)pid, (byte)mi);
        return true;
    }

    private static int? ExtractHex(string s, string marker)
    {
        var idx = s.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        idx += marker.Length;
        if (idx + 4 > s.Length) return null;
        return int.TryParse(s.AsSpan(idx, 4), System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? value
            : null;
    }
}
