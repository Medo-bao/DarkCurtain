using ScreenBlank;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static partial class Tests
{
    private const long MiB = 1048576;
    private static readonly BindingFlags MemoryFields = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void VerifyMemoryCleanup()
    {
        MemoryCase(VerifyNativeMemoryOperation, "Native memory status and a real child working-set trim preserve the child data");
        MemoryCase(VerifyMemorySelectionPersistence, "All seven manual cleanup choices and the four defaults survive settings serialization");
        MemoryCase(VerifyMemoryFailureReporting, "Partial failure, UAC cancellation and retry report actual results without inflated freed memory");
        MemoryCase(VerifyMemoryConcurrency, "Overlapping cleanup requests execute once and leave the UI responsive");
        MemoryCase(VerifyMemoryEntryPoints, "Main-window and tray commands invoke the same saved cleanup selection");
        MemoryCase(VerifyMemorySettingsUi, "Settings expose seven manual choices and safely finish cleanup after the settings window closes");
        MemoryCase(VerifyExitDuringMemoryCleanup, "Exiting cancels pending cleanup without delaying exit or breaking repeated disposal");
        MemoryCase(VerifyMemoryHelperTransport, "One-shot helper IPC returns an explicit failure and detects a helper that exits before connection");
    }

    private static void MemoryCase(Action verify, string name)
    {
        verify();
        Check(true, name);
    }

    private static void MemoryRequire(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private static void PumpMemoryUntil(Func<bool> complete)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!complete() && timeout.ElapsedMilliseconds < 10000)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
        MemoryRequire(complete(), "Memory cleanup did not finish within ten seconds");
    }

    private static MemoryCleanupResult SuccessfulMemoryResult(MemoryCleanItems items)
        => new(new(16UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024),
            new(16UL * 1024 * 1024 * 1024, 2UL * 1024 * 1024 * 1024 + 3UL * 1048576),
            MemoryCleaner.Options.Where(option => items.HasFlag(option.Item))
                .Select(option => new MemoryCleanStep(option.Item, option.Name, true, "已完成")).ToArray(),
            Processed: 2, WorkingSetBytes: 500 * MiB);

    private static void RunMemoryProbe()
    {
        byte[] pages = new byte[64 * MiB];
        for (int offset = 0; offset < pages.Length; offset += 4096) pages[offset] = 123;
        Console.WriteLine("ready");
        Console.Out.Flush();
        if (Console.ReadLine() == "check")
        {
            bool intact = true;
            for (int offset = 0; offset < pages.Length; offset += 4096) intact &= pages[offset] == 123;
            Console.WriteLine(intact ? "intact" : "damaged");
            Console.Out.Flush();
        }
        GC.KeepAlive(pages);
    }

    private static void VerifyNativeMemoryOperation()
    {
        MemorySnapshot status = MemoryCleanupNative.ReadStatus();
        MemoryRequire(status.TotalBytes > 0 && status.AvailableBytes <= status.TotalBytes, "Invalid live physical memory status");
        ProcessStartInfo start = new(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--memory-probe");
        using Process child = Process.Start(start)!;
        try
        {
            Task<string?> ready = child.StandardOutput.ReadLineAsync();
            MemoryRequire(ready.Wait(10000) && ready.Result == "ready", "Memory probe was not ready");
            using var handle = MemoryCleanupNative.OpenForTrimming(child.Id);
            MemoryRequire(!handle.IsInvalid && MemoryCleanupNative.TrimProcess(handle, out long bytes) && bytes > 0,
                "Native working-set trimming did not reduce the probe working set");
            child.StandardInput.WriteLine("check");
            child.StandardInput.Flush();
            Task<string?> intact = child.StandardOutput.ReadLineAsync();
            MemoryRequire(intact.Wait(10000) && intact.Result == "intact", "Working-set trimming damaged child allocations");
            MemoryRequire(child.WaitForExit(10000) && child.ExitCode == 0, "Memory probe did not exit cleanly");
        }
        finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(); } }

        MemoryRequire(MemoryCleanupNative.ShouldSkipProcess(1000000, "DWM.EXE", 999999)
            && MemoryCleanupNative.ShouldSkipProcess(1000000, "VALORANT-Win64-Shipping.exe", 999999)
            && MemoryCleanupNative.ShouldSkipProcess(1000000, "inZOI.exe", 999999)
            && MemoryCleanupNative.ShouldSkipProcess(Environment.ProcessId, "anything.exe", 999999)
            && !MemoryCleanupNative.ShouldSkipProcess(1000000, "ordinary-app.exe", 999999),
            "System, own-process or game exclusions were not applied");
        if (!MemoryCleaner.IsAdministrator())
        {
            MemoryCleanupResult denied = MemoryCleanupNative.Clean(new(MemoryCleanItems.All & ~MemoryCleanItems.WorkingSet, Environment.ProcessId));
            MemoryRequire(!denied.Success && !denied.Partial && denied.Steps.Length == 6
                && denied.Steps.All(step => !step.Success), "Non-admin cache cleanup incorrectly reported success");
        }
    }

    private static void VerifyMemorySelectionPersistence()
    {
        MemoryRequire(MemoryCleaner.Options.Length == 7 && new Settings().MemoryCleanupItems == MemoryCleanItems.Default,
            "Manual cleanup defaults changed");
        Settings settings = new() { MemoryCleanupItems = MemoryCleanItems.Modified | MemoryCleanItems.Registry | MemoryCleanItems.Combined };
        Settings restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        MemoryRequire(restored.MemoryCleanupItems == settings.MemoryCleanupItems, "Saved cleanup choices were lost");
        MemoryRequire(JsonSerializer.Deserialize<Settings>("{\"MonitorPowerOff\":true}")!.MemoryCleanupItems == MemoryCleanItems.Default,
            "Existing settings did not receive the default manual cleanup selection");
        MemoryRequire(!MemoryCleaner.ValidItems(MemoryCleanItems.None)
            && !MemoryCleaner.ValidItems((MemoryCleanItems)128)
            && !MemoryCleaner.RequiresAdministrator(MemoryCleanItems.WorkingSet)
            && MemoryCleaner.RequiresAdministrator(MemoryCleanItems.FileCache), "Invalid cleanup selection or permission routing");
    }

    private static void VerifyMemoryFailureReporting()
    {
        MemoryCleanupResult partial = SuccessfulMemoryResult(MemoryCleanItems.Default) with
        {
            Steps = [new(MemoryCleanItems.FileCache, "系统文件缓存", false, "权限不足"),
                new(MemoryCleanItems.WorkingSet, "进程工作集", true, "整理 2 个进程")]
        };
        MemoryCleaner cleaner = new((_, _) => Task.FromResult(partial));
        Task<MemoryCleanupResult> clean = cleaner.CleanAsync(MemoryCleanItems.Default);
        PumpMemoryUntil(() => clean.IsCompleted);
        MemoryRequire(clean.Result.Partial && !clean.Result.Success && clean.Result.AvailableChange == 3 * MiB
            && clean.Result.Message.Contains("部分完成") && !clean.Result.Message.Contains("500"),
            "Cleanup failures or working-set totals were presented as physical memory freed");
        int requests = 0;
        MemoryCleaner retry = new((request, _) => ++requests == 1
            ? throw new Win32Exception(1223)
            : Task.FromResult(SuccessfulMemoryResult(request.Items)));
        Task<MemoryCleanupResult> cancelled = retry.CleanAsync(MemoryCleanItems.Default);
        PumpMemoryUntil(() => cancelled.IsCompleted);
        MemoryRequire(cancelled.Result.Cancelled && cancelled.Result.Message.Contains("管理员授权") && !retry.IsRunning,
            "Denied UAC left cleanup running or reported success");
        Task<MemoryCleanupResult> repeated = retry.CleanAsync(MemoryCleanItems.WorkingSet);
        PumpMemoryUntil(() => repeated.IsCompleted);
        MemoryRequire(repeated.Result.Success && requests == 2, "Cleanup could not retry after authorization was cancelled");
    }

    private static void VerifyMemoryConcurrency()
    {
        int requests = 0;
        TaskCompletionSource<MemoryCleanupResult> pending = new();
        MemoryCleaner cleaner = new((_, _) => { requests++; return pending.Task; });
        Task<MemoryCleanupResult> first = cleaner.CleanAsync(MemoryCleanItems.Default);
        Task<MemoryCleanupResult> duplicate = cleaner.CleanAsync(MemoryCleanItems.WorkingSet);
        bool posted = false;
        using Form messages = new();
        messages.Show();
        messages.BeginInvoke((Action)(() => posted = true));
        PumpMemoryUntil(() => posted);
        MemoryRequire(cleaner.IsRunning && !first.IsCompleted && duplicate.IsCompleted
            && duplicate.Result.Error is not null && requests == 1, "Overlapping requests ran twice or blocked the UI");
        pending.SetResult(SuccessfulMemoryResult(MemoryCleanItems.Default));
        PumpMemoryUntil(() => first.IsCompleted);
        MemoryRequire(first.Result.Success && !cleaner.IsRunning, "Cleanup stayed busy after completion");
        messages.Close();
    }

    private static void VerifyMemoryEntryPoints()
    {
        List<MemoryCleanRequest> requests = [];
        MemoryCleaner cleaner = new((request, _) => { requests.Add(request); return Task.FromResult(SuccessfulMemoryResult(request.Items)); });
        Settings settings = new() { CloseExits = true, MemoryCleanupItems = MemoryCleanItems.WorkingSet };
        using MainForm main = new(settings, memoryCleaner: cleaner);
        main.Show();
        Application.DoEvents();
        Button memoryIcon = Descendants(main).OfType<Button>().Single(button => button.AccessibleName == "清理内存");
        Button settingsIcon = Descendants(main).OfType<Button>().Single(button => button.AccessibleName == "设置");
        Rectangle memoryBounds = main.RectangleToClient(memoryIcon.RectangleToScreen(memoryIcon.ClientRectangle));
        Rectangle settingsBounds = main.RectangleToClient(settingsIcon.RectangleToScreen(settingsIcon.ClientRectangle));
        MemoryRequire(memoryBounds.Right < settingsBounds.Left && memoryIcon.Size == settingsIcon.Size
            && memoryIcon.FlatAppearance.BorderSize == 0 && memoryIcon.BackColor == settingsIcon.BackColor
            && memoryIcon.ForeColor == settingsIcon.ForeColor, "Cleanup icon did not match the settings icon or sit to its left");
        Label memoryStatus = (Label)typeof(MainForm).GetField("_memoryStatus", MemoryFields)!.GetValue(main)!;
        Rectangle statusBounds = main.RectangleToClient(memoryStatus.RectangleToScreen(memoryStatus.ClientRectangle));
        MemoryRequire(main.ClientSize.Height - statusBounds.Bottom <= 28, "Main window retained excess blank space below its status");
        memoryIcon.PerformClick();
        var tray = (NotifyIcon)typeof(MainForm).GetField("_trayIcon", MemoryFields)!.GetValue(main)!;
        ToolStripMenuItem command = tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "清理内存");
        main.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        command.PerformClick();
        MemoryRequire(requests.Count == 2 && requests.All(request => request.Items == settings.MemoryCleanupItems
            && request.CallerProcessId == Environment.ProcessId) && !main.Visible,
            "The main-window and tray cleanup routes diverged or tray cleanup reopened the window");
        main.ShowMainWindow();
        Capture(main, "main-memory-cleanup");
        main.Close();
        PumpMemoryUntil(() => main.IsDisposed);
    }

    private static void VerifyMemorySettingsUi()
    {
        TaskCompletionSource<MemoryCleanupResult> pending = new();
        MemoryCleanItems requested = MemoryCleanItems.None;
        MemoryCleaner cleaner = new((request, _) => { requested = request.Items; return pending.Task; });
        using SettingsForm form = new(new Settings(), new ShutdownSchedule(() => Task.CompletedTask), cleaner);
        form.Show();
        Application.DoEvents();
        CheckBox[] options = MemoryCleaner.Options.Select(option => Find<CheckBox>(form, option.Name)).ToArray();
        foreach (CheckBox option in options) option.Checked = false;
        Button clean = Find<Button>(form, "清理所选内存");
        MemoryRequire(!clean.Enabled && form.SelectedMemoryItems == MemoryCleanItems.None, "Empty cleanup selection remained executable");
        foreach (CheckBox option in options) option.Checked = true;
        MemoryRequire(form.SelectedMemoryItems == MemoryCleanItems.All && clean.Enabled, "Manual cleanup options could not all be selected");
        var layout = (TableLayoutPanel)clean.Parent!.Parent!;
        Panel viewport = (Panel)layout.Parent!;
        Label notice = Descendants(form).OfType<Label>().Single(label => label.Name == "MemoryCleanupNotice");
        clean.Focus();
        viewport.ScrollControlIntoView(notice);
        Application.DoEvents();
        Rectangle noticeBounds = viewport.RectangleToClient(notice.RectangleToScreen(notice.ClientRectangle));
        MemoryRequire(notice.Height >= notice.GetPreferredSize(new Size(notice.Width, 0)).Height
            && viewport.ClientRectangle.Contains(noticeBounds) && !viewport.HorizontalScroll.Visible,
            "Memory cleanup introduction was clipped or could not be fully scrolled into view");
        Capture(form, "settings-memory-cleanup");
        clean.PerformClick();
        MemoryRequire(requested == MemoryCleanItems.All && !clean.Enabled && options.All(option => !option.Enabled)
            && Find<Button>(form, "开始计时").Enabled, "Cleanup did not lock its own controls or interfered with shutdown settings");
        form.Close();
        MemoryRequire(cleaner.IsRunning, "Closing the settings window cancelled the one-shot cleanup");
        pending.SetResult(SuccessfulMemoryResult(MemoryCleanItems.All));
        PumpMemoryUntil(() => !cleaner.IsRunning);
    }

    private static void VerifyExitDuringMemoryCleanup()
    {
        MemoryCleaner cleaner = new(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return SuccessfulMemoryResult(MemoryCleanItems.Default);
        });
        MainForm main = new(new Settings { CloseExits = true }, memoryCleaner: cleaner);
        main.Show();
        Descendants(main).OfType<Button>().Single(button => button.AccessibleName == "清理内存").PerformClick();
        MemoryRequire(cleaner.IsRunning, "Pending cleanup was not started");
        main.Close();
        PumpMemoryUntil(() => main.IsDisposed && !cleaner.IsRunning);
        MemoryRequire(cleaner.LastResult?.Cancelled == true, "App exit did not cancel its pending cleanup");
        main.Dispose();
        main.Dispose();
    }

    private static void VerifyMemoryHelperTransport()
    {
        Task<MemoryCleanupResult> request = MemoryCleanupProcess.RunAsync(new(MemoryCleanItems.None, Environment.ProcessId),
            startProcess: start =>
            {
                MemoryRequire(start.UseShellExecute && start.Verb == "runas"
                    && start.WindowStyle == ProcessWindowStyle.Hidden && start.ArgumentList[0] == MemoryCleanupProcess.Argument,
                    "One-shot helper did not request hidden administrator execution");
                start.UseShellExecute = false;
                start.Verb = "";
                start.CreateNoWindow = true;
                return Process.Start(start);
            });
        PumpMemoryUntil(() => request.IsCompleted);
        MemoryCleanupResult result = request.GetAwaiter().GetResult();
        MemoryRequire(!result.Success && result.Error?.Contains("无效") == true,
            "Helper IPC lost a failed result or executed an invalid cleanup request");
        Task<MemoryCleanupResult> missing = MemoryCleanupProcess.RunAsync(new(MemoryCleanItems.None, Environment.ProcessId),
            startProcess: start =>
            {
                start.UseShellExecute = false;
                start.Verb = "";
                start.CreateNoWindow = true;
                start.ArgumentList.Clear();
                start.ArgumentList.Add("--memory-helper-exit");
                return Process.Start(start);
            });
        PumpMemoryUntil(() => missing.IsCompleted);
        bool failed = false;
        try { missing.GetAwaiter().GetResult(); }
        catch (InvalidOperationException error) { failed = error.Message.Contains("连接前"); }
        MemoryRequire(failed, "A helper that exits before connection caused an indefinite wait");
    }
}
