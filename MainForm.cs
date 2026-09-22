using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenBlank;

internal sealed class MainForm : Form
{
    private const int HotkeyId = 1;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkB = 0x42;

    private readonly NotifyIcon _trayIcon;
    private readonly Settings _settings;
    private readonly Button _blankButton;
    private readonly ToolTip _tips = new();
    private readonly ShutdownSchedule _shutdown;
    private readonly System.Windows.Forms.Timer _shutdownTimer;
    private readonly List<BlankOverlay> _overlays = [];
    private readonly System.Windows.Forms.Timer _overlayTimer;
    private readonly DragSmooth _dragSmooth;
    private readonly GammaDim _gammaDim = new();
    private readonly Action _restoreBrightness;
    private readonly Action<CancellationToken> _applyBrightness;
    private readonly TimeSpan _restoreTimeout;
    private readonly Action<int> _setDisplayPower;
    private bool _monitorPowerBlanked;
    private CancellationTokenSource? _dimmingCancellation;
    private Task _displayApply = Task.CompletedTask;
    private Task _displayRestore = Task.CompletedTask;
    private bool _waitingForRestore;
    private bool _restoreWaitFinished;
    private bool _blanked;
    private bool _cursorHidden;
    private bool _exiting;
    private bool _cleanupDone;
    private bool _initialVisibilityHandled;
    private bool _restoringWindow;
    private SettingsForm? _settingsForm;

