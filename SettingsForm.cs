namespace ScreenBlank;

internal sealed class SettingsForm : Form
{
    private readonly CheckBox _monitorPowerBox;
    private readonly CheckBox _closeExitsBox;
    private readonly CheckBox _startTrayBox;
    private readonly CheckBox _autoStartBox;
    private readonly CheckBox _windowsShutdownBox;
    private readonly Label _shutdownNotice;
    private readonly ShutdownSchedule _shutdown;
    private readonly NumericUpDown _minutes;
    private readonly Label _scheduleStatus;
    private readonly Button _scheduleButton;
    private readonly Button _cancelScheduleButton;
    private readonly MemoryCleaner _memoryCleaner;
    private readonly CancellationToken _memoryCancellation;
    private readonly Dictionary<MemoryCleanItems, CheckBox> _memoryOptions = [];
    private readonly Button _cleanMemoryButton;
    private readonly Label _memoryStatus;

    public SettingsForm(Settings settings, ShutdownSchedule shutdown, MemoryCleaner? memoryCleaner = null,
        CancellationToken memoryCancellation = default)
    {
        _shutdown = shutdown;
        _memoryCleaner = memoryCleaner ?? new MemoryCleaner();
        _memoryCancellation = memoryCancellation;
        Text = "暗黑天幕 · 设置";
        Icon = AppIcons.LoadSmallIcon();
        UiTheme.Apply(this);
        ClientSize = new Size(560, 720);
        MinimumSize = SizeFromClientSize(new Size(560, 720));
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        if (settings.SettingsWindowX >= 0 && settings.SettingsWindowY >= 0)
        {
            StartPosition = FormStartPosition.Manual;
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int x = Math.Clamp(settings.SettingsWindowX, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            int y = Math.Clamp(settings.SettingsWindowY, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            Location = new Point(x, y);
        }
        DoubleBuffered = true;

        TableLayoutPanel layout = UiTheme.Stack(28);
        layout.Dock = DockStyle.Top;
        layout.AutoScroll = false;
        layout.AutoSize = true;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Label title = UiTheme.Label("设置", true);
        title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
        title.Margin = new Padding(0, 0, 0, 16);
        layout.Controls.Add(title);
        layout.Controls.Add(UiTheme.Label("常规", true));
        _monitorPowerBox = Option("关闭显示器电源", settings.MonitorPowerOff);
        _startTrayBox = Option("启动时最小化到托盘", settings.StartToTray);
        _autoStartBox = Option("开机时自动启动", settings.AutoStart);
        _closeExitsBox = Option("关闭窗口时直接退出", settings.CloseExits);
        layout.Controls.Add(_monitorPowerBox);
        layout.Controls.Add(_startTrayBox);
        layout.Controls.Add(_autoStartBox);
        layout.Controls.Add(_closeExitsBox);
        layout.Controls.Add(UiTheme.Divider());
        layout.Controls.Add(UiTheme.Label("定时关机", true));
        _windowsShutdownBox = Option("定时关机采用 Windows 命令", settings.UseWindowsShutdownCommand);
        layout.Controls.Add(_windowsShutdownBox);

        FlowLayoutPanel time = UiTheme.Actions();
        time.Margin = new Padding(0, 4, 0, 14);
        _minutes = new NumericUpDown
        {
            Minimum = 1, Maximum = 1440, Value = 60, Width = 104,
            Font = Font,
            TextAlign = HorizontalAlignment.Center,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "关机倒计时分钟数", Margin = new Padding(0, 0, 16, 0)
        };
        time.Controls.Add(_minutes);
        time.Controls.Add(new Label
        {
            Text = "分钟后关机", AutoSize = true, ForeColor = UiTheme.Muted,
            Margin = new Padding(0, 3, 0, 0)
        });
        layout.Controls.Add(time);

        _scheduleStatus = UiTheme.Label("");
        _scheduleStatus.BackColor = Color.White;
        _scheduleStatus.Padding = new Padding(0, 6, 0, 6);
        _scheduleStatus.MinimumSize = new Size(0, 32);
        _scheduleStatus.Margin = new Padding(0, 0, 0, 8);
        layout.Controls.Add(_scheduleStatus);
        layout.SizeChanged += (_, _) => _scheduleStatus.MaximumSize =
            new Size(Math.Max(100, layout.ClientSize.Width - layout.Padding.Horizontal), 0);

        FlowLayoutPanel scheduleActions = UiTheme.Actions();
        _scheduleButton = UiTheme.Button("开始计时", light: true);
        _scheduleButton.Click += async (_, _) => await _shutdown.ScheduleAsync((int)_minutes.Value, _windowsShutdownBox.Checked);
        _cancelScheduleButton = UiTheme.Button("取消关机", light: true);
        _cancelScheduleButton.Click += async (_, _) => await _shutdown.CancelAsync();
        scheduleActions.Controls.AddRange([_scheduleButton, _cancelScheduleButton]);
        layout.Controls.Add(scheduleActions);
        _shutdownNotice = UiTheme.Label("");
        _shutdownNotice.Font = new Font(Font.FontFamily, 8.5F);
        layout.Controls.Add(_shutdownNotice);
        layout.SizeChanged += (_, _) => _shutdownNotice.MaximumSize =
            new Size(Math.Max(100, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
        _windowsShutdownBox.CheckedChanged += (_, _) => RefreshSchedule();

        layout.Controls.Add(UiTheme.Divider());
        layout.Controls.Add(UiTheme.Label("内存清理", true));
        foreach (MemoryCleanOption option in MemoryCleaner.Options)
        {
            CheckBox box = Option(option.Name, settings.MemoryCleanupItems.HasFlag(option.Item));
            box.CheckedChanged += (_, _) => RefreshMemory();
            _memoryOptions.Add(option.Item, box);
            layout.Controls.Add(box);
        }
        _cleanMemoryButton = UiTheme.Button("清理所选内存", light: true);
        _cleanMemoryButton.Click += async (_, _) => await _memoryCleaner.CleanAsync(SelectedMemoryItems, _memoryCancellation);
        FlowLayoutPanel memoryActions = UiTheme.Actions();
        memoryActions.Controls.Add(_cleanMemoryButton);
        layout.Controls.Add(memoryActions);
        _memoryStatus = UiTheme.Label("内存清理就绪");
        layout.Controls.Add(_memoryStatus);
        Label memoryNotice = UiTheme.Label("系统缓存清理需要管理员授权。进程工作集整理会跳过系统关键进程和已识别游戏。\n清理结果显示本次可用内存变化；工作集减少不等于释放了相同数量的物理内存。");
        memoryNotice.Name = "MemoryCleanupNotice";
        memoryNotice.Font = new Font(Font.FontFamily, 8.5F);
        layout.Controls.Add(memoryNotice);
        layout.SizeChanged += (_, _) =>
        {
            int width = Math.Max(100, layout.ClientSize.Width - layout.Padding.Horizontal);
            _memoryStatus.MaximumSize = new Size(width, 0);
            memoryNotice.MaximumSize = new Size(width, 0);
        };

        Button done = UiTheme.Button("保存设置", light: true);
        done.DialogResult = DialogResult.OK;
        Button close = UiTheme.Button("关闭", light: true);
        close.DialogResult = DialogResult.Cancel;
        Panel scrollContent = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        scrollContent.ClientSizeChanged += (_, _) =>
            layout.MaximumSize = new Size(scrollContent.ClientSize.Width, 0);
        scrollContent.Controls.Add(layout);
        Control footer = UiTheme.Footer(close, done);
        footer.Dock = DockStyle.Fill;
        TableLayoutPanel page = new()
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, footer.Height));
        page.Controls.Add(scrollContent, 0, 0);
        page.Controls.Add(footer, 0, 1);
        Controls.Add(page);
        AcceptButton = done;
        CancelButton = close;
        _shutdown.Changed += RefreshSchedule;
        _memoryCleaner.Changed += RefreshMemory;
        RefreshSchedule();
        RefreshMemory();
    }

    private void RefreshMemory()
    {
        if (IsDisposed || Disposing || _cleanMemoryButton is null) return;
        if (InvokeRequired)
        {
            if (IsHandleCreated) BeginInvoke((Action)RefreshMemory);
            return;
        }
        _cleanMemoryButton.Enabled = !_memoryCleaner.IsRunning && MemoryCleaner.ValidItems(SelectedMemoryItems);
        foreach (CheckBox box in _memoryOptions.Values) box.Enabled = !_memoryCleaner.IsRunning;
        _memoryStatus.Text = _memoryCleaner.IsRunning ? "正在清理内存…"
            : _memoryCleaner.LastResult is { } result ? result.Message + "\n" + result.Detail : "内存清理就绪";
    }

    private static CheckBox Option(string text, bool value) => new()
    {
        Text = text, Checked = value, AutoSize = true, Dock = DockStyle.Fill,
        CheckAlign = ContentAlignment.MiddleRight,
        MinimumSize = new Size(0, 32),
        Margin = new Padding(0, 0, 0, 6), Cursor = Cursors.Hand
    };

    private void RefreshSchedule()
    {
        _scheduleStatus.Text = _shutdown.Status;
        _scheduleStatus.ForeColor = _shutdown.Error is null
            ? _shutdown.DueAt.HasValue ? UiTheme.Accent : UiTheme.Muted : UiTheme.Ink;
        _scheduleButton.Text = _shutdown.DueAt.HasValue ? "重新计时" : "开始计时";
        _scheduleButton.Enabled = !_shutdown.IsExecuting;
        _minutes.Enabled = !_shutdown.IsExecuting;
        _cancelScheduleButton.Enabled = !_shutdown.IsExecuting && _shutdown.DueAt.HasValue;
        _windowsShutdownBox.Enabled = !_shutdown.IsExecuting;
        _shutdownNotice.Text = (_windowsShutdownBox.Checked || _shutdown.UsesWindowsCommand)
            ? "Windows 计划退出软件后仍生效，到期强制关闭应用，请保存工作。\n取消会撤销当前系统关机计划；切换模式后需重新计时。"
            : "到时关闭电脑，请保存工作。退出软件会取消计划。";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdown.Changed -= RefreshSchedule;
            _memoryCleaner.Changed -= RefreshMemory;
        }
        base.Dispose(disposing);
    }

    public bool MonitorPowerOff => _monitorPowerBox.Checked;
    public bool CloseExits => _closeExitsBox.Checked;
    public bool StartToTray => _startTrayBox.Checked;
    public bool AutoStart => _autoStartBox.Checked;
    public bool UseWindowsShutdownCommand => _windowsShutdownBox.Checked;
    public MemoryCleanItems SelectedMemoryItems => _memoryOptions
        .Where(option => option.Value.Checked).Aggregate(MemoryCleanItems.None, (items, option) => items | option.Key);
}
