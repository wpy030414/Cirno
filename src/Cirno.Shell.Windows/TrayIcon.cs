using System.Runtime.InteropServices;

namespace Cirno.Shell.Windows;

/// <summary>
/// 通知区图标（托盘）。菜单用 Win32 原生弹出菜单，
/// 因为 WinUI 的 MenuFlyout 需要 XAML 焦点，托盘点击时窗口可能并不在前台。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint MenuShow = 1;
    private const uint MenuNewSession = 2;
    private const uint MenuExit = 3;

    private readonly IntPtr _hwnd;
    private readonly uint _callbackMessage;
    private readonly Action _onShow;
    private readonly Action _onNewSession;
    private readonly Action _onExit;
    private IntPtr _icon;
    private bool _visible;

    public TrayIcon(IntPtr hwnd, uint callbackMessage, Action onShow, Action onNewSession, Action onExit)
    {
        _hwnd = hwnd;
        _callbackMessage = callbackMessage;
        _onShow = onShow;
        _onNewSession = onNewSession;
        _onExit = onExit;
    }

    public bool Show(IntPtr icon, string tooltip)
    {
        _icon = icon;
        var data = CreateData(icon, tooltip);

        if (!_visible)
        {
            _visible = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
        }
        else
        {
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
        }

        return _visible;
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_visible)
        {
            return;
        }

        var data = CreateData(_icon, tooltip);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>由窗口消息分发调用；返回 true 表示已处理。</summary>
    public bool HandleMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message != _callbackMessage)
        {
            return false;
        }

        // 未调用 NIM_SETVERSION 时，lParam 就是鼠标消息
        var mouseMessage = (int)lParam;
        if (mouseMessage is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONUP)
        {
            ShowMenu();
        }

        return true;
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, MenuShow, "显示 Cirno");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, MenuNewSession, "新建会话");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, MenuExit, "退出");

            NativeMethods.GetCursorPos(out var point);

            // 经典坑：不先把窗口提到前台，菜单点空白处不会关闭
            NativeMethods.SetForegroundWindow(_hwnd);

            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                _hwnd,
                IntPtr.Zero);

            NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch ((uint)command)
            {
                case MenuShow:
                    _onShow();
                    break;
                case MenuNewSession:
                    _onNewSession();
                    break;
                case MenuExit:
                    _onExit();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private NativeMethods.NOTIFYICONDATAW CreateData(IntPtr icon, string tooltip) => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP,
        uCallbackMessage = _callbackMessage,
        hIcon = icon,
        szTip = tooltip.Length > 120 ? tooltip[..120] : tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
        guidItem = Guid.Empty,
    };

    public void Dispose()
    {
        if (_visible)
        {
            var data = CreateData(_icon, string.Empty);
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _visible = false;
        }

        if (_icon != IntPtr.Zero)
        {
            StatusIconFactory.Destroy(_icon);
            _icon = IntPtr.Zero;
        }
    }
}
