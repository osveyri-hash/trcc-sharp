using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TrccSharp.Core.WinUsb;

namespace TrccSharp.Core.Hid;

/// <summary>Найденный HID-интерфейс вместе с его capabilities.</summary>
public sealed record HidDeviceInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportLength,
    ushort OutputReportLength,
    ushort FeatureReportLength)
{
    public override string ToString() =>
        $"{VendorId:X4}:{ProductId:X4} v{VersionNumber} " +
        $"usage={UsagePage:X4}:{Usage:X4} in={InputReportLength} out={OutputReportLength} feat={FeatureReportLength}";
}

/// <summary>Перечисление и открытие HID-устройств.</summary>
public static class HidEnumerator
{
    public static IReadOnlyList<HidDeviceInfo> Enumerate()
    {
        var result = new List<HidDeviceInfo>();
        foreach (var path in SetupApi.EnumerateInterfacePaths(HidNative.HidInterfaceGuid))
        {
            try
            {
                using var handle = Open(path, queryOnly: true);
                var info = Describe(path, handle);
                if (info is not null) result.Add(info);
            }
            catch
            {
                // Устройство могли забрать между перечислением и открытием
            }
        }

        return result;
    }

    public static SafeFileHandle Open(string path, bool queryOnly = false)
    {
        var access = queryOnly ? 0u : 0x80000000u | 0x40000000u;
        var handle = NativeFile.CreateFile(path, access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Не открыть HID-устройство: {path}");
        return handle;
    }

    private static HidDeviceInfo? Describe(string path, SafeFileHandle handle)
    {
        var attributes = new HidNative.HiddAttributes { Size = (uint)Marshal.SizeOf<HidNative.HiddAttributes>() };
        if (!HidNative.HidD_GetAttributes(handle, ref attributes)) return null;

        if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed)) return null;
        try
        {
            if (HidNative.HidP_GetCaps(preparsed, out var caps) < 0) return null;

            return new HidDeviceInfo(path, attributes.VendorId, attributes.ProductId,
                attributes.VersionNumber, caps.UsagePage, caps.Usage,
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength);
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }
    }
}

internal static class NativeFile
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);
}
