using System.Runtime.InteropServices;
using System.Management;

namespace ScreenBlank;

internal sealed class GammaDim
{
    private readonly List<DisplayRamp> _displays = [];
    private readonly List<byte> _originalBrightness = [];
    private readonly Func<IReadOnlyList<byte>> _readBrightness;
    private readonly Action<byte, CancellationToken> _setBacklight;
    private readonly Action<IReadOnlyList<byte>> _restoreBacklight;
    private bool _dimmed;

    public GammaDim(Func<IReadOnlyList<byte>>? readBrightness = null,
        Action<byte, CancellationToken>? setBacklight = null,
        Action<IReadOnlyList<byte>>? restoreBacklight = null)
    {
        _readBrightness = readBrightness ?? ReadBrightness;
        _setBacklight = setBacklight ?? SetBacklightBrightness;
        _restoreBacklight = restoreBacklight ?? RestoreBrightness;
    }

    public void Apply(CancellationToken cancellationToken = default)
    {
        // Keep a failed restoration's original values until it has actually completed.
        if (_dimmed) Restore();
        EnumerateDisplays();
        if (_displays.Count == 0)
        {
            return;
        }

        foreach (DisplayRamp display in _displays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (display.SaveOriginal())
            {
                cancellationToken.ThrowIfCancellationRequested();
                _dimmed = true;
                display.ApplyDark();
            }
        }
        CaptureAndDimBacklight(cancellationToken);
    }

    public void Restore()
    {
        if (!_dimmed)
        {
            return;
        }

        foreach (DisplayRamp display in _displays)
        {
            display.RestoreOriginal();
        }
        if (_originalBrightness.Count > 0) _restoreBacklight(_originalBrightness);
        _originalBrightness.Clear();
        _dimmed = false;
    }

    internal void CaptureAndDimBacklight(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<byte> captured;
        try { captured = _readBrightness(); }
        catch (OperationCanceledException) { throw; }
        catch { return; }
        // Never dim a backlight whose original brightness could not be saved.
        if (captured.Count == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        _originalBrightness.Clear();
        _originalBrightness.AddRange(captured);
        _dimmed = true;
        _setBacklight(1, cancellationToken);
    }

    private static IReadOnlyList<byte> ReadBrightness()
    {
        List<byte> brightnessValues = [];
        try
        {
            using ManagementObjectSearcher searcher = new(
                new ManagementScope(@"\\.\root\wmi"),
                new WqlObjectQuery("SELECT * FROM WmiMonitorBrightness"));
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    object raw = obj["CurrentBrightness"];
                    if (ushort.TryParse(raw?.ToString(), out ushort brightness))
                    {
                        byte value = (byte)Math.Min(100, (int)brightness);
                        brightnessValues.Add(value);
                    }
                }
            }
        }
        catch (Exception)
        {
            brightnessValues.Clear();
        }
        return brightnessValues;
    }

    private void SetBacklightBrightness(byte level, CancellationToken cancellationToken)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                new ManagementScope(@"\\.\root\wmi"),
                new WqlObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using ManagementBaseObject? parameters = obj.GetMethodParameters("WmiSetBrightness");
                    if (parameters is null)
                    {
                        continue;
                    }
                    parameters["Brightness"] = level;
                    parameters["Timeout"] = 1;
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = obj.InvokeMethod("WmiSetBrightness", parameters, null);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static void RestoreBrightness(IReadOnlyList<byte> originalBrightness)
    {
        if (originalBrightness.Count == 0)
        {
            return;
        }

        using ManagementObjectSearcher searcher = new(
            new ManagementScope(@"\\.\root\wmi"),
            new WqlObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
        using ManagementObjectCollection monitors = searcher.Get();
        int index = 0;
        foreach (ManagementObject obj in monitors)
        {
            using (obj)
            {
                if (index >= originalBrightness.Count) break;
                byte original = originalBrightness[index];
                using ManagementBaseObject? parameters = obj.GetMethodParameters("WmiSetBrightness");
                if (parameters is null) throw new InvalidOperationException("显示器没有提供亮度恢复方法");
                parameters["Brightness"] = original;
                parameters["Timeout"] = 1;
                _ = obj.InvokeMethod("WmiSetBrightness", parameters, null);
                index++;
            }
        }
        if (index < originalBrightness.Count) throw new InvalidOperationException("未能恢复已保存的显示器亮度");
    }

    private void EnumerateDisplays()
    {
        if (_displays.Count > 0)
        {
            return;
        }

        for (uint index = 0; ; index++)
        {
            NativeMethods.DISPLAY_DEVICE device = new() { cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
            if (!NativeMethods.EnumDisplayDevices(null, index, ref device, 0))
            {
                break;
            }

            if (device.StateFlags.HasFlag(NativeMethods.DisplayDeviceStateFlags.AttachedToDesktop))
            {
                _displays.Add(new DisplayRamp(device.DeviceName));
            }
        }
    }
}

internal sealed class DisplayRamp
{
    private const ushort DarkValue = 2;
    private readonly string _deviceName;
    private readonly ushort[] _original = new ushort[3 * 256];
    private bool _originalCaptured;

    public DisplayRamp(string deviceName)
    {
        _deviceName = deviceName;
    }

    public bool SaveOriginal()
    {
        using DisplayContext context = new(_deviceName);
        if (!context.IsValid)
        {
            return false;
        }

        bool ok = NativeMethods.GetDeviceGammaRamp(context.Handle, _original);
        _originalCaptured = ok;
        return ok;
    }

    public void ApplyDark()
    {
        using DisplayContext context = new(_deviceName);
        if (!context.IsValid)
        {
            return;
        }

        ushort[] dark = new ushort[3 * 256];
        for (int i = 0; i < dark.Length; i++)
        {
            dark[i] = DarkValue;
        }
        SetRamp(context.Handle, dark);
    }

    public void RestoreOriginal()
    {
        if (!_originalCaptured)
        {
            return;
        }

        using DisplayContext context = new(_deviceName);
        if (!context.IsValid)
        {
            return;
        }

        SetRamp(context.Handle, _original);
    }

    private void SetRamp(IntPtr hdc, ushort[] ramp)
    {
        GCHandle handle = GCHandle.Alloc(ramp, GCHandleType.Pinned);
        try
        {
            _ = NativeMethods.SetDeviceGammaRamp(hdc, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }
}

internal sealed class DisplayContext : IDisposable
{
    private readonly IntPtr _hdc;

    public DisplayContext(string deviceName)
    {
        _hdc = NativeMethods.CreateDC(deviceName, deviceName, null, IntPtr.Zero);
    }

    public IntPtr Handle => _hdc;
    public bool IsValid => _hdc != IntPtr.Zero;

    public void Dispose()
    {
        if (_hdc != IntPtr.Zero)
        {
            _ = NativeMethods.DeleteDC(_hdc);
        }
    }
}
