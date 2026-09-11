namespace ScreenBlank;

internal sealed class BlankOverlay : Form
{
    public event Action? DoubleClicked;
    private DateTime _lastLeftDown = DateTime.MinValue;
    private Point _lastLeftDownLocation;

    public BlankOverlay(Rectangle bounds)
    {
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        Cursor = BlankCursor.Create();
        MouseDown += BlankOverlay_MouseDown;
    }

    private void BlankOverlay_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        bool withinTime = (DateTime.Now - _lastLeftDown).TotalMilliseconds <= SystemInformation.DoubleClickTime;
        bool withinDistance = Math.Abs(e.Location.X - _lastLeftDownLocation.X) <= SystemInformation.DoubleClickSize.Width
            && Math.Abs(e.Location.Y - _lastLeftDownLocation.Y) <= SystemInformation.DoubleClickSize.Height;

        _lastLeftDown = DateTime.Now;
        _lastLeftDownLocation = e.Location;

        if (withinTime && withinDistance)
        {
            DoubleClicked?.Invoke();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }
}
