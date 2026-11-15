using ScreenSound.Interop;
using ScreenSound.Models;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace ScreenSound.Services;

/// <summary>
/// Per-monitor brightness control.
///
/// Two transport paths, tried in order:
///  1. DDC/CI (VCP code 0x10) — works for the vast majority of external monitors.
///  2. WMI (WmiMonitorBrightness / WmiMonitorBrightnessMethods) — laptop
///     integrated panels don't speak DDC/CI over their internal connectors,
///     but always expose WMI on machines with a controllable backlight.
///
/// All hardware I/O happens on background threads; DDC/CI round-trips take
/// 50–200 ms per call and must never run on the UI thread.
/// </summary>
public class BrightnessService : IDisposable
{
    /// <summary>One controllable brightness channel tied to a logical monitor.</summary>
    public class BrightnessMonitor
    {
        public required MonitorInfo Monitor { get; init; }
        public string Transport { get; init; } = "";   // "DDC/CI" or "WMI"
        public IntPtr PhysicalHandle { get; init; }     // DDC/CI only
        public string Description { get; init; } = "";
    }

    // HMONITOR -> physical monitor handle cache. Rebuilt on RefreshMonitors.
    private readonly Dictionary<IntPtr, List<BrightnessMonitor>> _byHMonitor = new();
    private readonly object _lock = new();
    private static readonly Dictionary<IntPtr, string?> _wmiInstanceByHMonitor = new();

    /// <summary>
    /// Enumerate controllable monitors. Must be called from any thread; safe
    /// concurrently. Monitors with no controllable path (rare virtual displays)
    /// are omitted.
    /// </summary>
    public List<BrightnessMonitor> RefreshMonitors(IEnumerable<MonitorInfo> monitors)
    {
        lock (_lock)
        {
            DisposeHandles();
            _byHMonitor.Clear();
            _wmiInstanceByHMonitor.Clear();

            var wmiChecked = false;
            var wmiAvailable = false;

            foreach (var monitor in monitors)
            {
                var list = new List<BrightnessMonitor>();

                // Path 1: DDC/CI via physical monitor handles.
                    try
                    {
                        uint count = 0;
                        if (MonitorInterop.GetNumberOfPhysicalMonitorsFromHMONITOR(
                                monitor.Handle, ref count) && count > 0)
                        {
                            var phys = new MonitorInterop.PHYSICAL_MONITOR[count];
                            if (MonitorInterop.GetPhysicalMonitorsFromHMONITOR(monitor.Handle, count, phys))
                            {
                                foreach (var pm in phys)
                                {
                                    if (pm.hPhysicalMonitor == IntPtr.Zero) continue;

                                    // Probe: can we read brightness via DDC/CI?
                                    var vct = MonitorInterop.LPMC_VCP_CODE_TYPE.MC_SET_PARAMETER;
                                    bool ddcOk;
                                    MonitorInterop.DdcBus.Wait();
                                    try
                                    {
                                        ddcOk = MonitorInterop.GetVCPFeatureAndVCPFeatureReply(
                                            pm.hPhysicalMonitor, MonitorInterop.VCP_CODE_BRIGHTNESS,
                                            ref vct, out _, out _);
                                    }
                                    finally { MonitorInterop.DdcBus.Release(); }
                                    if (ddcOk)
                                    {
                                        list.Add(new BrightnessMonitor
                                        {
                                            Monitor = monitor,
                                            Transport = "DDC/CI",
                                            PhysicalHandle = pm.hPhysicalMonitor,
                                            Description = pm.szPhysicalMonitorDescription ?? ""
                                        });
                                    }
                                    else
                                    {
                                        // Not DDC-capable; release the handle.
                                        MonitorInterop.DestroyPhysicalMonitor(pm.hPhysicalMonitor);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Brightness] DDC probe failed for {monitor.DeviceName}: {ex.Message}");
                    }

                // Path 2: WMI (laptop panel). The WMI backlight instance is
                // machine-global; map it to the primary (or first) monitor
                // whose DDC probe failed.
                if (list.Count == 0 && !wmiAvailable)
                {
                    if (!wmiChecked)
                    {
                        wmiChecked = true;
                        wmiAvailable = ProbeWmi();
                    }
                    if (wmiAvailable && (monitor.IsPrimary || _wmiInstanceByHMonitor.Count == 0))
                    {
                        _wmiInstanceByHMonitor[monitor.Handle] = null; // marker: this monitor owns the WMI path
                        list.Add(new BrightnessMonitor
                        {
                            Monitor = monitor,
                            Transport = "WMI",
                            Description = "Integrated display"
                        });
                    }
                }

                if (list.Count > 0)
                    _byHMonitor[monitor.Handle] = list;
            }

            return _byHMonitor.Values.SelectMany(v => v).ToList();
        }
    }

    private static bool ProbeWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI", "SELECT * FROM WmiMonitorBrightness");
            return searcher.Get().Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Read brightness 0-100 for one channel, or null on failure.</summary>
    public async Task<int?> GetBrightnessAsync(BrightnessMonitor bm)
    {
        return await Task.Run(() =>
        {
            if (bm.Transport == "DDC/CI")
            {
                MonitorInterop.DdcBus.Wait();
                try
                {
                    var vct = MonitorInterop.LPMC_VCP_CODE_TYPE.MC_SET_PARAMETER;
                    if (MonitorInterop.GetVCPFeatureAndVCPFeatureReply(
                            bm.PhysicalHandle, MonitorInterop.VCP_CODE_BRIGHTNESS,
                            ref vct, out uint current, out uint max) && max > 0)
                    {
                        return (int)Math.Round(current * 100.0 / max);
                    }
                }
                finally { MonitorInterop.DdcBus.Release(); }
                return (int?)null;
            }

            // WMI
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (var o in searcher.Get())
                {
                    var v = Convert.ToInt32(o["CurrentBrightness"]);
                    o.Dispose();
                    return v;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Brightness] WMI read failed: {ex.Message}");
            }
            return (int?)null;
        });
    }

    /// <summary>Set brightness 0-100 for one channel. Returns true on success.</summary>
    public async Task<bool> SetBrightnessAsync(BrightnessMonitor bm, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return await Task.Run(() =>
        {
            if (bm.Transport == "DDC/CI")
            {
                MonitorInterop.DdcBus.Wait();
                try
                {
                    return MonitorInterop.SetVCPFeature(
                        bm.PhysicalHandle, MonitorInterop.VCP_CODE_BRIGHTNESS, (uint)percent);
                }
                finally { MonitorInterop.DdcBus.Release(); }
            }

            // WMI
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject o in searcher.Get())
                {
                    o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                    o.Dispose();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Brightness] WMI write failed: {ex.Message}");
            }
            return false;
        });
    }

