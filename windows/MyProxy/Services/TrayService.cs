using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// 托盘图标，直接走 <c>Shell_NotifyIcon</c>。
///
/// 为什么不用 WinForms 的 NotifyIcon：那一个类会把整个 WinForms 栈拖进自包含发布，
/// 实测 21.1 MB（System.Windows.Forms + .Design + .Primitives + Interop），
/// 而整个项目用到 WinForms 的地方只有这一个文件。菜单改用 WPF 原生的 ContextMenu，
/// 于是 &lt;UseWindowsForms&gt; 可以整个关掉。
///
/// 三个容易踩空的地方，都在下面各自的注释里：隐藏的顶层窗口、
/// explorer 重启后要重新登记图标、弹菜单前要抢前台。
/// </summary>
public sealed class TrayService : ITrayService
{
    private const int WmApp = 0x8000;
    private const int TrayCallbackMessage = WmApp + 1;

    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;

    private const uint NiifInfo = 0x00000001;

    private const int WsExToolWindow = 0x00000080;

    private readonly HwndSource _source;
    private readonly ContextMenu _menu;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _stopItem;
    private readonly ILogService? _log;

    /// <summary>explorer 重启后要用它重新登记图标。</summary>
    private readonly uint _taskbarCreatedMessage;

    private IntPtr _icon;
    private bool _iconAdded;
    private bool _backgroundHintShown;
    private bool _disposed;

    public TrayService(ILogService? log = null)
    {
        _log = log;

        // TaskbarCreated 只广播给顶层窗口，message-only 窗口收不到。
        // 显式去掉 WS_VISIBLE，并独立于主窗口保留这个消息接收器。
        _source = new HwndSource(new HwndSourceParameters("MyProxyTray")
        {
            Width = 0,
            Height = 0,
            ParentWindow = IntPtr.Zero,
            WindowStyle = 0,
            ExtendedWindowStyle = WsExToolWindow
        });
        _source.AddHook(WndProc);

        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadAppIcon();

        var titleItem = new MenuItem { Header = AppInfo.ProductName, IsEnabled = false };
        _statusItem = new MenuItem { Header = "未连接", IsEnabled = false };
        var openItem = new MenuItem { Header = "打开客户端" };
        _stopItem = new MenuItem { Header = "停止代理" };
        var exitItem = new MenuItem { Header = "退出" };

        openItem.Click += (_, _) => OpenRequested?.Invoke();
        _stopItem.Click += (_, _) => StopRequested?.Invoke();
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu = new ContextMenu();
        _menu.Items.Add(titleItem);
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(openItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exitItem);
    }

    public event Action? OpenRequested;
    public event Action? StopRequested;
    public event Action? ExitRequested;

    internal IntPtr WindowHandle => _source.Handle;

    public void Show()
    {
        if (_disposed || _iconAdded)
        {
            return;
        }

        NOTIFYICONDATAW data = BaseData(NifMessage | NifIcon | NifTip);
        data.szTip = AppInfo.ProductName;

        _iconAdded = Shell_NotifyIconW(NimAdd, ref data);
        if (!_iconAdded)
        {
            _log?.Warn(nameof(TrayService), "Shell_NotifyIcon(NIM_ADD) 失败，托盘图标不可用");
        }
    }

    public void ShowBackgroundHint()
    {
        if (_disposed || !_iconAdded || _backgroundHintShown)
        {
            return;
        }

        _backgroundHintShown = true;

        NOTIFYICONDATAW data = BaseData(NifInfo);
        data.szInfoTitle = AppInfo.ProductName;
        data.szInfo = "仍在后台运行，可从系统托盘打开。";
        data.dwInfoFlags = NiifInfo;

        Shell_NotifyIconW(NimModify, ref data);
    }

    public void UpdateState(AppState state, ProxyMode mode)
    {
        _statusItem.Header = TrayPresentation.StatusLine(state, mode);
        _stopItem.IsEnabled = TrayPresentation.CanStop(state);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_iconAdded)
        {
            NOTIFYICONDATAW data = BaseData(0);
            Shell_NotifyIconW(NimDelete, ref data);
            _iconAdded = false;
        }

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private NOTIFYICONDATAW BaseData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _source.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = TrayCallbackMessage,
        hIcon = _icon,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // explorer.exe 崩溃重启后，整个通知区被重建，之前登记的图标随之消失。
        // 系统用这条广播消息通知所有人重新登记——不处理它，托盘图标就会在
        // explorer 重启后永久不见，而应用自己毫无察觉。
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            _iconAdded = false;
            Show();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != TrayCallbackMessage)
        {
            return IntPtr.Zero;
        }

        switch ((int)lParam & 0xFFFF)
        {
            case WmLButtonDblClk:
                OpenRequested?.Invoke();
                handled = true;
                break;

            case WmRButtonUp:
            case WmContextMenu:
                ShowMenu();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // 托盘菜单的老问题：不先抢到前台，点击菜单外部时它不会关闭，
        // 会一直挂在屏幕上直到再次点中它。
        SetForegroundWindow(_source.Handle);

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    /// <summary>
    /// 从自身 exe 里取图标。取不到就交给系统的默认应用图标——
    /// 托盘没图标也得有个占位，否则用户会以为程序没起来。
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        string exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "MyProxy.exe");
        if (File.Exists(exePath)
            && ExtractIconExW(exePath, 0, out IntPtr large, out IntPtr small, 1) > 0)
        {
            if (large != IntPtr.Zero)
            {
                DestroyIcon(large);
            }

            if (small != IntPtr.Zero)
            {
                return small;
            }
        }

        return LoadIconW(IntPtr.Zero, new IntPtr(32512));   // IDI_APPLICATION
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconExW(
        string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);
}
