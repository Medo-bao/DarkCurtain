using ScreenBlank;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Tests
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--verify-release")
        {
            VerifyRelease(args[1]);
            return;
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int calls = 0;
        ShutdownSchedule schedule = new(() => { calls++; return Task.CompletedTask; }, () => now);
        schedule.Schedule(1);
        schedule.TickAsync().GetAwaiter().GetResult();
        Check(calls == 0, "Does not run early");
        schedule.Cancel();
        now = now.AddMinutes(2);
        schedule.TickAsync().GetAwaiter().GetResult();
        Check(calls == 0, "Cancel prevents shutdown");
        schedule.Schedule(1);
        schedule.Schedule(2);
        now = now.AddMinutes(1);
        schedule.TickAsync().GetAwaiter().GetResult();
        Check(calls == 0, "Replacement resets deadline");
        now = now.AddMinutes(2);
        schedule.TickAsync().GetAwaiter().GetResult();
        schedule.TickAsync().GetAwaiter().GetResult();
        Check(calls == 1 && schedule.DueAt is null, "Expired plan runs exactly once");
        TaskCompletionSource pending = new();
        ShutdownSchedule slow = new(() => pending.Task, () => now);
        slow.Schedule(1);
        now = now.AddMinutes(1);
        Task execution = slow.TickAsync();
        slow.TickAsync().GetAwaiter().GetResult();
        Check(slow.IsExecuting && slow.DueAt is null, "No duplicate request while executing");
        pending.SetResult();
        execution.GetAwaiter().GetResult();
        ShutdownSchedule failing = new(() => throw new InvalidOperationException("test failure"), () => now);
        failing.Schedule(1);
        now = now.AddMinutes(1);
        failing.TickAsync().GetAwaiter().GetResult();
        Check(failing.Error == "test failure" && !failing.IsExecuting && failing.DueAt is null, "Failure clears plan and exposes error");
        failing.Schedule(2);
        Check(failing.Error is null, "Can retry after failure");
        foreach (int minutes in new[] { 0, 1441 })
        {
            bool rejected = false;
            try { schedule.Schedule(minutes); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Reject invalid duration");
        }
        var command = ShutdownSchedule.CreateShutdownCommand();
        Check(command.Arguments == "/s /t 0" && !command.UseShellExecute && command.CreateNoWindow,
            "Shutdown command has no forced-close or shell flags");

        VerifyWindowsShutdownMode().GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        VerifyMonitorPowerOnly();
        VerifyPowerFailuresAndSettingsReentry();
        VerifyDisplayTransitions();
        VerifyCursorOwnership();
        VerifyRestoreTimeout();
        VerifyShutdownResponsiveness();
        VerifyPowerNotification();
        VerifyShutdownProcess();
        List<string> uiCommands = [];
        ShutdownSchedule uiSchedule = new(() => Task.CompletedTask, runCommand: command =>
        {
            uiCommands.Add(command.Arguments);
            return Task.CompletedTask;
        });
        using (SettingsForm windowsForm = new(new Settings(), uiSchedule))
        {
            windowsForm.Show();
            Find<CheckBox>(windowsForm, "定时关机采用 Windows 命令").Checked = true;
            Find<Button>(windowsForm, "开始计时").PerformClick();
            Check(uiCommands.SequenceEqual(new[] { "/s /t 3600" }) && windowsForm.UseWindowsShutdownCommand,
                "Settings checkbox routes countdown to the Windows command runner");
            Check(Descendants(windowsForm).Any(c => c.Text.Contains("强制关闭应用")), "Windows mode visibly warns about forced closure");
            Directory.CreateDirectory("tests/artifacts");
            Capture(windowsForm, "settings-windows-command");
            Find<Button>(windowsForm, "取消关机").PerformClick();
            Check(uiCommands.Last() == "/a" && uiSchedule.DueAt is null, "Settings cancel button aborts the Windows countdown");
            windowsForm.Close();
        }
        using SettingsForm form = new(new Settings(), schedule);
        form.Show();
        Application.DoEvents();
        Find<Button>(form, "开始计时").PerformClick();
        Check(schedule.DueAt.HasValue, "Settings starts countdown");
        Check(Find<Button>(form, "取消关机").Enabled, "Cancel becomes enabled");
        Directory.CreateDirectory("tests/artifacts");
        Capture(form, "settings");
        Find<Button>(form, "取消关机").PerformClick();
        Check(schedule.DueAt is null, "Settings cancels countdown");
        Check(Descendants(form).OfType<Button>().All(b => b.BackColor == Color.White && b.ForeColor == Color.Black),
            "All settings buttons use white backgrounds and black text");
        Check(!Descendants(form).Any(c => c.Text.Contains("USB")), "Settings has no USB note");
        Check(Descendants(form).OfType<CheckBox>().Any(c => c.Text == "开机时自动启动"),
            "Settings exposes startup option");
        form.Location = new Point(120, 140);
        form.Close();
        Check(form.Location == new Point(120, 140), "Settings window location is retained on close");
        using SettingsForm positioned = new(new Settings { SettingsWindowX = 120, SettingsWindowY = 140 }, schedule);
        positioned.Show();
        Application.DoEvents();
        Check(positioned.Location == new Point(120, 140), "Settings window restores saved location");
        positioned.Size = positioned.MinimumSize;
        Application.DoEvents();
        Capture(positioned, "settings-minimum");
        Button save = Find<Button>(positioned, "保存设置");
        Rectangle saveBounds = positioned.RectangleToClient(save.RectangleToScreen(save.ClientRectangle));
        Check(positioned.ClientRectangle.Contains(saveBounds), "Save button remains visible at minimum window size");
        Check(save.Parent!.Width - save.Right == save.Parent.Padding.Right,
            "Settings footer is aligned to the content right inset");
        NumericUpDown minutesInput = Descendants(positioned).OfType<NumericUpDown>().Single();
        Check(minutesInput.Font.Name == positioned.Font.Name && minutesInput.Font.Size == positioned.Font.Size,
            "Duration input uses the settings font and size");
        Button start = Find<Button>(positioned, "开始计时");
        Check(start.Parent!.Parent!.ClientRectangle.Contains(start.Parent.Parent.RectangleToClient(start.RectangleToScreen(start.ClientRectangle))),
            "Countdown button remains visible without scrolling");
        positioned.Scale(new SizeF(1.5F, 1.5F));
        Application.DoEvents();
        Capture(positioned, "settings-150");
        Check(save.Parent.Width - save.Right == save.Parent.Padding.Right,
            "Settings footer retains right alignment after scaling");
        positioned.Close();
        form.Close();
        using MainForm main = new(new Settings { CloseExits = true });
        main.Show();
        Application.DoEvents();
        Check(main.Visible, "Normal startup displays the main window");
        Capture(main, "main-registration-state");
        typeof(MainForm).GetMethod("UpdateStatus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(main, null);
        Capture(main, "main");
        Button blank = Find<Button>(main, "开始息屏");
        Check(!Descendants(main).OfType<Button>().Any(b => b.Text == "退出"),
            "Main window has no exit button");
        Check(!Descendants(main).Any(c => c.Text.Contains("屏幕已点亮") || c.Text.Contains("息屏保护中")),
            "Main window has no screen status row");
        Check(main.ClientRectangle.Contains(main.RectangleToClient(blank.RectangleToScreen(blank.ClientRectangle))),
            "Main action fits the compact window");
        main.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        Check(!main.Visible, "Minimizing hides the main window to tray");
        var tray = (NotifyIcon)typeof(MainForm).GetField("_trayIcon", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(main)!;
        typeof(NotifyIcon).GetMethod("OnDoubleClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(tray, [EventArgs.Empty]);
        Application.DoEvents();
        Check(main.Visible && main.WindowState == FormWindowState.Normal, "Tray double-click restores the main window");
        Check(blank.BackColor == Color.Black && blank.ForeColor == Color.White,
            "Main action uses a black background and white text");
        Button settings = Descendants(main).OfType<Button>().Single(b => b.AccessibleName == "设置");
        Check(settings.BackColor == Color.White && settings.ForeColor == Color.Black && settings.FlatAppearance.BorderSize == 0,
            "Settings icon uses a white background with no border");
        settings.Enabled = false;
        settings.Enabled = true;
        Check(settings.BackColor == Color.White && settings.ForeColor == Color.Black,
            "Settings icon retains its palette after enabled state changes");
        using Bitmap normalButton = new(blank.Width, blank.Height);
        blank.DrawToBitmap(normalButton, blank.ClientRectangle);
        typeof(ThemeButton).GetMethod("OnMouseEnter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(blank, [EventArgs.Empty]);
        using Bitmap hoverButton = new(blank.Width, blank.Height);
        blank.DrawToBitmap(hoverButton, blank.ClientRectangle);
        Check(normalButton.GetPixel(20, 20) != hoverButton.GetPixel(20, 20), "Primary button provides hover feedback");
        typeof(ThemeButton).GetMethod("OnMouseLeave", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(blank, [EventArgs.Empty]);
        var field = typeof(MainForm).GetField("_shutdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var mainSchedule = (ShutdownSchedule)field.GetValue(main)!;
        mainSchedule.Schedule(1440);
        Application.DoEvents();
        Capture(main, "main-countdown");
        Check(!Descendants(main).Any(c => c.Text.Contains("定时关机") || c.Text.Contains("剩余")),
            "Main window has no shutdown scheduling UI even with an active plan");
        main.Close();
        WaitUntil(() => { Application.DoEvents(); return main.IsDisposed; }, "Main window finishes asynchronous exit");
        Check(mainSchedule.DueAt is null, "Exiting app cancels its plan");
        using MainForm trayStart = new(new Settings { StartToTray = true, CloseExits = true });
        trayStart.Show();
        Application.DoEvents();
        Check(!trayStart.Visible, "Start-to-tray suppresses the initial window");
        trayStart.ShowMainWindow();
        Check(trayStart.Visible, "Start-to-tray window can be restored");
        trayStart.Close();
        Console.WriteLine("PASS: All checks; no real shutdown requested.");
    }

    private static void VerifyPowerFailuresAndSettingsReentry()
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        bool fail = true;
        List<int> requests = [];
        using MainForm window = new(new Settings { MonitorPowerOff = true, CloseExits = true },
            setDisplayPower: state =>
            {
                requests.Add(state);
                if (fail) throw new InvalidOperationException("Injected power failure");
            });
        window.Show();
        void Toggle() => typeof(MainForm).GetMethod("ToggleBlank", flags)!.Invoke(window, null);
        bool Blanked() => (bool)typeof(MainForm).GetField("_blanked", flags)!.GetValue(window)!;
        Toggle();
        Check(!Blanked(), "Rejected power-off does not leave a false active state");
        fail = false;
        TaskCompletionSource unfinishedRestore = new();
        typeof(MainForm).GetField("_displayRestore", flags)!.SetValue(window, unfinishedRestore.Task);
        Toggle();
        Check(Blanked(), "Power-only mode works while an earlier brightness restoration remains pending");
        fail = true;
        Toggle();
        Check(Blanked(), "Failed wake retains the active state for hotkey retry");
        fail = false;
        Toggle();
        Check(!Blanked(), "Wake retry clears the active state after success");
        unfinishedRestore.SetResult();
        using SettingsForm settings = new(new Settings(), new ShutdownSchedule(() => Task.CompletedTask));
        settings.Show(window);
        typeof(MainForm).GetField("_settingsForm", flags)!.SetValue(window, settings);
        int count = Application.OpenForms.OfType<SettingsForm>().Count();
        typeof(MainForm).GetMethod("ShowSettings", flags)!.Invoke(window, null);
        Check(Application.OpenForms.OfType<SettingsForm>().Count() == count,
            "Repeated settings request reuses the existing window");
        settings.Close();
        typeof(MainForm).GetField("_settingsForm", flags)!.SetValue(window, null);
        Toggle();
        fail = true;
        window.Close();
        Check(window.IsDisposed, "Power API failure cannot prevent normal exit or escape the event handler");
    }

    private static void VerifyMonitorPowerOnly()
    {
        List<int> powerCommands = [];
        int brightnessCalls = 0;
        Settings settings = new() { MonitorPowerOff = true, CloseExits = true };
        using MainForm window = new(settings,
            restoreBrightness: () => brightnessCalls++,
            applyBrightness: _ => brightnessCalls++,
            setDisplayPower: state => powerCommands.Add(state));
        window.Show();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Hotkey()
        {
            Message message = Message.Create(window.Handle, NativeMethods.WM_HOTKEY, new IntPtr(1), IntPtr.Zero);
            typeof(MainForm).GetMethod("WndProc", flags)!.Invoke(window, new object[] { message });
            Application.DoEvents();
        }
        Hotkey();
        Check(powerCommands.SequenceEqual(new[] { 2 }), "Power-only hotkey sends monitor-off immediately");
        Check(((List<BlankOverlay>)typeof(MainForm).GetField("_overlays", flags)!.GetValue(window)!).Count == 0,
            "Power-only mode creates no black overlays");
        Check(!(bool)typeof(MainForm).GetField("_cursorHidden", flags)!.GetValue(window)!, "Power-only mode does not hide the cursor");
        Check(!((System.Windows.Forms.Timer)typeof(MainForm).GetField("_overlayTimer", flags)!.GetValue(window)!).Enabled,
            "Power-only mode does not run the overlay timer");
        typeof(MainForm).GetMethod("SystemEvents_SessionSwitch", flags)!.Invoke(window,
            new object[] { window, new Microsoft.Win32.SessionSwitchEventArgs(Microsoft.Win32.SessionSwitchReason.SessionUnlock) });
        Check(powerCommands.Count == 1, "Session unlock does not force the monitor off again");
        settings.MonitorPowerOff = false;
        Hotkey();
        Check(powerCommands.SequenceEqual(new[] { 2, -1 }), "Hotkey restores the active mode even if the setting changed");
        settings.MonitorPowerOff = true;
        Hotkey();
        window.Close();
        Check(window.IsDisposed && powerCommands.SequenceEqual(new[] { 2, -1, 2, -1 }), "Exiting power-only mode wakes the monitor");
        Check(brightnessCalls == 0, "Power-only entry, recovery and exit never adjust brightness");
    }

    private static async Task VerifyWindowsShutdownMode()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<string> commands = [];
        int localCalls = 0;
        Exception? commandError = null;
        ShutdownSchedule timer = new(() => { localCalls++; return Task.CompletedTask; }, () => now,
            command =>
            {
                Check(!command.UseShellExecute && command.CreateNoWindow && Path.GetFileName(command.FileName) == "shutdown.exe",
                    "Windows schedule directly targets system shutdown.exe without a shell");
                commands.Add(command.Arguments);
                return commandError is null ? Task.CompletedTask : Task.FromException(commandError);
            });
        await timer.ScheduleAsync(60, true);
        Check(commands.SequenceEqual(new[] { "/s /t 3600" }) && timer.UsesWindowsCommand, "Windows mode submits seconds immediately");
        DateTimeOffset? firstDue = timer.DueAt;
        timer.Cancel();
        Check(timer.DueAt == firstDue, "App exit preserves Windows-owned countdown");
        await timer.ScheduleAsync(2, true);
        Check(commands.TakeLast(2).SequenceEqual(new[] { "/a", "/s /t 120" }), "Replacing a Windows timer cancels then reschedules");
        commandError = new InvalidOperationException("Injected abort failure");
        firstDue = timer.DueAt;
        await timer.CancelAsync();
        Check(timer.Error is not null && timer.DueAt == firstDue && timer.UsesWindowsCommand, "Failed abort retains the active plan");
        commandError = new ShutdownCommandException(1116, "No pending shutdown");
        await timer.CancelAsync();
        Check(timer.DueAt is null && timer.Error is null, "Already-aborted system plan clears stale state");
        commandError = null;
        await timer.ScheduleAsync(1, true);
        now = now.AddMinutes(2);
        int sent = commands.Count;
        await timer.TickAsync();
        Check(localCalls == 0 && commands.Count == sent && timer.DueAt is null, "Windows deadline never invokes a second local shutdown");
        await timer.ScheduleAsync(1, false);
        firstDue = timer.DueAt;
        commandError = new InvalidOperationException("Injected submission failure");
        await timer.ScheduleAsync(3, true);
        Check(timer.DueAt == firstDue && !timer.UsesWindowsCommand && timer.Error is not null, "Failed Windows submission preserves the local timer");
        commandError = null;
        await timer.ScheduleAsync(1, true);
        await timer.ScheduleAsync(2, false);
        Check(!timer.UsesWindowsCommand && commands.Last() == "/a", "Switching back to local mode aborts the previous Windows timer");
        now = now.AddMinutes(3);
        await timer.TickAsync();
        Check(localCalls == 1, "Local mode still invokes the original shutdown path");
        TaskCompletionSource pending = new();
        int submissions = 0;
        ShutdownSchedule busy = new(() => Task.CompletedTask, () => now, _ => { submissions++; return pending.Task; });
        Task submit = busy.ScheduleAsync(1, true);
        await busy.ScheduleAsync(2, true);
        await busy.CancelAsync();
        Check(submissions == 1 && busy.IsExecuting, "Overlapping commands cannot double-submit or cancel an unfinished submission");
        pending.SetResult();
        await submit;
        var stored = new Settings { UseWindowsShutdownCommand = true, WindowsShutdownDueAt = busy.WindowsDueAt };
        var restored = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(stored))!;
        ShutdownSchedule reopened = new(() => Task.CompletedTask, () => now, _ => Task.CompletedTask, restored.WindowsShutdownDueAt);
        Check(restored.UseWindowsShutdownCommand && reopened.UsesWindowsCommand && reopened.DueAt == busy.DueAt,
            "Windows mode and countdown survive settings serialization and reopen");
    }

    private static void VerifyCursorOwnership()
    {
        int live = 0;
        for (int i = 0; i < 100; i++)
        {
            using BlankOverlay overlay = new(new Rectangle(0, 0, 20, 20));
            IntPtr handle = overlay.Cursor.Handle;
            overlay.Dispose();
            if (!GetIconInfo(handle, out CursorInfo info)) continue;
            live++;
            if (info.Mask != IntPtr.Zero) NativeMethods.DeleteObject(info.Mask);
            if (info.Color != IntPtr.Zero) NativeMethods.DeleteObject(info.Color);
            NativeMethods.DestroyCursor(handle);
        }
        Check(live == 0, "100 disposed overlays leave no live native cursor handles");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public bool Icon;
        public int X, Y;
        public IntPtr Mask, Color;
    }

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr cursor, out CursorInfo info);

    private static void VerifyDisplayTransitions()
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (bool failApply in new[] { false, true })
        {
            using ManualResetEventSlim entered = new(false);
            using ManualResetEventSlim release = new(false);
            int restores = 0;
            using MainForm window = new(new Settings { CloseExits = true },
                restoreBrightness: () => Interlocked.Increment(ref restores),
                applyBrightness: token =>
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException();
                    if (failApply) throw new InvalidOperationException("Injected display failure");
                    token.ThrowIfCancellationRequested();
                });
            window.Show();
            Stopwatch elapsed = Stopwatch.StartNew();
            typeof(MainForm).GetMethod("Blank", flags)!.Invoke(window, null);
            try
            {
                Check(elapsed.ElapsedMilliseconds < 1000 && entered.Wait(1000), "Dimming runs off the UI thread");
                if (!failApply)
                {
                    typeof(MainForm).GetMethod("Unblank", flags)!.Invoke(window, null);
                    typeof(MainForm).GetMethod("Blank", flags)!.Invoke(window, null);
                    Check(!(bool)typeof(MainForm).GetField("_blanked", flags)!.GetValue(window)!,
                        "Repeated blank is blocked until the previous hardware operation completes");
                    Check(restores == 0, "Restoration cannot race a pending dim operation");
                }
            }
            finally { release.Set(); }
            WaitUntil(() => { Application.DoEvents(); return Volatile.Read(ref restores) == 1; },
                "Cancellation or dim failure restores the display once");
            WaitUntil(() => { Application.DoEvents(); return ((Task)typeof(MainForm).GetField("_displayRestore", flags)!.GetValue(window)!).IsCompleted; },
                "Display work completes before normal exit");
            Check(!(bool)typeof(MainForm).GetField("_blanked", flags)!.GetValue(window)!, "Failed or cancelled dim leaves no black overlay");
            window.Close();
        }
    }

    private static void VerifyRestoreTimeout()
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (bool failRestore in new[] { false, true })
        {
            using ManualResetEventSlim release = new(false);
            int requests = 0;
            using MainForm window = new(new Settings { CloseExits = true },
                restoreBrightness: () =>
                {
                    if (failRestore) throw new InvalidOperationException("Injected restore failure");
                    release.Wait(5000);
                },
                requestShutdown: () => { requests++; return Task.CompletedTask; },
                restoreTimeout: TimeSpan.FromMilliseconds(50));
            window.Show();
            typeof(MainForm).GetField("_blanked", flags)!.SetValue(window, true);
            ShutdownSchedule schedule = (ShutdownSchedule)typeof(MainForm).GetField("_shutdown", flags)!.GetValue(window)!;
            typeof(ShutdownSchedule).GetField("<DueAt>k__BackingField", flags)!.SetValue(schedule, DateTimeOffset.UtcNow.AddSeconds(-1));
            Task pending = schedule.TickAsync();
            try
            {
                WaitUntil(() => { Application.DoEvents(); return pending.IsCompleted; }, "Restore timeout or failure does not strand scheduled shutdown");
                Check(requests == 1 && !schedule.IsExecuting, "Restore timeout or failure still requests shutdown exactly once");
                Stopwatch closing = Stopwatch.StartNew();
                window.Close();
                WaitUntil(() => { Application.DoEvents(); return window.IsDisposed; }, "Exit is bounded when restoration stalls");
                Check(closing.ElapsedMilliseconds < 1000, "Exit does not wait for a stuck driver indefinitely");
            }
            finally
            {
                release.Set();
                ((Task)typeof(MainForm).GetField("_displayRestore", flags)!.GetValue(window)!).ContinueWith(_ => { }).Wait(1000);
            }
        }
    }

    private static void VerifyShutdownResponsiveness()
    {
        foreach (bool closeWhileRestoring in new[] { false, true })
        {
            using ManualResetEventSlim release = new(false);
            int shutdownCalls = 0;
            using MainForm window = new(new Settings { CloseExits = true },
                () => { if (!release.Wait(5000)) throw new TimeoutException("Test restoration timed out"); },
                () => { shutdownCalls++; return Task.CompletedTask; });
            window.Show();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(MainForm).GetField("_blanked", flags)!.SetValue(window, true);
            ShutdownSchedule timer = (ShutdownSchedule)typeof(MainForm).GetField("_shutdown", flags)!.GetValue(window)!;
            typeof(ShutdownSchedule).GetField("<DueAt>k__BackingField", flags)!.SetValue(timer, DateTimeOffset.UtcNow.AddSeconds(-1));
            Stopwatch elapsed = Stopwatch.StartNew();
            Task pending = timer.TickAsync();
            try
            {
                Check(elapsed.ElapsedMilliseconds < 1000 && !pending.IsCompleted,
                    "Slow display restoration does not block the shutdown timer's UI thread");
                bool dispatched = false;
                window.BeginInvoke((Action)(() => dispatched = true));
                Application.DoEvents();
                Check(dispatched && shutdownCalls == 0, "UI messages run while shutdown waits for display restoration");
                timer.TickAsync().GetAwaiter().GetResult();
                if (closeWhileRestoring)
                {
                    window.Close();
                    Check(!window.IsDisposed, "Closing waits asynchronously for display restoration");
                }
            }
            finally { release.Set(); }
            WaitUntil(() => { Application.DoEvents(); return pending.IsCompleted; }, "Shutdown preparation completes");
            pending.GetAwaiter().GetResult();
            Check(shutdownCalls == (closeWhileRestoring ? 0 : 1),
                closeWhileRestoring ? "Exiting during preparation suppresses shutdown" : "Shutdown is requested once after restoration");
            window.Close();
        }
    }

    private static void VerifyShutdownProcess()
    {
        foreach (int exitCode in new[] { 0, 7 })
        {
            ProcessStartInfo command = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                Arguments = $"/d /c exit /b {exitCode}", UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            bool failed = false;
            try { ShutdownSchedule.RunShutdownCommandAsync(command).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { failed = ex.Message.Contains("7"); }
            Check(failed == (exitCode != 0), "Shutdown command runner handles child-process exit codes without real shutdown");
        }
    }

    private static void VerifyPowerNotification()
    {
        using ManualResetEventSlim ready = new(false);
        using ManualResetEventSlim received = new(false);
        using ManualResetEventSlim release = new(false);
        IntPtr handle = IntPtr.Zero;
        Thread receiver = new(() =>
        {
            using PowerNotificationWindow window = new(received, release);
            handle = window.Handle;
            window.Shown += (_, _) => ready.Set();
            Application.Run(window);
        });
        receiver.SetApartmentState(ApartmentState.STA);
        receiver.Start();
        try
        {
            Check(ready.Wait(5000), "Power notification test window is ready");
            Stopwatch elapsed = Stopwatch.StartNew();
            Check(NativeMethods.NotifyDisplayPower(handle, NativeMethods.MonitorPowerOn), "Power notification is accepted");
            Check(elapsed.ElapsedMilliseconds < 1000 && received.Wait(1000),
                "Unresponsive recipient cannot block asynchronous power notification");
        }
        finally
        {
            release.Set();
            NativeMethods.SendNotifyMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            Check(receiver.Join(6000), "Power notification test window exits");
        }
    }

    private sealed class PowerNotificationWindow(ManualResetEventSlim received, ManualResetEventSlim release) : Form
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_SYSCOMMAND && m.WParam.ToInt32() == NativeMethods.SC_MONITORPOWER)
            {
                if (m.LParam.ToInt32() == -1) received.Set();
                release.Wait(5000);
                return; // Consume the test message without changing real monitor power.
            }
            base.WndProc(ref m);
        }
    }

    private static void VerifyRelease(string executable)
    {
        if (Mutex.TryOpenExisting("ScreenBlank_SingleInstance", out Mutex? existing))
        {
            existing.Dispose();
            throw new InvalidOperationException("Exit the running app before the release activation test.");
        }
        using Process first = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
        try
        {
            IntPtr window = IntPtr.Zero;
            WaitUntil(() =>
            {
                window = NativeMethods.FindWindow(null, "暗黑天幕");
                NativeMethods.GetWindowThreadProcessId(window, out uint id);
                return window != IntPtr.Zero && id == first.Id;
            }, "Published app creates its main window");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                NativeMethods.SendMessage(window, NativeMethods.WM_SYSCOMMAND, new IntPtr(0xF020), IntPtr.Zero);
                WaitUntil(() => !IsWindowVisible(window), "Published app minimizes to tray");
                using Process second = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
                try
                {
                    Check(second.WaitForExit(5000) && second.ExitCode == 0, "Second launch exits without a duplicate instance");
                    WaitUntil(() => IsWindowVisible(window) && !IsIconic(window), "Second launch restores the existing published window");
                }
                finally
                {
                    if (!second.HasExited) { second.Kill(); second.WaitForExit(); }
                }
            }
        }
        finally
        {
            if (!first.HasExited) { first.Kill(); first.WaitForExit(); }
        }
    }

    private static void WaitUntil(Func<bool> condition, string name)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition() && timeout.ElapsedMilliseconds < 5000) Thread.Sleep(50);
        Check(condition(), name);
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static T Find<T>(Control parent, string text) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match && child.Text == text) return match;
            try { return Find<T>(child, text); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Missing control: " + text);
    }

    private static void Capture(Form form, string name)
    {
        bool topMost = form.TopMost;
        form.TopMost = true;
        try
        {
            form.PerformLayout();
            form.Activate();
            form.Refresh();
            Application.DoEvents();
            using Bitmap bitmap = new(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save($"tests/artifacts/{name}.png", ImageFormat.Png);
            // Allow the desktop compositor's window transition to finish before capture.
            Thread.Sleep(600);
            Application.DoEvents();
            using Bitmap desktop = new(form.Width, form.Height);
            using (Graphics graphics = Graphics.FromImage(desktop))
                graphics.CopyFromScreen(form.Location, Point.Empty, desktop.Size);
            desktop.Save($"tests/artifacts/{name}-desktop.png", ImageFormat.Png);
        }
        finally
        {
            form.TopMost = topMost;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }
}
