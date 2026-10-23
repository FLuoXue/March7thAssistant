using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace March7th.Desktop;

internal sealed class DesktopWindow : Form
{
    private readonly LaunchOptions _options;
    private readonly RdpActiveXHost _rdp = new();
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill, BackColor = Color.Black, AutoScroll = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 32, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 20 };
    private readonly MouseFlow _flow = new();
    private readonly LocalMouse _mouse = new();
    private readonly MousePipe _pipe;
    private readonly ToolStripButton _connect = new("启动并连接");
    private readonly ToolStripButton _launch = new("启动三月七");
    private readonly ToolStripButton _gameMouse = new("普通鼠标") { CheckOnClick = true };
    private readonly ToolStripButton _pin = new("置顶") { CheckOnClick = true };
    private readonly ToolStripButton _mute = new("静音") { CheckOnClick = true };
    private readonly ToolStripMenuItem _adaptive = new("自适应缩放") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _aspect = new("保持 16:9") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _shortcuts = new("系统快捷键发送到分身") { CheckOnClick = true, Checked = true };
    private readonly Size _desktopSize = new(1920, 1080);
    private uint? _ownedSession;
    private bool _connecting;
    private bool _launching;
    private bool _closing;
    private bool _reconfiguring;
    private bool _loginCompleted;
    private long _connectedAt;
    private long _lastStatus;
    private int _retryRemaining;
    private bool _retryPending;
    private bool _fittingWindow;
    private Size _normalSize = new(1280, 800);
    private readonly string _settingsPath;
    private readonly bool _persistSettings;

    public DesktopWindow(LaunchOptions options, bool persistSettings = true)
    {
        _options = options;
        _persistSettings = persistSettings;
        _settingsPath = Path.Combine(options.Directory, "config", "desktop-session-window.json");
        Text = options.Title;
        MinimumSize = new Size(500, 340);
        Size = _normalSize;
        StartPosition = FormStartPosition.CenterScreen;
        var icon = Path.Combine(options.Directory, "assets", "logo", "March7th.ico");
        if (File.Exists(icon)) Icon = new Icon(icon);
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        toolbar.Items.AddRange([_connect, _launch, new ToolStripSeparator(), _gameMouse, _pin, _mute]);
        var more = new ToolStripDropDownButton("控制中心");
        more.DropDownItems.Add("显示桌面", null, (_, _) => Run(_rdp.SendShowDesktopShortcut));
        more.DropDownItems.Add("任务视图", null, (_, _) => Run(_rdp.SendTaskViewShortcut));
        more.DropDownItems.Add("以管理员启动…", null, async (_, _) => await LaunchOtherAsync());
        more.DropDownItems.Add(new ToolStripSeparator());
        more.DropDownItems.Add(_adaptive);
        more.DropDownItems.Add(_aspect);
        more.DropDownItems.Add(_shortcuts);
        more.DropDownItems.Add("小窗 / 还原", null, (_, _) => ToggleSmall());
        more.DropDownItems.Add("隐藏（保持运行）", null, (_, _) => Hide());
        more.DropDownItems.Add("重新连接", null, async (_, _) => await ReconnectAsync());
        more.DropDownItems.Add(new ToolStripSeparator());
        more.DropDownItems.Add("关闭分身并注销", null, async (_, _) => await ShutdownAsync());
        toolbar.Items.Add(more);
        Controls.Add(_viewport);
        Controls.Add(_status);
        Controls.Add(toolbar);
        ((ISupportInitialize)_rdp).BeginInit();
        _viewport.Controls.Add(_rdp);
        ((ISupportInitialize)_rdp).EndInit();
        _rdp.Dock = DockStyle.None;
        _viewport.Resize += (_, _) => LayoutDesktop();
        _pipe = new MousePipe(() => _ownedSession);
        _pipe.Acknowledged += (epoch, seq, handled) => OnUi(() => _flow.Acknowledge(epoch, seq, handled));
        _pipe.ConnectionChanged += () => OnUi(() => { _flow.Reset(); _mouse.Release(); });
        _mouse.Moved += (dx, dy) => { if (CanCapture()) _flow.Add(dx, dy); };
        _rdp.LoginCompleted += async (_, _) => await LoginCompletedAsync();
        _rdp.ConnectionFailed += (_, error) =>
        {
            _connecting = false;
            _loginCompleted = false;
            ResetMouse();
            SetStatus(error.Message, true);
            _ = RetryConnectionAsync();
        };
        _connect.Click += async (_, _) => await ConnectAsync();
        _launch.Click += async (_, _) => await LaunchAssistantAsync();
        _gameMouse.CheckedChanged += (_, _) =>
        {
            _gameMouse.Text = _gameMouse.Checked ? "游戏鼠标（Alt 释放）" : "普通鼠标";
            ResetMouse();
            if (_gameMouse.Checked) Run(_mouse.Start);
        };
        _pin.CheckedChanged += (_, _) => TopMost = _pin.Checked;
        _mute.CheckedChanged += async (_, _) => await ReconfigureAsync();
        _shortcuts.CheckedChanged += async (_, _) => await ReconfigureAsync();
        _adaptive.CheckedChanged += (_, _) => Run(() => { _rdp.SetSmartSizing(_adaptive.Checked); FitWindowToDesktop(); LayoutDesktop(); });
        _aspect.CheckedChanged += (_, _) => { FitWindowToDesktop(); LayoutDesktop(); };
        DpiChanged += (_, _) => OnUi(FitWindowToDesktop);
        ResizeEnd += (_, _) => FitWindowToDesktop();
        Deactivate += (_, _) => ResetMouse();
        VisibleChanged += (_, _) => { if (!Visible) ResetMouse(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) ResetMouse(); };
        FormClosing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
                _ = ShutdownAsync();
            else { Hide(); SetStatus("已隐藏分身，任务继续运行。可从三月七工具箱重新打开。"); }
        };
        LoadSettings();
        FitWindowToDesktop();
        _normalSize = Width > LogicalToDeviceUnits(650) ? Size : AspectWindowSize(_normalSize);
        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) => StartMonitoring();
        SetStatus("点击“启动并连接”创建桌面分身；关闭窗口会隐藏，控制中心可注销。分辨率：1920×1080。");
    }

    internal void StartMonitoring()
    {
        FitWindowToDesktop();
        LayoutDesktop();
        _timer.Start();
        Program.Report("ready", "桌面分身窗口已打开。");
        _ = WatchOwnerAsync();
        _ = Task.Run(ReadCommandsAsync);
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception exception) { ResetMouse(); SetStatus(exception.GetBaseException().Message, true); }
    }

    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    private void SetStatus(string message, bool error = false)
    {
        _status.Text = message.ReplaceLineEndings(" ");
        _status.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        _lastStatus = Environment.TickCount64;
        Program.Report(error ? "error" : "status", message);
    }

    private async Task ConnectAsync(bool retry = false)
    {
        if (_closing || _connecting || _reconfiguring) return;
        try
        {
            if (!retry) _retryRemaining = 2;
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("请以管理员身份启动三月七助手后使用桌面分身。");
            var existing = ChildSessionNativeMethods.TryGetChildSessionId();
            if (existing.HasValue && existing != _ownedSession)
                throw new InvalidOperationException("已有其他程序创建的桌面分身，请先在原程序（例如 BetterGI）中关闭。三月七不会接管或注销它。");
            if (!existing.HasValue) _ownedSession = null;
            if (_rdp.ConnectedState != 0) return;
            ChildSessionNativeMethods.EnableChildSessions();
            _connecting = true;
            _loginCompleted = false;
            _connectedAt = Environment.TickCount64;
            _rdp.SetAudioMuted(_mute.Checked);
            _rdp.SetSendSystemShortcutsToRemote(_shortcuts.Checked);
            _rdp.ConnectToChildSession(_desktopSize);
            SetStatus("正在连接桌面分身，请在 Windows 登录框中完成登录。");
        }
        catch (Exception exception) { _connecting = false; SetStatus(exception.GetBaseException().Message, true); }
        await Task.CompletedTask;
    }

    private async Task RetryConnectionAsync()
    {
        if (_closing || _retryPending || _retryRemaining <= 0) return;
        _retryPending = true;
        _retryRemaining--;
        await Task.Delay(2000);
        _retryPending = false;
        if (!_closing && _rdp.ConnectedState == 0)
            await ConnectAsync(retry: true);
    }

    private async Task LoginCompletedAsync()
    {
        _connecting = false;
        _loginCompleted = true;
        _retryRemaining = 2;
        // WTS may briefly return no session immediately after the login event.
        for (var attempt = 0; attempt < 30 && !_closing; attempt++)
        {
            _ownedSession ??= ChildSessionNativeMethods.TryGetChildSessionId();
            if (_ownedSession.HasValue) break;
            await Task.Delay(200);
        }
        if (_closing) return;
        if (!_ownedSession.HasValue) { SetStatus("已登录，但尚未取得子会话编号，请重新连接。", true); return; }
        SetStatus($"桌面分身已连接，会话 {_ownedSession}。");
        if (!_pipe.Connected) await LaunchAssistantAsync();
        LayoutDesktop();
    }

    private uint RequireSession()
    {
        var session = ChildSessionNativeMethods.TryGetChildSessionId();
        if (_ownedSession is null || session != _ownedSession || !_loginCompleted || _rdp.ConnectedState != 1)
            throw new InvalidOperationException("请先连接并登录桌面分身。");
        return session.Value;
    }

    private async Task LaunchAssistantAsync()
    {
        if (_launching || _closing) return;
        _launching = true;
        try
        {
            var session = RequireSession();
            // Pipe names and PID are generated locally, so no shell interpretation is needed.
            var arguments = _options.Arguments + " --desktop-session-pipe " + _pipe.Name +
                " --desktop-session-host-pid " + Environment.ProcessId;
            await ChildSessionProcessLauncher.LaunchElevatedAsync(session, _options.Executable, arguments, _options.Directory);
            SetStatus("已启动分身内的三月七助手，等待鼠标连接。");
        }
        catch (Exception exception) { SetStatus(exception.GetBaseException().Message, true); }
        finally { _launching = false; }
    }

    private async Task LaunchOtherAsync()
    {
        try
        {
            var session = RequireSession();
            using var dialog = new OpenFileDialog { Filter = "程序 (*.exe)|*.exe", Title = "在分身中以管理员身份启动" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await ChildSessionProcessLauncher.LaunchElevatedAsync(session, dialog.FileName);
            SetStatus("已在分身中启动：" + Path.GetFileName(dialog.FileName));
        }
        catch (Exception exception) { SetStatus(exception.GetBaseException().Message, true); }
    }

    private async Task ReconfigureAsync()
    {
        if (_reconfiguring || _closing) return;
        Run(() => { _rdp.SetAudioMuted(_mute.Checked); _rdp.SetSendSystemShortcutsToRemote(_shortcuts.Checked); });
        if (_rdp.ConnectedState == 1) await ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        if (_closing || _connecting || _reconfiguring) return;
        if (_ownedSession is null) { await ConnectAsync(); return; }
        _reconfiguring = true;
        try
        {
            ResetMouse();
            _loginCompleted = false;
            _rdp.SetAudioMuted(_mute.Checked);
            _rdp.SetSendSystemShortcutsToRemote(_shortcuts.Checked);
            _rdp.ReconnectToChildSession(_desktopSize);
            _connecting = true;
            _connectedAt = Environment.TickCount64;
            SetStatus("正在重新连接，分身中的程序保持运行。");
        }
        catch (Exception exception) { _connecting = false; SetStatus(exception.GetBaseException().Message, true); }
        finally { _reconfiguring = false; }
    }

    private bool CanCapture() => !_closing && _gameMouse.Checked && Visible && ContainsFocus && LocalMouse.IsForeground(Handle) &&
        WindowState != FormWindowState.Minimized && _loginCompleted && _pipe.Connected &&
        !LocalMouse.AltPressed && _rdp.IsInputWindowFocused();

    private void ResetMouse() { _flow.Enable(false); _mouse.Release(); }

    private void Tick()
    {
        try
        {
            _connect.Enabled = !_connecting && !_closing && _rdp.ConnectedState == 0;
            _launch.Enabled = !_launching && !_closing && _loginCompleted && _ownedSession.HasValue;
            if (_loginCompleted && _rdp.ConnectedState == 0)
            {
                _loginCompleted = false;
                ResetMouse();
                SetStatus("桌面分身连接已断开，可点击“启动并连接”恢复。");
            }
            if (_connecting)
            {
                // Remember only sessions created during our own connection attempt.
                _ownedSession ??= ChildSessionNativeMethods.TryGetChildSessionId();
                if (Environment.TickCount64 - _connectedAt > 120_000)
                {
                    _rdp.DisconnectSession();
                    _connecting = false;
                    SetStatus("连接超时，请检查登录框后重新连接。", true);
                }
            }
            _flow.Enable(CanCapture());
            if (!_flow.Enabled) _mouse.Release();
            var batch = _flow.Take(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (batch is not null && !_pipe.Send(batch)) _flow.Reset();
            if (_flow.Confirmed) _mouse.Capture(_rdp.RectangleToScreen(_rdp.ClientRectangle));
            else _mouse.Release();
            if (_loginCompleted && Environment.TickCount64 - _lastStatus > 5000)
            {
                _status.Text = _pipe.Connected
                    ? $"会话 {_ownedSession} · 助手已连接 · {(_flow.Confirmed ? "游戏鼠标生效，按住 Alt 释放" : "分身运行中") }"
                    : $"会话 {_ownedSession} · 等待分身助手；可点击“启动三月七”。";
            }
        }
        catch (Exception exception)
        {
            ResetMouse();
            _gameMouse.Checked = false;
            SetStatus(exception.GetBaseException().Message, true);
        }
    }

    private void LayoutDesktop()
    {
        _viewport.AutoScroll = !_adaptive.Checked;
        if (_adaptive.Checked)
        {
            _viewport.AutoScrollMinSize = Size.Empty;
            var size = _viewport.ClientSize;
            if (_aspect.Checked && size.Width > 0 && size.Height > 0 &&
                Math.Abs(size.Height - size.Width * (double)_desktopSize.Height / _desktopSize.Width) >= 1)
            {
                var scale = Math.Min(size.Width / 1920d, size.Height / 1080d);
                var width = (int)(1920 * scale);
                var height = (int)(1080 * scale);
                _rdp.Bounds = new Rectangle((size.Width - width) / 2, (size.Height - height) / 2, width, height);
            }
            else _rdp.Bounds = new Rectangle(Point.Empty, size);
        }
        else
        {
            _viewport.AutoScrollMinSize = _desktopSize;
            _rdp.Bounds = new Rectangle(_viewport.AutoScrollPosition, _desktopSize);
        }
    }

    private Size AspectWindowSize(Size proposed, bool heightDriven = false)
    {
        // Measure the actual non-desktop area, including window borders, toolbar,
        // status bar and their current DPI scaling. The viewport must be 16:9.
        var chrome = Size - _viewport.ClientSize;
        var ratio = (double)_desktopSize.Height / _desktopSize.Width;
        var minimumWidth = Math.Max(1, Math.Max(MinimumSize.Width - chrome.Width,
            (int)Math.Ceiling((MinimumSize.Height - chrome.Height) / ratio)));
        var width = Math.Max(minimumWidth, heightDriven
            ? (int)Math.Round((proposed.Height - chrome.Height) / ratio)
            : proposed.Width - chrome.Width);
        return new Size(width + chrome.Width, (int)Math.Round(width * ratio) + chrome.Height);
    }

    private void FitWindowToDesktop()
    {
        if (_fittingWindow || !_adaptive.Checked || !_aspect.Checked || WindowState != FormWindowState.Normal) return;
        _fittingWindow = true;
        try
        {
            _viewport.AutoScroll = false;
            PerformLayout();
            Size = AspectWindowSize(Size);
            LayoutDesktop();
        }
        finally { _fittingWindow = false; }
    }

    protected override void WndProc(ref Message message)
    {
        const int WmSizing = 0x0214;
        base.WndProc(ref message);
        if (message.Msg != WmSizing || !_adaptive.Checked || !_aspect.Checked) return;
        var rect = Marshal.PtrToStructure<SizingRect>(message.LParam);
        var edge = message.WParam.ToInt32();
        var size = AspectWindowSize(new Size(rect.Right - rect.Left, rect.Bottom - rect.Top),
            heightDriven: edge is 3 or 6); // Dragging the top/bottom edge sets height.
        if (edge is 1 or 4 or 7) rect.Left = rect.Right - size.Width;
        else rect.Right = rect.Left + size.Width;
        if (edge is 3 or 4 or 5) rect.Top = rect.Bottom - size.Height;
        else rect.Bottom = rect.Top + size.Height;
        Marshal.StructureToPtr(rect, message.LParam, false);
        message.Result = new IntPtr(1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizingRect { public int Left, Top, Right, Bottom; }

    private void ToggleSmall()
    {
        if (Width <= LogicalToDeviceUnits(650)) Size = _normalSize;
        else { _normalSize = Size; Size = LogicalToDeviceUnits(new Size(600, 410)); }
        FitWindowToDesktop();
    }

    private async Task WatchOwnerAsync()
    {
        try { using var process = Process.GetProcessById(_options.OwnerPid); await process.WaitForExitAsync(); }
        catch (ArgumentException) { }
        OnUi(() => _ = ShutdownAsync());
    }

    private async Task ReadCommandsAsync()
    {
        try
        {
            while (!_closing)
            {
                var line = await Console.In.ReadLineAsync();
                if (line is null) { OnUi(() => _ = ShutdownAsync()); return; }
                if (line == "show") OnUi(() => { Show(); WindowState = FormWindowState.Normal; Activate(); });
                else if (line == "shutdown") OnUi(() => _ = ShutdownAsync());
            }
        }
        catch (IOException) { OnUi(() => _ = ShutdownAsync()); }
    }

    private async Task ShutdownAsync()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        ResetMouse();
        SaveSettings();
        try
        {
            // Never log off an unrelated session, even if another app replaced ours.
            var session = ChildSessionNativeMethods.TryGetChildSessionId();
            _rdp.DisconnectSession();
            if (_ownedSession.HasValue && session == _ownedSession)
                await Task.Run(() => ChildSessionNativeMethods.LogoffOwnedSession(_ownedSession.Value));
            _ownedSession = null;
            Close();
        }
        catch (Exception exception)
        {
            _closing = false;
            Show();
            SetStatus("分身注销失败：" + exception.GetBaseException().Message + "。请重试或在任务管理器的用户页注销分身会话。", true);
            _timer.Start();
        }
    }

    private void LoadSettings()
    {
        if (!_persistSettings) return;
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var settings = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(_settingsPath));
            if (settings is null) return;
            Size = new Size(Math.Clamp(settings.Width, 500, 3840), Math.Clamp(settings.Height, 340, 2160));
            _pin.Checked = settings.Topmost;
            _adaptive.Checked = settings.Adaptive;
            _aspect.Checked = settings.Aspect;
            _mute.Checked = settings.Muted;
            _shortcuts.Checked = settings.Shortcuts;
            _gameMouse.Checked = settings.GameMouse;
            // The persisted default (true) may not trigger CheckedChanged.
            _rdp.SetSmartSizing(_adaptive.Checked);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        { Program.Report("status", "分身窗口设置无法读取，使用默认值。"); }
    }

    private void SaveSettings()
    {
        if (!_persistSettings) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var size = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new WindowSettings(size.Width, size.Height,
                _pin.Checked, _adaptive.Checked, _aspect.Checked, _mute.Checked, _shortcuts.Checked, _gameMouse.Checked)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Program.Report("status", "分身窗口设置保存失败。"); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _pipe.Dispose(); _mouse.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record WindowSettings(int Width, int Height, bool Topmost, bool Adaptive, bool Aspect,
        bool Muted, bool Shortcuts, bool GameMouse);
}
