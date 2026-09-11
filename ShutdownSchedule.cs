using System.Diagnostics;

namespace ScreenBlank;

internal sealed class ShutdownSchedule
{
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Task> _shutdown;
    public event Action? Changed;
    public DateTimeOffset? DueAt { get; private set; }
    public bool IsExecuting { get; private set; }
    public string? Error { get; private set; }

    public ShutdownSchedule(Func<Task> shutdown, Func<DateTimeOffset>? now = null)
    {
        _shutdown = shutdown;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void Schedule(int minutes)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (IsExecuting) return;
        DueAt = _now().AddMinutes(minutes);
        Error = null;
        Changed?.Invoke();
    }

    public void Cancel()
    {
        DueAt = null;
        Error = null;
        Changed?.Invoke();
    }

    public string Status
    {
        get
        {
            if (IsExecuting) return "正在请求关机";
            if (Error is not null) return "关机失败：" + Error;
            if (DueAt is not { } due) return "未设置定时关机";
            TimeSpan left = due - _now();
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"{due.LocalDateTime:MM-dd HH:mm} 关机 · 剩余 {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
        }
    }

    public async Task TickAsync()
    {
        if (IsExecuting) return;
        if (DueAt is not { } due || _now() < due)
        {
            Changed?.Invoke();
            return;
        }

        // Consume the plan before awaiting so message-loop reentry cannot submit it twice.
        DueAt = null;
        IsExecuting = true;
        Changed?.Invoke();
        try { await _shutdown(); }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsExecuting = false;
            Changed?.Invoke();
        }
    }

    internal static ProcessStartInfo CreateShutdownCommand() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"))
    {
        Arguments = "/s /t 0",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };

    public static async Task RequestShutdownAsync()
    {
        using Process process = Process.Start(CreateShutdownCommand())
            ?? throw new InvalidOperationException("无法启动系统关机程序");
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        string detail = (await error).Trim();
        await output;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(detail.Length == 0 ? $"系统返回错误 {process.ExitCode}" : detail);
    }
}
