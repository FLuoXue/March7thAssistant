using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace March7th.Desktop;

internal sealed class MousePipe : IDisposable
{
    internal const int MaximumFrame = 65536;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<uint?> _targetSession;
    private Channel<byte[]>? _outgoing;
    public string Name { get; } = "March7th.Desktop." + Guid.NewGuid().ToString("N");
    public bool Connected => Volatile.Read(ref _outgoing) is not null;
    public event Action? ConnectionChanged;
    public event Action<int, long, bool>? Acknowledged;

    public MousePipe(Func<uint?> targetSession)
    {
        _targetSession = targetSession;
        _ = ServeAsync();
    }

    public bool Send(object value)
    {
        var channel = Volatile.Read(ref _outgoing);
        return channel is not null && channel.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var hello = await ReadAsync(server, handshakeTimeout.Token);
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var pid))
                    throw new IOException("无法验证分身助手进程。");
                using var process = Process.GetProcessById(checked((int)pid));
                var session = _targetSession();
                var root = hello.RootElement;
                if (session is null || process.SessionId != session ||
                    root.GetProperty("op").GetString() != "hello" || root.GetProperty("version").GetInt32() != 1 ||
                    root.GetProperty("pid").GetInt32() != pid)
                    throw new IOException("分身助手身份或协议版本不匹配。");
                await WriteAsync(server, JsonSerializer.SerializeToUtf8Bytes(new { op = "hello", version = 1 }), _stop.Token);
                var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
                {
                    FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
                });
                Volatile.Write(ref _outgoing, channel);
                ConnectionChanged?.Invoke();
                var writer = WriteLoopAsync(server, channel.Reader, connectionStop.Token);
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var document = await ReadAsync(server, connectionStop.Token);
                        var message = document.RootElement;
                        if (message.GetProperty("op").GetString() != "ack")
                            throw new IOException("无法识别的鼠标响应。");
                        Acknowledged?.Invoke(message.GetProperty("epoch").GetInt32(),
                            message.GetProperty("seq").GetInt64(), message.GetProperty("handled").GetBoolean());
                    }
                }
                finally
                {
                    channel.Writer.TryComplete();
                    connectionStop.Cancel();
                    server.Dispose();
                    try { await writer; } catch (Exception) when (connectionStop.IsCancellationRequested) { }
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException
                or InvalidOperationException or JsonException or ArgumentException or KeyNotFoundException or FormatException or OverflowException)
            {
                if (!_stop.IsCancellationRequested) Program.Report("status", "分身助手连接已断开，等待重新连接。");
            }
            finally
            {
                Volatile.Write(ref _outgoing, null);
                ConnectionChanged?.Invoke();
            }
            if (!_stop.IsCancellationRequested)
            {
                try { await Task.Delay(200, _stop.Token); } catch (OperationCanceledException) { }
            }
        }
    }

    private static async Task WriteLoopAsync(Stream stream, ChannelReader<byte[]> reader, CancellationToken token)
    {
        try
        {
            await foreach (var bytes in reader.ReadAllAsync(token)) await WriteAsync(stream, bytes, token);
        }
        catch
        {
            stream.Dispose(); // Unblock the reader too when the outgoing side fails.
            throw;
        }
    }

    internal static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > MaximumFrame) throw new IOException("鼠标消息长度超出限制。");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token);
        return JsonDocument.Parse(payload);
    }

    internal static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        if (payload.Length == 0 || payload.Length > MaximumFrame) throw new IOException("鼠标消息长度超出限制。");
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
    }

    public void Dispose() => _stop.Cancel();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
