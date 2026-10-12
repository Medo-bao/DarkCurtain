using System.ComponentModel;
using System.Security.Principal;

namespace ScreenBlank;

[Flags]
internal enum MemoryCleanItems
{
    None = 0,
    Standby = 1,
    FileCache = 2,
    LowPriorityStandby = 4,
    WorkingSet = 8,
    Modified = 16,
    Registry = 32,
    Combined = 64,
    Default = Standby | FileCache | LowPriorityStandby | WorkingSet,
    All = Default | Modified | Registry | Combined
}

internal sealed record MemoryCleanOption(MemoryCleanItems Item, string Name);
internal sealed record MemorySnapshot(ulong TotalBytes, ulong AvailableBytes)
{
    public ulong UsedBytes => TotalBytes >= AvailableBytes ? TotalBytes - AvailableBytes : 0;
    public int UsedPercent => TotalBytes == 0 ? 0 : (int)(100.0 * UsedBytes / TotalBytes);
}

internal sealed record MemoryCleanRequest(MemoryCleanItems Items, int CallerProcessId);
internal sealed record MemoryCleanStep(MemoryCleanItems Item, string Name, bool Success, string Detail);

internal sealed record MemoryCleanupResult(
    MemorySnapshot? Before, MemorySnapshot? After, MemoryCleanStep[] Steps,
    int Processed = 0, int Skipped = 0, int Failed = 0, long WorkingSetBytes = 0,
    string? Error = null, bool Cancelled = false)
{
    public bool Success => !Cancelled && Error is null && Steps.Length > 0 && Steps.All(step => step.Success);
    public bool Partial => !Cancelled && (Steps.Any(step => step.Success) || Processed > 0) && !Success;
    public long? AvailableChange => Before is null || After is null
        ? null : (long)After.AvailableBytes - (long)Before.AvailableBytes;
    public string Message
    {
        get
        {
            if (Cancelled) return Error ?? "内存清理已取消";
            if (Error is not null) return Error;
            string state = Success ? "内存清理完成" : Partial ? "内存清理部分完成" : "内存清理失败";
            string change = AvailableChange is long bytes
                ? $"；可用内存变化 {bytes / 1048576.0:+0.0;-0.0;0.0} MiB" : "";
            return state + change;
        }
    }
    public string Detail => string.Join(Environment.NewLine,
        Steps.Select(step => $"{step.Name}：{step.Detail}"))
        + (Processed + Skipped + Failed > 0
            ? $"\n进程：整理 {Processed}，跳过 {Skipped}，失败 {Failed}；工作集减少 {WorkingSetBytes / 1048576.0:0.0} MiB"
            : "");
}

internal sealed class MemoryCleaner
{
    internal static readonly MemoryCleanOption[] Options =
    [
        new(MemoryCleanItems.Standby, "待机列表"),
        new(MemoryCleanItems.FileCache, "系统文件缓存"),
        new(MemoryCleanItems.LowPriorityStandby, "低优先级待机列表"),
        new(MemoryCleanItems.WorkingSet, "进程工作集"),
        new(MemoryCleanItems.Modified, "修改页列表"),
        new(MemoryCleanItems.Registry, "注册表缓存"),
        new(MemoryCleanItems.Combined, "内存合并")
    ];

    private readonly Func<MemoryCleanRequest, CancellationToken, Task<MemoryCleanupResult>> _execute;
    private int _running;
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public MemoryCleanupResult? LastResult { get; private set; }
    public event Action? Changed;

    public MemoryCleaner(Func<MemoryCleanRequest, CancellationToken, Task<MemoryCleanupResult>>? execute = null)
    {
        _execute = execute ?? ExecuteAsync;
    }

    public static bool ValidItems(MemoryCleanItems items) => items != MemoryCleanItems.None
        && (items & ~MemoryCleanItems.All) == 0;

    public static bool RequiresAdministrator(MemoryCleanItems items) => (items & ~MemoryCleanItems.WorkingSet) != 0;

    public static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public async Task<MemoryCleanupResult> CleanAsync(MemoryCleanItems items, CancellationToken cancellationToken = default)
    {
        if (!ValidItems(items)) return new(null, null, [], Error: "请至少选择一个有效的内存清理项");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return new(null, null, [], Error: "内存清理正在进行");

        try
        {
            Changed?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            LastResult = await _execute(new(items, Environment.ProcessId), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LastResult = new(null, null, [], Cancelled: true);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            LastResult = new(null, null, [], Error: "已取消管理员授权，未执行清理", Cancelled: true);
        }
        catch (Exception error)
        {
            LastResult = new(null, null, [], Error: $"内存清理失败：{error.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            Changed?.Invoke();
        }
        return LastResult;
    }

    private static Task<MemoryCleanupResult> ExecuteAsync(MemoryCleanRequest request, CancellationToken cancellationToken)
    {
        return RequiresAdministrator(request.Items) && !IsAdministrator()
            ? MemoryCleanupProcess.RunAsync(request, cancellationToken)
            : Task.Run(() => MemoryCleanupNative.Clean(request, cancellationToken), cancellationToken);
    }
}