    public MainForm(Settings settings, Action? restoreBrightness = null, Func<Task>? requestShutdown = null,
        Action<CancellationToken>? applyBrightness = null, TimeSpan? restoreTimeout = null,
        Action<int>? setDisplayPower = null)
    {
        _settings = settings;
        _restoreBrightness = restoreBrightness ?? _gammaDim.Restore;
        _applyBrightness = applyBrightness ?? _gammaDim.Apply;
        _restoreTimeout = restoreTimeout ?? TimeSpan.FromSeconds(3);
        _setDisplayPower = setDisplayPower ?? NativeMethods.SetDisplayPower;
        _dragSmooth = new DragSmooth(this);

        Text = "暗黑天幕";
        Icon = AppIcons.LoadSmallIcon();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        UiTheme.Apply(this);
        ClientSize = new Size(460, 180);
        MaximizeBox = false;
        DoubleBuffered = true;

        _shutdown = new ShutdownSchedule(async () =>
        {
            Unblank();
            await WaitForDisplayRestoreAsync();
            if (!_exiting) await (requestShutdown ?? ShutdownSchedule.RequestShutdownAsync)();
        }, windowsDueAt: settings.WindowsShutdownDueAt);
        _shutdown.Changed += () =>
        {
            if (_settings.WindowsShutdownDueAt == _shutdown.WindowsDueAt) return;
            _settings.WindowsShutdownDueAt = _shutdown.WindowsDueAt;
            SettingsStore.Save(_settings);
        };
        _shutdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _shutdownTimer.Tick += async (_, _) => await _shutdown.TickAsync();
        _shutdownTimer.Start();

        TableLayoutPanel layout = UiTheme.Stack(28);
        TableLayoutPanel brand = new()
        {
            ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 20)
        };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
        brand.Controls.Add(new PictureBox
        {
            Image = AppIcons.LoadApplicationIcon()?.ToBitmap(),
            Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 2, 12, 0)
        });
        Label title = UiTheme.Label("暗黑天幕", true);
        title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.Margin = Padding.Empty;
        brand.Controls.Add(title);
        Button settingsButton = UiTheme.IconButton("\uE713", "设置", _tips);
        settingsButton.Click += (_, _) => ShowSettings();
        brand.Controls.Add(settingsButton);
        layout.Controls.Add(brand);
        _blankButton = UiTheme.Button("开始息屏");
        _blankButton.AutoSize = false;
        _blankButton.Dock = DockStyle.Fill;
        _blankButton.Height = 48;
        _blankButton.Font = new Font(Font.FontFamily, 10F, FontStyle.Regular);
        _blankButton.Margin = new Padding(0, 0, 0, 6);
        _blankButton.Click += (_, _) => ToggleBlank();
        layout.Controls.Add(_blankButton);
        Controls.Add(layout);

        _trayIcon = new NotifyIcon
        {
            Icon = AppIcons.LoadSmallIcon() ?? IconFactory.CreateShieldIcon(),
            Text = "暗黑天幕 - 息屏保护",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        _overlayTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _overlayTimer.Tick += (_, _) => EnsureOverlaysOnTop();

        SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        if (NativeMethods.RegisterHotKey(Handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkB))
        {
            return;
        }

        _tips.SetToolTip(_blankButton, "快捷键注册失败，可使用此按钮息屏");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            ToggleBlank();
            return;
        }

        _dragSmooth.WndProc(ref m);
        base.WndProc(ref m);
    }

    private ContextMenuStrip BuildMenu()
    {
        ContextMenuStrip menu = new();
        menu.Items.Add("立即息屏", null, (_, _) => Blank());
        menu.Items.Add("设置...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());
        return menu;
    }

    internal void ShowMainWindow()
    {
        if (IsDisposed || _exiting) return;
        _initialVisibilityHandled = true;
        _restoringWindow = true;
        try
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }
        finally
        {
            _restoringWindow = false;
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && !_initialVisibilityHandled)
        {
            _initialVisibilityHandled = true;
            if (_settings.StartToTray) value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_restoringWindow && WindowState == FormWindowState.Minimized) Hide();
    }

    private void ShowSettings()
    {
        if (_exiting || IsDisposed) return;
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        using SettingsForm form = new(_settings, _shutdown);
        _settingsForm = form;
        try
        {
            if (form.ShowDialog(this) == DialogResult.OK)
            {
                _settings.MonitorPowerOff = form.MonitorPowerOff;
                _settings.StartToTray = form.StartToTray;
                _settings.CloseExits = form.CloseExits;
                _settings.AutoStart = form.AutoStart;
                _settings.UseWindowsShutdownCommand = form.UseWindowsShutdownCommand;
            }
            _settings.SettingsWindowX = form.Location.X;
            _settings.SettingsWindowY = form.Location.Y;
            SettingsStore.Save(_settings);
            StartupManager.Apply(_settings.AutoStart);
        }
        finally { _settingsForm = null; }
    }

    private void ToggleBlank()
    {
        if (_blanked)
        {
            Unblank();
        }
        else
        {
            Blank();
        }
    }

    private async void Blank()
    {
        if (_blanked || (!_settings.MonitorPowerOff && !_displayRestore.IsCompleted) || _shutdown.IsExecuting || _exiting)
        {
            return;
        }

        _blanked = true;
        _monitorPowerBlanked = _settings.MonitorPowerOff;
        _dimmingCancellation?.Dispose();
        _dimmingCancellation = new CancellationTokenSource();
        CancellationToken token = _dimmingCancellation.Token;
        try
        {
            UpdateStatus();
            if (_monitorPowerBlanked)
            {
                NativeMethods.SetThreadExecutionState(
                    NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
                if (!TrySetDisplayPower(2))
                {
                    _blanked = false;
                    _monitorPowerBlanked = false;
                    UpdateStatus();
                    NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
                }
                return;
            }
            HideSystemCursor();
            _cursorHidden = true;
            NativeMethods.SetThreadExecutionState(
                NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED);

            foreach (Screen screen in Screen.AllScreens)
            {
                BlankOverlay overlay = new(screen.Bounds);
                overlay.DoubleClicked += Unblank;
                _overlays.Add(overlay);
                overlay.Show(this);
            }

            EnsureOverlaysOnTop();
            _overlayTimer.Start();
            if (token.IsCancellationRequested) return;
            _displayApply = Task.Run(() => _applyBrightness(token));
            await _displayApply;
            if (token.IsCancellationRequested || !_blanked || _exiting || IsDisposed) return;
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested && !IsDisposed && _blanked) Unblank();
        }
    }

    private void Unblank()
    {
        if (!_blanked)
        {
            return;
        }

        // Keep the active mode on failure so the same hotkey can retry waking it.
        if (_monitorPowerBlanked && !TrySetDisplayPower(NativeMethods.MonitorPowerOn) && !_exiting) return;
        _blanked = false;
        UpdateStatus();
        if (_cursorHidden)
        {
            RestoreSystemCursor();
            _cursorHidden = false;
        }
        _overlayTimer.Stop();
        if (_monitorPowerBlanked)
        {
            _monitorPowerBlanked = false;
        }
        else StartDisplayRestore();

        foreach (BlankOverlay overlay in _overlays)
        {
            overlay.Close();
            overlay.Dispose();
        }
        _overlays.Clear();

        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
    }

    private bool TrySetDisplayPower(int state)
    {
        try { _setDisplayPower(state); return true; }
        catch (Exception ex)
        {
            if (!IsDisposed && !_exiting)
            {
                _tips.SetToolTip(_blankButton, ex.Message);
                _trayIcon.ShowBalloonTip(4000, "暗黑天幕", "显示器电源操作失败，请重试。", ToolTipIcon.Warning);
            }
            return false;
        }
    }

    private void EnsureOverlaysOnTop()
    {
        if (!_blanked)
        {
            return;
        }

        foreach (BlankOverlay overlay in _overlays)
        {
            if (!overlay.IsDisposed && overlay.IsHandleCreated)
            {
                NativeMethods.SetWindowPos(
                    overlay.Handle,
                    new IntPtr(NativeMethods.HWND_TOPMOST),
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
        }
    }

    private void StartDisplayRestore()
    {
        _dimmingCancellation?.Cancel();
        Task applying = _displayApply;
        // A timed-out driver call cannot be killed; serialize its eventual restoration.
        _displayRestore = Task.Run(async () =>
        {
            try { await applying; }
            catch (Exception) { }
            _restoreBrightness();
        });
        _ = _displayRestore.ContinueWith(task => { _ = task.Exception; },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private async Task WaitForDisplayRestoreAsync()
    {
        try { await _displayRestore.WaitAsync(_restoreTimeout); }
        catch (Exception)
        {
            if (!IsDisposed && !_exiting)
                _trayIcon.ShowBalloonTip(4000, "暗黑天幕", "显示器亮度恢复失败或超时，关机请求将继续。", ToolTipIcon.Warning);
        }
    }

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock && _blanked && !_monitorPowerBlanked)
        {
            foreach (BlankOverlay overlay in _overlays)
            {
                overlay.BringToFront();
            }
            EnsureOverlaysOnTop();
        }
    }

    private void Exit()
    {
        _exiting = true;
        Close();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_exiting && !_settings.CloseExits)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
        if (!e.Cancel)
        {
            _exiting = true;
            if (_blanked) Unblank();
            // Let the message loop run while WMI/drivers restore the display.
            if (!_restoreWaitFinished && !_displayRestore.IsCompleted && e.CloseReason != CloseReason.WindowsShutDown)
            {
                e.Cancel = true;
                if (_waitingForRestore) return;
                _waitingForRestore = true;
                try { await WaitForDisplayRestoreAsync(); }
                finally { _waitingForRestore = false; }
                _restoreWaitFinished = true;
                if (!IsDisposed) Close();
                return;
            }
            CleanupForExit();
        }
    }

    private void CleanupForExit()
    {
        if (_cleanupDone)
        {
            return;
        }

        _cleanupDone = true;
        _shutdownTimer.Stop();
        _shutdown.Cancel();
        if (_blanked)
        {
            Unblank();
        }

        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private void HideSystemCursor()
    {
        for (int i = 0; i < 16; i++)
        {
            if (NativeMethods.ShowCursor(false) < 0)
            {
                break;
            }
        }
    }

    private void RestoreSystemCursor()
    {
        for (int i = 0; i < 16; i++)
        {
            if (NativeMethods.ShowCursor(true) >= 0)
            {
                break;
            }
        }
    }

    private void UpdateStatus()
    {
        _blankButton.Text = _blanked ? "解除息屏" : "开始息屏";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dimmingCancellation?.Cancel();
            _dimmingCancellation?.Dispose();
            _dimmingCancellation = null;
            _tips.Dispose();
            _shutdownTimer.Dispose();
            _shutdown.Cancel();
            _overlayTimer.Dispose();
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}


internal static class AppIcons
{
    private static Icon? _cached;
    private static Icon? _smallCached;

    public static Icon? LoadApplicationIcon()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            using Stream stream = typeof(AppIcons).Assembly.GetManifestResourceStream("ScreenBlank.Assets.icon.ico")!;
            _cached = new Icon(stream);
            return _cached;
        }
        catch
        {
            return null;
        }
    }

    public static Icon? LoadSmallIcon()
    {
        if (_smallCached is not null)
        {
            return _smallCached;
        }

        try
        {
            using Stream stream = typeof(AppIcons).Assembly.GetManifestResourceStream("ScreenBlank.Assets.icon.ico")!;
            using Icon full = new(stream);
            using Icon small = new(full, new Size(16, 16));
            _smallCached = (Icon)small.Clone();
            return _smallCached;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class DragSmooth
{
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_SETREDRAW = 0x000B;
    private const int WM_PAINT = 0x000F;

    private readonly Form _form;
    private bool _redrawSuppressed;

    public DragSmooth(Form form)
    {
        _form = form;
    }

    public void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ENTERSIZEMOVE:
                SuppressRedraw();
                break;
            case WM_EXITSIZEMOVE:
                RestoreRedraw();
                break;
            case WM_PAINT when _redrawSuppressed:
                m.Result = new IntPtr(1);
                return;
        }
    }

    private void SuppressRedraw()
    {
        if (_redrawSuppressed)
        {
            return;
        }

        _redrawSuppressed = true;
        NativeMethods.SendMessage(_form.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
    }

    private void RestoreRedraw()
    {
        if (!_redrawSuppressed)
        {
            return;
        }

        _redrawSuppressed = false;
        NativeMethods.SendMessage(_form.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
        NativeMethods.InvalidateRect(_form.Handle, IntPtr.Zero, true);
    }
}

internal sealed class SmoothLabel : Label
{
    public SmoothLabel()
    {
        DoubleBuffered = true;
    }
}

internal static class IconFactory
{
    public static Icon CreateShieldIcon()
    {
        using Bitmap bmp = new(16, 16);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using SolidBrush shieldBrush = new(Color.FromArgb(35, 45, 70));
            g.FillPolygon(shieldBrush,
            [
                new Point(3, 2),
                new Point(13, 2),
                new Point(13, 8),
                new Point(8, 14),
                new Point(3, 8)
            ]);

            using SolidBrush accentBrush = new(Color.FromArgb(90, 190, 255));
            g.FillRectangle(accentBrush, 7, 5, 2, 4);
            g.FillRectangle(accentBrush, 6, 7, 4, 1);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }
}

internal static class BlankCursor
{
    public static Cursor Create()
    {
        using Bitmap mask = new(1, 1);
        using (Graphics g = Graphics.FromImage(mask))
        {
            g.Clear(Color.White);
        }

        NativeMethods.ICONINFO info = new()
        {
            fIcon = false,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = mask.GetHbitmap(),
            hbmColor = IntPtr.Zero
        };

        IntPtr cursorHandle = NativeMethods.CreateIconIndirect(ref info);
        NativeMethods.DeleteObject(info.hbmMask);
        return new Cursor(cursorHandle);
    }
}
