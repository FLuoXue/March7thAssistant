using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace March7th.Desktop;

internal static class SelfTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static JsonElement Packet(object value) => JsonSerializer.SerializeToElement(value);

    public static int Run()
    {
        var flow = new MouseFlow();
        Check(flow.Take(1000) is null, "Disabled capture sent input");
        flow.Enable(true);
        flow.Add(10, 0);
        flow.Add(-10, 0);
        var first = Packet(flow.Take(1000)!);
        Check(first.GetProperty("samples").GetArrayLength() == 2, "Direction reversal was lost");
        Check(!flow.Confirmed, "Cursor captured without acknowledgement");
        Check(flow.Take(1100) is null, "More than one outstanding batch");
        flow.Acknowledge(first.GetProperty("epoch").GetInt32(), first.GetProperty("seq").GetInt64(), true);
        Check(flow.Confirmed, "Successful movement did not confirm capture");
        var pending = Packet(flow.Take(1120)!);
        flow.Enable(false);
        flow.Enable(true);
        flow.Acknowledge(pending.GetProperty("epoch").GetInt32(), pending.GetProperty("seq").GetInt64(), true);
        Check(!flow.Confirmed, "Old acknowledgement recaptured after Alt/focus change");
        flow.Add(20, 2);
        var stale = Packet(flow.Take(1200)!);
        flow.Add(100, 0);
        var afterTimeout = Packet(flow.Take(1500)!);
        Check(afterTimeout.GetProperty("samples").GetArrayLength() == 0, "Stalled input replayed stale movement");
        flow.Acknowledge(stale.GetProperty("epoch").GetInt32(), stale.GetProperty("seq").GetInt64(), true);
        Check(!flow.Confirmed, "Timed-out acknowledgement recaptured cursor");
        flow.Acknowledge(afterTimeout.GetProperty("epoch").GetInt32(), afterTimeout.GetProperty("seq").GetInt64(), true);
        Check(!flow.Confirmed, "An empty poll locked cursor before real movement");
        flow.Add(30, 3);
        var rejected = Packet(flow.Take(1520)!);
        flow.Acknowledge(rejected.GetProperty("epoch").GetInt32(), rejected.GetProperty("seq").GetInt64(), false);
        Check(!flow.Confirmed, "Inactive game kept cursor locked");
        for (var i = 0; i < 2000; i++) flow.Add(i % 2 == 0 ? 1 : -1, 0);
        var bounded = Packet(flow.Take(1540)!);
        Check(bounded.GetProperty("samples").GetArrayLength() <= 64, "Unbounded mouse queue");
        using var memory = new MemoryStream();
        MousePipe.WriteAsync(memory, JsonSerializer.SerializeToUtf8Bytes(new { op = "hello", version = 1 }), CancellationToken.None).GetAwaiter().GetResult();
        memory.Position = 0;
        using var frame = MousePipe.ReadAsync(memory, CancellationToken.None).GetAwaiter().GetResult();
        Check(frame.RootElement.GetProperty("version").GetInt32() == 1, "Frame round trip failed");
        var invalidHeader = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(invalidHeader, MousePipe.MaximumFrame + 1);
        try
        {
            MousePipe.ReadAsync(new MemoryStream(invalidHeader), CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("Oversized frame accepted");
        }
        catch (IOException) { }
        Program.Report("test", "Mouse state, stale ACK, timeout, direction changes, queue bounds and frame tests passed.");
        return 0;
    }

    public static int Smoke()
    {
        using var window = new Form { ShowInTaskbar = false };
        using var rdp = new RdpActiveXHost();
        ((ISupportInitialize)rdp).BeginInit();
        window.Controls.Add(rdp);
        ((ISupportInitialize)rdp).EndInit();
        rdp.Probe();
        using var controls = new DesktopWindow(new LaunchOptions(Environment.ProcessId, Environment.ProcessPath!, "",
            Path.GetTempPath()), persistSettings: false);
        _ = controls.Handle;
        Program.Report("test", "RDP ActiveX instantiated; ConnectToChildSession COM property accepted; no connection started.");
        return 0;
    }

    public static int Lifecycle()
    {
        using var window = new DesktopWindow(new LaunchOptions(Environment.ProcessId, Environment.ProcessPath!, "",
            Path.GetTempPath()), persistSettings: false);
        _ = window.Handle;
        window.FormClosed += (_, _) => Application.ExitThread();
        window.StartMonitoring();
        Application.Run(); // Hidden, no RDP Connect; exercise the actual stdin shutdown path.
        Program.Report("test", "Desktop host graceful shutdown passed without creating a session.");
        return 0;
    }

    public static async Task<int> PipeAsync()
    {
        // Test transport only. No input is captured or simulated in this mode.
        using var pipe = new MousePipe(() => (uint)Process.GetCurrentProcess().SessionId);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipe.ConnectionChanged += () => { if (pipe.Connected) ready.TrySetResult(true); };
        pipe.Acknowledged += (epoch, sequence, handled) => done.TrySetResult(epoch == 7 && sequence == 42 && handled);
        Program.Report("pipe", pipe.Name);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(pipe.Send(new { op = "mouse", epoch = 7, seq = 42,
            sent_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), samples = new[] { new[] { 7, -4 } } }), "Pipe write failed");
        Check(await done.Task.WaitAsync(TimeSpan.FromSeconds(10)), "Python acknowledgement did not match");
        Program.Report("test", "Native/Python named-pipe handshake and mouse ACK passed.");
        return 0;
    }
}
