// TrayIconNative.cs — Shell_NotifyIcon wrapper used by MainWindow.Tray.cs.
//
// WHY NOT System.Windows.Forms.NotifyIcon (which this replaced):
// the shell identifies a tray icon either by the pair (hWnd, uID) or by a GUID.
// NotifyIcon only ever uses the first form: the hWnd is a throwaway NativeWindow
// created at runtime and the uID comes from a per-process counter starting at 1.
// Both are values Windows recycles freely across processes, so K2's icon could
// land on the exact (hWnd, uID) another program had already registered — the
// shell then treats the two registrations as the same icon: they share one slot
// (K2's icon drawn over the other app's) and every mouse notification is
// dispatched to both windows, which is why a double click opened K2 *and* the
// other program.
//
// The fix is the identity form Microsoft recommends for exactly this reason:
// NIF_GUID with a hard-coded, per-application GUID (TrayGuid below). It is
// stable across runs and cannot collide with another app.
//
// Caveat of GUID identity: the shell binds the GUID to the executable's full
// path, so the same GUID used from a different path (e.g. running the bin\Debug
// build after the installed one under Program Files) makes Shell_NotifyIcon fail.
// AddIcon therefore falls back to plain (hWnd, uID) identity when the GUID form
// is refused — no worse than the old behaviour.
//
// Everything else mirrors what NotifyIcon did: a hidden message-only window
// receives the callback message, the WinForms ContextMenuStrip is reused as-is
// (shown by hand on WM_CONTEXTMENU), and the icon is re-added when Explorer
// restarts ("TaskbarCreated").

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace K2.App.Services;

internal sealed class TrayIconNative : IDisposable
{
    // Identity of K2's tray icon. Never change this value: the shell keys the
    // icon's user-visible settings (pinned / hidden in the overflow flyout) on it.
    private static readonly Guid TrayGuid = new("6C9C2B4E-0F2A-4C31-9E7B-1A5D0B3E7C42");

    private const int WM_APP      = 0x8000;
    private const int CallbackMsg = WM_APP + 0x21;

    private const int WM_USER          = 0x0400;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_CONTEXTMENU   = 0x007B;
    // NIN_SELECT is WM_USER-based (shellapi.h), not WM_APP-based — do not confuse
    // with CallbackMsg above, which IS WM_APP-based (that's the outer message id;
    // this is the notification code carried in its lParam).
    private const int NIN_SELECT       = WM_USER + 0;
    // Balloon/toast notifications: the shell reports the user clicking the toast
    // body (as opposed to letting it time out or dismissing it) with this code.
    private const int NIN_BALLOONUSERCLICK = WM_USER + 5;

    private const int NIM_ADD        = 0x00;
    private const int NIM_MODIFY     = 0x01;
    private const int NIM_DELETE     = 0x02;
    private const int NIM_SETVERSION = 0x04;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON    = 0x02;
    private const int NIF_TIP     = 0x04;
    private const int NIF_GUID    = 0x20;
    private const int NIF_INFO    = 0x10;

    // dwInfoFlags: NIIF_USER draws hBalloonIcon (K2's own icon) in the toast
    // instead of one of the shell's generic info/warning glyphs.
    private const int NIIF_USER       = 0x04;
    private const int NIIF_LARGE_ICON = 0x20;

