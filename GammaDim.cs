using System.Runtime.InteropServices;
using System.Management;

namespace ScreenBlank;

internal sealed class GammaDim
{
    private readonly List<DisplayRamp> _displays = [];
    private readonly List<byte> _originalBrightness = [];
    private bool _dimmed;

    public void Apply()
    {
        EnumerateDisplays();
        if (_displays.Count == 0)
        {
            return;
        }

        foreach (DisplayRamp display in _displays)
        {
            if (display.SaveOriginal())
            {
                display.ApplyDark();
            }
        }
        SaveBrightness();
        SetBacklightBrightness(1);
        _dimmed = true;
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
        RestoreBrightness();
        _dimmed = false;
    }

    private void SaveBrightness()
    {
        _originalBrightness.Clear();
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
                        _originalBrightness.Add(value);
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private void SetBacklightBrightness(byte level)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                new ManagementScope(@"\\.\root\wmi"),
                new WqlObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
            foreach (ManagementObject obj in searcher.Get())
            {
                ManagementBaseObject? parameters = obj.GetMethodParameters("WmiSetBrightness");
                if (parameters is null)
                {
                    continue;
                }
                parameters["Brightness"] = level;
                parameters["Timeout"] = 1;
                using (obj)
                {
                    _ = obj.InvokeMethod("WmiSetBrightness", parameters, null);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private void RestoreBrightness()
    {
        if (_originalBrightness.Count == 0)
        {
            return;
        }

        try
        {
            using ManagementObjectSearcher searcher = new(
                new ManagementScope(@"\\.\root\wmi"),
                new WqlObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
            int index = 0;
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj)
                {
                    if (index >= _originalBrightness.Count)
                    {
                        break;
                    }
                    byte original = _originalBrightness[index++];
                    ManagementBaseObject? parameters = obj.GetMethodParameters("WmiSetBrightness");
                    if (parameters is null)
                    {
                        continue;
                    }
                    parameters["Brightness"] = original;
                    parameters["Timeout"] = 1;
                    _ = obj.InvokeMethod("WmiSetBrightness", parameters, null);
                }
            }
        }
        catch (Exception)
        {
        }
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
