using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace TrccSharp.App;

/// <summary>
/// Иконка в области уведомлений. Сделана на shell32 напрямую, без WinForms:
/// подключение Windows Forms тянет за собой System.Drawing и конфликтует
/// с WPF-типами (Color, Brush) по всему проекту.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmTrayIcon = 0x8001;      // WM_APP + 1
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;

    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;

    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;
    private const int NifInfo = 0x10;

    private const int IdiApplication = 32512;
    private const int IconId = 1;

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly ContextMenu _menu;
    private bool _disposed;

    /// <summary>Двойной щелчок по иконке.</summary>
    public event Action? Activate;

    /// <summary>Пункт «Выход» в меню иконки.</summary>
    public event Action? ExitRequested;

    public TrayIcon(string tooltip, string openText, string exitText)
    {
        // Обычное скрытое окно: message-only окна не всегда получают уведомления оболочки.
        _window = new HwndSource(0, 0, 0, 0, 0, "TrccSharpTray", IntPtr.Zero);
        _window.AddHook(WndProc);

        _icon = LoadIcon(IntPtr.Zero, (IntPtr)IdiApplication);

        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var open = new MenuItem { Header = openText };
        open.Click += (_, _) => Activate?.Invoke();
        var exit = new MenuItem { Header = exitText };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(open);
        _menu.Items.Add(exit);

        Send(NimAdd, NifMessage | NifIcon | NifTip, tooltip);
    }

    /// <summary>Всплывающая подсказка над иконкой (показывается один раз).</summary>
    public void ShowBalloon(string title, string text)
    {
        var data = CreateData(NifInfo, "");
        data.szInfoTitle = Cut(title, 63);
        data.szInfo = Cut(text, 255);
        data.dwInfoFlags = 1;   // NIIF_INFO
        Shell_NotifyIcon(NimModify, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmTrayIcon) return IntPtr.Zero;

        switch ((int)lParam)
        {
            case WmLButtonDblClk:
                Activate?.Invoke();
                handled = true;
                break;
            case WmRButtonUp:
                _menu.IsOpen = true;
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void Send(int message, int flags, string tip)
    {
        var data = CreateData(flags, tip);
        Shell_NotifyIcon(message, ref data);
    }

    private NOTIFYICONDATA CreateData(int flags, string tip) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WmTrayIcon,
        hIcon = _icon,
        szTip = Cut(tip, 127),
        szInfo = "",
        szInfoTitle = ""
    };

    private static string Cut(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Send(NimDelete, 0, "");

        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
}
