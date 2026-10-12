using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace ScreenBlank;

internal static class MemoryCleanupProcess
{
    internal const string Argument = "--clean-memory";
    private const string PipePrefix = "ScreenBlank_Memory_";

    internal static async Task<MemoryCleanupResult> RunAsync(MemoryCleanRequest request,
        CancellationToken cancellationToken = default, Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        ProcessStartInfo start = new(Environment.ProcessPath ?? Application.ExecutablePath)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(pipeName);
        // ShellExecute can wait for UAC; keep it off the WinForms thread.
        using Process process = await Task.Run(() => (startProcess ?? Process.Start)(start), cancellationToken)
            ?? throw new InvalidOperationException("无法启动内存清理任务");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            Task connection = pipe.WaitForConnectionAsync(timeout.Token);
            Task exited = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connection, exited) == exited && !pipe.IsConnected)
                throw new InvalidOperationException("内存清理任务在连接前已退出");
            await connection;
            using StreamReader reader = new(pipe, leaveOpen: true);
            using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            string response = await reader.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException("内存清理任务没有返回结果");
            MemoryCleanupResult result = JsonSerializer.Deserialize<MemoryCleanupResult>(response)
                ?? throw new InvalidOperationException("内存清理结果无效");
            await process.WaitForExitAsync(timeout.Token);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("内存清理任务等待超时");
        }
    }

    internal static int RunHelper(string[] args,
        Func<MemoryCleanRequest, CancellationToken, MemoryCleanupResult>? clean = null)
        => RunHelperAsync(args, clean).GetAwaiter().GetResult();

    private static async Task<int> RunHelperAsync(string[] args,
        Func<MemoryCleanRequest, CancellationToken, MemoryCleanupResult>? clean)
    {
        if (args.Length != 2 || args[0] != Argument || !args[1].StartsWith(PipePrefix, StringComparison.Ordinal)
            || args[1].Length != PipePrefix.Length + 32) return 1;
        try
        {
            using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(45));
            using NamedPipeClientStream pipe = new(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10000, lifetime.Token);
            using StreamReader reader = new(pipe, leaveOpen: true);
            using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
            MemoryCleanRequest? request = JsonSerializer.Deserialize<MemoryCleanRequest>(
                await reader.ReadLineAsync(lifetime.Token) ?? "null");
            // Closing the parent's pipe cancels the remaining cleanup operations, even across UAC.
            Task callerDisconnected = WatchCallerAsync(reader, lifetime);
            try
            {
                MemoryCleanupResult result;
                try
                {
                    result = request is null ? new(null, null, [], Error: "内存清理请求无效")
                        : await Task.Run(() => (clean ?? MemoryCleanupNative.Clean)(request, lifetime.Token), lifetime.Token);
                }
                catch (OperationCanceledException) { result = new(null, null, [], Cancelled: true); }
                catch (Exception error) { result = new(null, null, [], Error: error.Message); }
                if (!lifetime.IsCancellationRequested)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), lifetime.Token);
                return result.Success ? 0 : 1;
            }
            finally
            {
                lifetime.Cancel();
                await callerDisconnected;
            }
        }
        catch { return 1; }
    }

    private static async Task WatchCallerAsync(StreamReader reader, CancellationTokenSource lifetime)
    {
        try
        {
            await reader.ReadLineAsync(lifetime.Token);
            lifetime.Cancel();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IOException) { lifetime.Cancel(); }
    }
}
