using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace March7th.Desktop;

internal sealed record LaunchOptions(int OwnerPid, string Executable, string Arguments, string Directory,
    string Title = "March7th Assistant（桌面分身）")
{
    internal static LaunchOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--"))
                throw new ArgumentException("分身启动参数不完整。");
            values.Add(args[i], args[i + 1]);
        }
        var result = new LaunchOptions(int.Parse(values["--owner-pid"]),
            Path.GetFullPath(values["--launch-exe"]), values["--launch-args"],
            Path.GetFullPath(values["--working-directory"]),
            values.GetValueOrDefault("--title", "March7th Assistant（桌面分身）"));
        if (!File.Exists(result.Executable) || !System.IO.Directory.Exists(result.Directory))
            throw new FileNotFoundException("三月七助手的启动路径不存在。");
        using var owner = Process.GetProcessById(result.OwnerPid);
        if (owner.SessionId != Process.GetCurrentProcess().SessionId)
            throw new ArgumentException("分身管理器必须与主窗口处于同一会话。");
        return result;
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A WinExe has no console code page. Bind UTF-8 readers/writers directly
        // to inherited pipes instead of setting Console.OutputEncoding.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)));
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.SequenceEqual(new[] { "--self-test" }))
                return SelfTests.Run();
            if (args.SequenceEqual(new[] { "--smoke-test" }))
                return SelfTests.Smoke();
            if (args.SequenceEqual(new[] { "--pipe-test" }))
                return SelfTests.PipeAsync().GetAwaiter().GetResult();
            if (args.SequenceEqual(new[] { "--lifecycle-test" }))
                return SelfTests.Lifecycle();
            var options = LaunchOptions.Parse(args);
            using var identity = WindowsIdentity.GetCurrent();
            using var mutex = new Mutex(true, @"Global\March7th.Desktop." + identity.User!.Value, out var created);
            if (!created)
                throw new InvalidOperationException("已有三月七桌面分身管理器，请从原窗口打开。");
            try
            {
                using var window = new DesktopWindow(options);
                Application.Run(window);
            }
            finally { mutex.ReleaseMutex(); }
            return 0;
        }
        catch (Exception exception)
        {
            Report("error", exception.GetBaseException().Message);
            return 1;
        }
    }

    internal static void Report(string type, string message)
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(new { type, message }));
            Console.Out.Flush();
        }
        catch (IOException) { }
    }
}

internal sealed class ChildSessionConnectionFailedEventArgs(
    string message, int errorCode, int? extendedErrorCode = null) : EventArgs
{
    public string Message { get; } = message;
    public int ErrorCode { get; } = errorCode;
    public int? ExtendedErrorCode { get; } = extendedErrorCode;
}
