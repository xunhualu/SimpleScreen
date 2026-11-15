using ScreenSound.Interop;
using ScreenSound.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ScreenSound.Services;

/// <summary>
/// Generic DDC/CI (VCP) access for the screen-details page: contrast (0x12),
/// speaker volume (0x62), input source (0x60), color preset (0x14), plus the
/// capabilities string used to discover which codes and values a panel
/// actually declares. Brightness stays in <see cref="BrightnessService"/>.
/// </summary>
public class VcpService : IDisposable
{
    private class Channel
    {
        public required IntPtr Handle;
        public required string Description;
        public string? Capabilities;
    }

    // DeviceName (\\.\DISPLAYx) -> physical handle. Rebuilt on Refresh().
    private readonly ConcurrentDictionary<string, Channel> _channels = new();

    // DDC transactions must never overlap. A dual-cable setup (e.g. one panel
    // wired over both HDMI and USB-C) yields two physical-monitor handles that
    // end at the SAME I2C bus; concurrent GetVCPFeatureAndVCPFeatureReply calls
    // then collide, reads time out, and the details page wrongly marks codes as
    // "not supported". Serialize every transaction app-wide (shared with
    // BrightnessService via MonitorInterop.DdcBus).

    /// <summary>One DDC read transaction with retries (bus NACKs are transient).</summary>
    private (uint Current, uint Max)? ReadVcp(Channel ch, byte vcp)
    {
        var vct = MonitorInterop.LPMC_VCP_CODE_TYPE.MC_SET_PARAMETER;
        for (var attempt = 0; ; attempt++)
        {
            if (MonitorInterop.GetVCPFeatureAndVCPFeatureReply(
                    ch.Handle, vcp, ref vct, out var cur, out var max))
                return (cur, max);
            if (attempt >= 2) return null;
            Thread.Sleep(70 * (attempt + 1));   // let the panel's scaler breathe
        }
    }

