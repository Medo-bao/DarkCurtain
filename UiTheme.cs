namespace ScreenBlank;

internal static class UiTheme
{
    public static readonly Color Ink = Color.FromArgb(24, 24, 24);
    public static readonly Color Muted = Color.FromArgb(105, 105, 105);
    public static readonly Color Accent = Color.Black;
    public static readonly Color Canvas = Color.FromArgb(247, 247, 247);
    public static readonly Color Line = Color.FromArgb(226, 226, 226);

    public static void Apply(Form form)
    {
        form.Font = new Font("Microsoft YaHei UI", 9.5F);
        form.BackColor = Color.White;
        form.ForeColor = Ink;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(96, 96);
    }

    public static Label Label(string text, bool heading = false) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 0, 8),
        Font = new Font("Microsoft YaHei UI", heading ? 12F : 9.5F,
            heading ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = heading ? Ink : Muted
    };

    public static Button Button(string text, bool light = false) => new ThemeButton(light: light)
    {
        Text = text, AutoSize = true, MinimumSize = new Size(100, 40),
        Padding = new Padding(16, 5, 16, 5), Margin = new Padding(0, 0, 12, 0),
        Cursor = Cursors.Hand
    };

    public static Button IconButton(string glyph, string name, ToolTip tips)
    {
        Button button = new ThemeButton(iconOnly: true)
        {
            Text = glyph,
            Cursor = Cursors.Hand
        };
        button.Font = new Font("Segoe MDL2 Assets", 12F);
        button.AutoSize = false;
        button.MinimumSize = button.Size = new Size(40, 40);
        button.Padding = Padding.Empty;
        button.AccessibleName = name;
        button.Margin = Padding.Empty;
        tips.SetToolTip(button, name);
        return button;
    }

    public static TableLayoutPanel Stack(int padding = 24)
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true,
            Padding = new Padding(padding), GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return panel;
    }

    public static FlowLayoutPanel Actions() => new()
    {
        AutoSize = true, Dock = DockStyle.Fill, WrapContents = true,
        Margin = new Padding(0, 8, 0, 8)
    };

    public static Control Divider() => new Panel
    {
        Height = 1, Dock = DockStyle.Top, BackColor = Line,
        Margin = new Padding(0, 12, 0, 18)
    };

    public static Control Footer(params Button[] buttons)
    {
        TableLayoutPanel footer = new()
        {
            Dock = DockStyle.Bottom, Height = 72, ColumnCount = buttons.Length + 1,
            RowCount = 1, Padding = new Padding(28, 14, 28, 14),
            BackColor = Color.White, Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < buttons.Length; i++)
        {
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttons[i].Margin = new Padding(12, 0, 0, 0);
            buttons[i].Anchor = AnchorStyles.Right;
            footer.Controls.Add(buttons[i], i + 1, 0);
        }
        footer.Paint += (_, e) => e.Graphics.DrawLine(Pens.Gainsboro, 0, 0, footer.Width, 0);
        return footer;
    }
}

internal sealed class ThemeButton : Button
{
    private readonly bool _iconOnly;
    private readonly bool _light;

    public ThemeButton(bool iconOnly = false, bool light = false)
    {
        _iconOnly = iconOnly;
        _light = light || iconOnly;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        ApplyPalette();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        BackColor = _light ? Color.White : Color.Black;
        ForeColor = _light ? Color.Black : Color.White;
        FlatAppearance.BorderSize = _iconOnly ? 0 : 1;
        FlatAppearance.BorderColor = _light ? UiTheme.Line : Color.Black;
        FlatAppearance.MouseOverBackColor = _light ? Color.FromArgb(242, 242, 242) : Color.FromArgb(38, 38, 38);
        FlatAppearance.MouseDownBackColor = _light ? Color.FromArgb(226, 226, 226) : Color.FromArgb(64, 64, 64);
    }
}