    private const int NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int    cbSize;
        public IntPtr hWnd;
        public int    uID;
        public int    uFlags;
        public int    uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int    dwState;
        public int    dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int    uVersion;   // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string szInfoTitle;
        public int    dwInfoFlags;
        public Guid   guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static readonly int WmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private sealed class MessageWindow : NativeWindow
    {
        private readonly TrayIconNative _owner;

        public MessageWindow(TrayIconNative owner)
        {
            _owner = owner;
            // Message-only window (HWND_MESSAGE parent): never shown, never taskbar-listed.
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == CallbackMsg)
            {
                // NOTIFYICON_VERSION_4: the notification id is in LOWORD(lParam),
                // the anchor point in wParam (screen coordinates).
                _owner.OnCallback(
                    (int)((long)m.LParam & 0xFFFF),
                    (short)((long)m.WParam & 0xFFFF),
                    (short)(((long)m.WParam >> 16) & 0xFFFF));
                return;
            }
            if (m.Msg == WmTaskbarCreated && _owner._visible)
            {
                // Explorer restarted: every icon registration was dropped with it.
                _owner._added = false;
                _owner.AddIcon();
                return;
            }
            base.WndProc(ref m);
        }
    }

    private readonly MessageWindow _window;
    private Icon? _icon;
    private string _text = string.Empty;
    private bool _visible;
    private bool _added;
    private bool _useGuid = true;
    private bool _disposed;

    public TrayIconNative() => _window = new MessageWindow(this);

    /// <summary>Fired on a left double click (or keyboard/single-click select) on the icon.</summary>
    public event EventHandler? DoubleClick;

    /// <summary>Fired when the user clicks the body of a balloon shown by
    /// <see cref="ShowBalloon"/> (not when it merely times out or is dismissed).</summary>
    public event EventHandler? BalloonClick;

    /// <summary>Menu shown on right click. Owned by the caller, same as NotifyIcon.</summary>
    public ContextMenuStrip? ContextMenuStrip { get; set; }

    public Icon? Icon
    {
        get => _icon;
        set { _icon = value; if (_added) ModifyIcon(); }
    }

    /// <summary>Tooltip text; truncated to the shell's 127-character limit.</summary>
    public string Text
    {
        get => _text;
        set
        {
            _text = value.Length > 127 ? value[..127] : value;
            if (_added) ModifyIcon();
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) AddIcon(); else RemoveIcon();
        }
    }

    private NOTIFYICONDATA NewData(bool withGuid)
    {
        var d = new NOTIFYICONDATA
        {
            hWnd             = _window.Handle,
            uID              = 1,
            uCallbackMessage = CallbackMsg,
            hIcon            = _icon?.Handle ?? IntPtr.Zero,
            szTip            = _text,
            szInfo           = string.Empty,
            szInfoTitle      = string.Empty,
            uFlags           = NIF_MESSAGE | NIF_ICON | NIF_TIP | (withGuid ? NIF_GUID : 0),
            guidItem         = withGuid ? TrayGuid : Guid.Empty,
        };
        d.cbSize = Marshal.SizeOf<NOTIFYICONDATA>();
        return d;
    }

    private void AddIcon()
    {
        if (_added || _disposed) return;

        // Clear a registration left behind by a previous instance that died without
        // deleting its icon — otherwise NIM_ADD for the same GUID is refused.
        var stale = NewData(withGuid: true);
        Shell_NotifyIconW(NIM_DELETE, ref stale);

        _useGuid = true;
        var data = NewData(withGuid: true);
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            // GUID already bound to this app at another path (see header comment):
            // fall back to (hWnd, uID) identity rather than showing no icon at all.
            _useGuid = false;
            data = NewData(withGuid: false);
            if (!Shell_NotifyIconW(NIM_ADD, ref data)) return;
        }

        // Opt into NOTIFYICON_VERSION_4 semantics (reliable NIN_* notifications and
        // a proper anchor point for the context menu).
        var ver = NewData(_useGuid);
        ver.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref ver);

        _added = true;
    }

    private void ModifyIcon()
    {
        var data = NewData(_useGuid);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        if (!_added) return;
        var data = NewData(_useGuid);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>Shows a shell balloon on the tray icon — on Windows 10/11 the shell
    /// renders it as a normal toast in the Action Center. Silently does nothing if the
    /// icon isn't registered (or the user turned K2's notifications off in Windows:
    /// the shell simply drops the request, there is no error to report).
    /// Title/text are truncated to the NOTIFYICONDATA field sizes — passing anything
    /// longer would throw at marshalling time.</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added || _disposed) return;

        var data = NewData(_useGuid);
        data.uFlags |= NIF_INFO;
        data.szInfoTitle = title.Length > 63  ? title[..63]  : title;
        data.szInfo      = text.Length  > 255 ? text[..255]  : text;
        data.dwInfoFlags = _icon is not null ? NIIF_USER | NIIF_LARGE_ICON : 0;
        data.hBalloonIcon = _icon?.Handle ?? IntPtr.Zero;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void OnCallback(int id, int x, int y)
    {
        switch (id)
        {
            case NIN_BALLOONUSERCLICK:
                BalloonClick?.Invoke(this, EventArgs.Empty);
                break;

            case WM_LBUTTONDBLCLK:
            case NIN_SELECT:
                DoubleClick?.Invoke(this, EventArgs.Empty);
                break;

            case WM_CONTEXTMENU:
                if (ContextMenuStrip is null) break;
                // Without this the menu does not close when the user clicks elsewhere
                // (documented requirement for menus owned by a hidden window).
                SetForegroundWindow(_window.Handle);
                ContextMenuStrip.Show(new Point(x, y));
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        _window.DestroyHandle();
    }
}
