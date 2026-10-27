using System.Runtime.InteropServices;

namespace TrccSharp.Core.WinUsb;

/// <summary>Общий обход интерфейсов через SetupAPI (используется и для USB, и для HID).</summary>
public static class SetupApi
{
    /// <summary>Все зарегистрированные пути интерфейсов заданного класса.</summary>
    public static IReadOnlyList<string> EnumerateInterfacePaths(Guid interfaceClass)
    {
        var paths = new List<string>();
        var devInfo = WinUsbNative.SetupDiGetClassDevs(ref interfaceClass, IntPtr.Zero, IntPtr.Zero,
            WinUsbNative.DigcfPresent | WinUsbNative.DigcfDeviceInterface);

        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
            return paths;

        try
        {
            var iface = new WinUsbNative.SpDeviceInterfaceData
            {
                CbSize = Marshal.SizeOf<WinUsbNative.SpDeviceInterfaceData>()
            };

            for (var i = 0; WinUsbNative.SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref interfaceClass, i, ref iface); i++)
            {
                if (!WinUsbNative.SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero)
                    && required <= 0)
                    continue;

                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize у SP_DEVICE_INTERFACE_DETAIL_DATA: 8 на x64, 6 на x86
                    Marshal.WriteInt32(buffer, 0, IntPtr.Size == 8 ? 8 : 6);
                    if (!WinUsbNative.SetupDiGetDeviceInterfaceDetail(devInfo, ref iface, buffer, required, out _, IntPtr.Zero))
                        continue;

                    var path = Marshal.PtrToStringUni(buffer + 4);
                    if (!string.IsNullOrEmpty(path)) paths.Add(path);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            WinUsbNative.SetupDiDestroyDeviceInfoList(devInfo);
        }

        return paths;
    }
}
