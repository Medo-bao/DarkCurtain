using System.Diagnostics;

namespace ScreenBlank;

internal sealed class ShutdownSchedule
{
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<Task> _shutdown;
    private readonly Func<ProcessStartInfo, Task> _runCommand;
    public bool UsesWindowsCommand { get; private set; }
    public DateTimeOffset? WindowsDueAt => UsesWindowsCommand ? DueAt : null;
    public event Action? Changed;
    public DateTimeOffset? DueAt { get; private set; }
    public bool IsExecuting { get; private set; }
    public string? Error { get; private set; }

    public ShutdownSchedule(Func<Task> shutdown, Func<DateTimeOffset>? now = null,
        Func<ProcessStartInfo, Task>? runCommand = null, DateTimeOffset? windowsDueAt = null)
    {
        _shutdown = shutdown;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _runCommand = runCommand ?? RunShutdownCommandAsync;
        if (windowsDueAt > _now())
        {
            DueAt = windowsDueAt;
            UsesWindowsCommand = true;
        }
    }

    public void Schedule(int minutes)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (IsExecuting || UsesWindowsCommand) return;
        DueAt = _now().AddMinutes(minutes);
        Error = null;
        Changed?.Invoke();
    }

    public void Cancel()
    {
        // Application exit only cancels software-owned timers.
        if (UsesWindowsCommand) return;
        DueAt = null;
        Error = null;
        Changed?.Invoke();
    }

    public async Task ScheduleAsync(int minutes, bool useWindowsCommand)
    {
        if (minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
        if (IsExecuting) return;
        IsExecuting = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            if (UsesWindowsCommand)
            {
                await AbortWindowsAsync();
                DueAt = null;
                UsesWindowsCommand = false;
            }
            DateTimeOffset due = _now().AddMinutes(minutes);
            if (useWindowsCommand)
                await _runCommand(CreateShutdownCommand(checked(minutes * 60)));
            DueAt = due;
            UsesWindowsCommand = useWindowsCommand;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsExecuting = false; Changed?.Invoke(); }
    }

    public async Task CancelAsync()
    {
        if (IsExecuting) return;
        IsExecuting = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            if (UsesWindowsCommand) await AbortWindowsAsync();
            DueAt = null;
            UsesWindowsCommand = false;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsExecuting = false; Changed?.Invoke(); }
    }

    private async Task AbortWindowsAsync()
    {
        try { await _runCommand(CreateAbortCommand()); }
        catch (ShutdownCommandException ex) when (ex.ExitCode == 1116) { }
    }

    public string Status
    {
        get
        {
            if (IsExecuting) return "正在处理关机请求";
            if (Error is not null) return "操作失败：" + Error;
            if (DueAt is not { } due) return "未设置定时关机";
            TimeSpan left = due - _now();
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"{(UsesWindowsCommand ? "Windows" : "软件")} · {due.LocalDateTime:MM-dd HH:mm} 关机 · 剩余 {(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
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
        if (UsesWindowsCommand)
        {
            UsesWindowsCommand = false;
            Changed?.Invoke();
            return;
        }
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

    internal static ProcessStartInfo CreateShutdownCommand(int seconds = 0) => CreateCommand($"/s /t {seconds}");

    internal static ProcessStartInfo CreateAbortCommand() => CreateCommand("/a");

    private static ProcessStartInfo CreateCommand(string arguments) => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"))
    {
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true
    };

    public static Task RequestShutdownAsync() => RunShutdownCommandAsync(CreateShutdownCommand());

    internal static Task RunShutdownCommandAsync(ProcessStartInfo command) => Task.Run(async () =>
    {
        // Process creation can block too; keep it off the window's message loop.
        using Process process = Process.Start(command)
            ?? throw new InvalidOperationException("无法启动系统关机程序");
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        string detail = (await error).Trim();
        await output;
        if (process.ExitCode != 0)
            throw new ShutdownCommandException(process.ExitCode, detail.Length == 0 ? $"系统返回错误 {process.ExitCode}" : detail);
    });
}

internal sealed class ShutdownCommandException(int exitCode, string message) : InvalidOperationException(message)
{
    public int ExitCode { get; } = exitCode;
}
