using System.ComponentModel;
using System.Runtime.InteropServices;

namespace March7th.Desktop;

// A separate mouse-only Raw Input window avoids replacing RDP's keyboard target.
internal sealed class LocalMouse : NativeWindow, IDisposable
{
    public event Action<int, int>? Moved;
    private bool _registered;
    private bool _captured;
    private Rect _previousClip;
    private int _hideCount;

    public LocalMouse() => CreateHandle(new CreateParams { Caption = "March7th Raw Mouse", Parent = new IntPtr(-3) });

    public void Start()
    {
        if (_registered) return;
        var devices = new[] { new RawDevice { UsagePage = 1, Usage = 2, Flags = 0x100, Target = Handle } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听相对鼠标移动。");
        _registered = true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0xFF)
        {
            uint size = 0;
            var headerSize = (uint)Marshal.SizeOf<RawHeader>();
            if (GetRawInputData(message.LParam, 0x10000003, IntPtr.Zero, ref size, headerSize) == 0 && size <= 4096 && size >= headerSize + 24)
            {
                var data = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(message.LParam, 0x10000003, data, ref size, headerSize) == size)
                    {
                        var header = Marshal.PtrToStructure<RawHeader>(data);
                        var mouse = IntPtr.Add(data, (int)headerSize);
                        if (header.Type == 0 && (Marshal.ReadInt16(mouse) & 1) == 0)
                            Moved?.Invoke(Marshal.ReadInt32(mouse, 12), Marshal.ReadInt32(mouse, 16));
                    }
                }
                finally { Marshal.FreeHGlobal(data); }
            }
        }
        base.WndProc(ref message);
    }

    public void Capture(Rectangle bounds)
    {
        if (!_captured)
        {
            if (!GetClipCursor(out _previousClip)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _captured = true;
            do { _hideCount++; } while (ShowCursor(false) >= 0 && _hideCount < 32);
        }
        var rect = new Rect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
        if (!ClipCursor(ref rect))
        {
            Release();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法限制分身窗口鼠标。");
        }
    }

    public void Release()
    {
        if (!_captured) return;
        ClipCursor(ref _previousClip);
        while (_hideCount > 0) { ShowCursor(true); _hideCount--; }
        _captured = false;
    }

    public static bool AltPressed => (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;
    public static bool IsForeground(IntPtr handle) => GetForegroundWindow() == handle;

    public void Dispose()
    {
        Release();
        if (_registered)
        {
            var devices = new[] { new RawDevice { UsagePage = 1, Usage = 2, Flags = 1 } };
            RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawDevice>());
        }
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClipCursor(ref Rect rect);
    [DllImport("user32.dll")] private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