    /// <summary>One DDC write transaction with one retry.</summary>
    private bool WriteVcp(Channel ch, byte vcp, uint value)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (MonitorInterop.SetVCPFeature(ch.Handle, vcp, value)) return true;
            if (attempt >= 1) return false;
            Thread.Sleep(80);
        }
    }

    /// <summary>Rebuild the handle cache for the current monitor set.</summary>
    public void Refresh(IEnumerable<MonitorInfo> monitors)
    {
        var old = _channels.ToArray();
        _channels.Clear();

        foreach (var monitor in monitors)
        {
            try
            {
                uint count = 0;
                if (!MonitorInterop.GetNumberOfPhysicalMonitorsFromHMONITOR(
                        monitor.Handle, ref count) || count == 0) continue;

                var phys = new MonitorInterop.PHYSICAL_MONITOR[count];
                if (!MonitorInterop.GetPhysicalMonitorsFromHMONITOR(monitor.Handle, count, phys))
                    continue;

                // One handle per logical monitor; extras are destroyed.
                for (var i = 0; i < phys.Length; i++)
                {
                    if (phys[i].hPhysicalMonitor == IntPtr.Zero) continue;
                    if (i == 0)
                    {
                        _channels[monitor.DeviceName] = new Channel
                        {
                            Handle = phys[i].hPhysicalMonitor,
                            Description = phys[i].szPhysicalMonitorDescription ?? ""
                        };
                    }
                    else
                    {
                        MonitorInterop.DestroyPhysicalMonitor(phys[i].hPhysicalMonitor);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Vcp] enumerate failed for {monitor.DeviceName}: {ex.Message}");
            }
        }

        foreach (var kv in old)
        {
            try { MonitorInterop.DestroyPhysicalMonitor(kv.Value.Handle); } catch { }
        }
    }

    /// <summary>
    /// MCCS capabilities string of the panel, cached after first read.
    /// e.g. "…60(0F 11 21)14(05 04)…" → input/preset value discovery.
    /// </summary>
    public string? GetCapabilities(MonitorInfo monitor)
    {
        if (!_channels.TryGetValue(monitor.DeviceName, out var ch)) return null;
        MonitorInterop.DdcBus.Wait();
        try
        {
            ch.Capabilities ??= MonitorInterop.GetCapabilitiesString(ch.Handle);
        }
        finally { MonitorInterop.DdcBus.Release(); }
        return ch.Capabilities;
    }

    /// <summary>Parse the allowed values of one VCP code out of the caps string. Empty when absent.</summary>
    public static List<uint> ParseCapValues(string? caps, byte vcp)
    {
        var result = new List<uint>();
        if (string.IsNullOrEmpty(caps)) return result;
        var key = vcp.ToString("X2");
        var idx = caps.IndexOf('(');
        // caps format: "type(vcp)…60(0F 11 21)14(05 04)…"
        var search = key + "(";
        var pos = caps.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return result;
        pos += search.Length;
        var end = caps.IndexOf(')', pos);
        if (end < 0) end = caps.Length;
        foreach (var tok in caps.Substring(pos, end - pos).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (uint.TryParse(tok, System.Globalization.NumberStyles.HexNumber, null, out var v))
                result.Add(v);
        }
        return result;
    }

    /// <summary>Read one VCP code. Null on failure (code absent, no DDC, timeout).</summary>
    public async Task<(uint Current, uint Max)?> GetVcpAsync(MonitorInfo monitor, byte vcp)
    {
        return await Task.Run(async () =>
        {
            await MonitorInterop.DdcBus.WaitAsync();
            try
            {
                if (!_channels.TryGetValue(monitor.DeviceName, out var ch)) return null;
                return ReadVcp(ch, vcp);
            }
            finally { MonitorInterop.DdcBus.Release(); }
        });
    }

    /// <summary>Write one VCP code. Returns true when the command was ACKed.</summary>
    public async Task<bool> SetVcpAsync(MonitorInfo monitor, byte vcp, uint value)
    {
        return await Task.Run(async () =>
        {
            await MonitorInterop.DdcBus.WaitAsync();
            try
            {
                if (!_channels.TryGetValue(monitor.DeviceName, out var ch)) return false;
                return WriteVcp(ch, vcp, value);
            }
            finally { MonitorInterop.DdcBus.Release(); }
        });
    }

    /// <summary>
    /// Read back a code after writing a value that differs from the current
    /// one. Cheap panels ACK every write but silently ignore codes they don't
    /// implement (0x87 sharpness on this machine answers "0/0" and snaps back),
    /// so a successful <c>SetVCPFeature</c> alone proves nothing.
    /// Always restores the original value.
    /// </summary>
    public async Task<bool> ProbeWritableAsync(MonitorInfo monitor, byte vcp, uint current, uint max)
    {
        var test = current == 0 ? 1u : 0u;
        if (max > 0 && test > max) test = max;
        try
        {
            if (!await SetVcpAsync(monitor, vcp, test)) return false;
            await Task.Delay(160);
            var back = await GetVcpAsync(monitor, vcp);
            return back.HasValue && back.Value.Current == test;
        }
        finally
        {
            // Whatever happened above, never leave the panel on the probe value.
            await SetVcpAsync(monitor, vcp, current);
        }
    }

    /// <summary>
    /// Which values of a discrete code actually stick. Panels commonly declare
    /// 1–13 as a maximum while only accepting three of them (this machine snaps
    /// every non-supported preset back to the active one), which would leave a
    /// dropdown full of dead entries. Slow (~150 ms per candidate) — call it
    /// once per panel and cache the result.
    /// </summary>
    public async Task<List<uint>> ProbeDiscreteValuesAsync(
        MonitorInfo monitor, byte vcp, uint current, uint max)
    {
        var valid = new List<uint>();
        var limit = Math.Min(max, 32);
        try
        {
            for (uint v = 0; v <= limit; v++)
            {
                if (!await SetVcpAsync(monitor, vcp, v)) continue;
                await Task.Delay(120);
                var back = await GetVcpAsync(monitor, vcp);
                if (back.HasValue && back.Value.Current == v) valid.Add(v);
            }
        }
        finally
        {
            // Whatever happened above, never leave the panel on the last probed value.
            await SetVcpAsync(monitor, vcp, current);
        }
        return valid;
    }

    /// <summary>Drop every cached capabilities string (monitor re-plugged).</summary>
    public void InvalidateCapabilities()
    {
        foreach (var ch in _channels.Values) ch.Capabilities = null;
    }

    public void Dispose()
    {
        foreach (var kv in _channels)
        {
            try { MonitorInterop.DestroyPhysicalMonitor(kv.Value.Handle); } catch { }
        }
        _channels.Clear();
        GC.SuppressFinalize(this);
    }
}
