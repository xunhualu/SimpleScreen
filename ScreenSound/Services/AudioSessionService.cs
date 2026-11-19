using ScreenSound.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Diagnostics;

namespace ScreenSound.Services;

public class AudioSessionService
{
    public List<AudioSessionInfo> GetActiveSessions()
    {
        // One entry per PID. Windows keeps an AudioSessionControl on *every*
        // render endpoint a process has ever touched — most of them Inactive
        // with a stale device. Taking the first one we happen to enumerate (as
        // before) can report FxSound / an unused HDMI endpoint as the app's
        // "current" device, so RoutingEngine's drift check compares against a
        // device the stream never actually used and re-applies forever.
        //
        // Pick instead: Active state first, then the session with the louder
        // meter, which is the one actually carrying sound right now.
        var byPid = new Dictionary<uint, Candidate>();

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            // One COM wrapper per endpoint per routing cycle — don't leak it.
            using (device)
            {
                try
                {
                    var sessionEnumerator = device.AudioSessionManager.Sessions;

                    for (int i = 0; i < sessionEnumerator.Count; i++)
                    {
                        var session = sessionEnumerator[i];
                        var pid = (uint)session.GetProcessID;
                        if (pid == 0) // system sounds
                            continue;

                        bool isActive = session.State == AudioSessionState.AudioSessionStateActive;
                        float peak;
                        try { peak = session.AudioMeterInformation.MasterPeakValue; }
                        catch { peak = 0f; }

                        if (byPid.TryGetValue(pid, out var current))
                        {
                            if ((isActive && !current.IsActive) ||
                                (isActive == current.IsActive && peak > current.Peak))
                                byPid[pid] = new Candidate(session, pid, device, isActive, peak);
                            continue;
                        }

                        byPid[pid] = new Candidate(session, pid, device, isActive, peak);
                    }
                }
                catch
                {
                    // Device may become unavailable during enumeration
                }
            }
        }

        return byPid.Values.Select(c => c.Info).ToList();
    }

    private sealed class Candidate
    {
        public bool IsActive { get; }
        public float Peak { get; }
        public AudioSessionInfo Info { get; }

        public Candidate(AudioSessionControl session, uint pid, MMDevice device, bool isActive, float peak)
        {
            IsActive = isActive;
            Peak = peak;

            string processName;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch
            {
                processName = "Unknown";
            }

            Info = new AudioSessionInfo
            {
                ProcessId = pid,
                ProcessName = processName,
                DisplayName = string.IsNullOrEmpty(session.DisplayName) ? processName : session.DisplayName,
                AudioDeviceId = device.ID,
                AudioDeviceName = device.FriendlyName
            };
        }
    }
}
