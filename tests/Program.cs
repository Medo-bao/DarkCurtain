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

        ApplicationConfiguration.Initialize();
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