    /// <summary>
    /// Some panels (budget HKC/HIV QHD units, for one) ACK a DDC/CI brightness
    /// write but simply keep their own level — reads work, contrast works, only
    /// the brightness VCP is write-ignored. "SetVCPFeature returned true" is
    /// therefore worthless as a capability signal. This probe nudges the panel
    /// by 3 % and watches for the level to actually move; the nudge is far too
    /// small to notice and is reverted immediately.
    /// </summary>
    public async Task<bool> VerifyWriteSupportAsync(BrightnessMonitor bm)
    {
        var original = await GetBrightnessAsync(bm);
        if (original == null) return false;

        var probe = original.Value <= 2 ? original.Value + 3 : original.Value - 3;
        if (!await SetBrightnessAsync(bm, probe)) return false;

        for (var i = 0; i < 7; i++)
        {
            await Task.Delay(200);
            var now = await GetBrightnessAsync(bm);
            if (now == probe)
            {
                await SetBrightnessAsync(bm, original.Value);
                return true;
            }
        }

        // Never reached the probed level → the panel ignores brightness writes.
        await SetBrightnessAsync(bm, original.Value);   // best effort; likely ignored too
        return false;
    }

    private void DisposeHandles()
    {
        foreach (var list in _byHMonitor.Values)
        {
            foreach (var bm in list)
            {
                if (bm.PhysicalHandle != IntPtr.Zero)
                {
                    try { MonitorInterop.DestroyPhysicalMonitor(bm.PhysicalHandle); }
                    catch { }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeHandles();
            _byHMonitor.Clear();
        }
        GC.SuppressFinalize(this);
    }
}
