using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Zwmenu.Services;

public sealed class AppVolume
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public float Level { get; init; }
    public bool Mute { get; init; }
}

public sealed class ScreenLamp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Level { get; init; }
    public bool CanSet { get; init; }
}

/// <summary>Звук, яркость и питание этого компьютера. Для второго уходит через WorkshopHub.</summary>
public static class DeskControl
{
    public static float GetMasterVolume()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch
        {
            return 0.5f;
        }
    }

    public static bool GetMasterMute()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioEndpointVolume.Mute;
        }
        catch
        {
            return false;
        }
    }

    public static void SetMasterVolume(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = level;
        }
        catch
        {
            // устройство могло исчезнуть
        }
    }

    public static void SetMasterMute(bool mute)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = mute;
        }
        catch
        {
            // устройство могло исчезнуть
        }
    }

    public static IReadOnlyList<AppVolume> ListAppVolumes()
    {
        var list = new List<AppVolume>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session == null)
                    continue;
                string name = NiceAppName(session.DisplayName);
                if (name.Length == 0)
                {
                    try
                    {
                        uint pid = session.GetProcessID;
                        if (pid != 0)
                        {
                            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                            name = proc.ProcessName;
                        }
                    }
                    catch
                    {
                        name = "Приложение";
                    }
                }

                if (name.Length == 0)
                    name = "Приложение";
                // Служебные и путь вместо имени не показываем.
                if (name.StartsWith('%') || name.Contains('\\') || name.Contains('/')
                    || name.Contains("SystemRoot", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("System32", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        uint pid = session.GetProcessID;
                        if (pid != 0)
                        {
                            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                            if (!string.IsNullOrWhiteSpace(proc.ProcessName))
                                name = proc.ProcessName;
                        }
                    }
                    catch { /* оставим как есть */ }
                }

                string id = session.GetSessionIdentifier ?? name + ":" + i;
                list.Add(new AppVolume
                {
                    Id = id,
                    Name = name,
                    Level = session.SimpleAudioVolume.Volume,
                    Mute = session.SimpleAudioVolume.Mute
                });
            }
        }
        catch
        {
            // сессии недоступны
        }

        return list
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(18)
            .ToList();
    }

    static string NiceAppName(string? raw)
    {
        string name = (raw ?? "").Trim();
        if (name.Length == 0)
            return "";
        if (name.StartsWith('%') || name.Contains('\\') || name.Contains('/'))
            return "";
        return name;
    }

    public static void SetAppVolume(string id, float level, bool? mute = null)
    {
        level = Math.Clamp(level, 0f, 1f);
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session == null)
                    continue;
                string sid = session.GetSessionIdentifier ?? "";
                string name = session.DisplayName?.Trim() ?? "";
                if (!string.Equals(sid, id, StringComparison.Ordinal)
                    && !string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
                    continue;
                session.SimpleAudioVolume.Volume = level;
                if (mute != null)
                    session.SimpleAudioVolume.Mute = mute.Value;
                return;
            }
        }
        catch
        {
            // сессия уже закрыта
        }
    }

    public static IReadOnlyList<ScreenLamp> ListScreens()
    {
        try
        {
            var wmi = ListScreensWmi();
            if (wmi.Count > 0)
                return wmi;
        }
        catch
        {
            // System.Management мог не попасть в установку, или WMI закрыт
        }

        var list = new List<ScreenLamp>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            string name = screen.Primary ? "Главный монитор" : "Монитор " + screen.DeviceName;
            list.Add(new ScreenLamp
            {
                Id = "disp:" + screen.DeviceName,
                Name = name,
                Level = 50,
                CanSet = false
            });
        }

        return list;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static List<ScreenLamp> ListScreensWmi()
    {
        var list = new List<ScreenLamp>();
        using var search = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
        int index = 0;
        foreach (System.Management.ManagementBaseObject item in search.Get())
        {
            using (item)
            {
                byte level = 50;
                try { level = Convert.ToByte(item["CurrentBrightness"]); } catch { /* ignore */ }
                list.Add(new ScreenLamp
                {
                    Id = "wmi:" + index,
                    Name = index == 0 ? "Встроенный экран" : "Экран " + (index + 1),
                    Level = level,
                    CanSet = true
                });
                index++;
            }
        }

        return list;
    }

    public static void SetBrightness(string id, int level)
    {
        level = Math.Clamp(level, 0, 100);
        if (id.StartsWith("wmi:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(id.AsSpan(4), out int index))
        {
            try { SetBrightnessWmi(index, level); return; }
            catch
            {
                // System.Management нет или монитор не умеет
            }
        }

        TryPhysicalBrightness(level);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void SetBrightnessWmi(int index, int level)
    {
        using var search = new System.Management.ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
        int i = 0;
        foreach (System.Management.ManagementObject item in search.Get())
        {
            using (item)
            {
                if (i == index)
                {
                    item.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, (byte)level });
                    return;
                }

                i++;
            }
        }
    }

    public static void SetAllBrightness(int level)
    {
        level = Math.Clamp(level, 0, 100);
        foreach (var screen in ListScreens())
        {
            if (screen.CanSet)
                SetBrightness(screen.Id, level);
        }

        TryPhysicalBrightness(level);
    }

    static void TryPhysicalBrightness(int level)
    {
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref Rect r, IntPtr data) =>
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                    return true;
                var monitors = new PhysicalMonitor[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    return true;
                try
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (GetMonitorBrightness(monitors[i].Handle, out _, out _, out _))
                            SetMonitorBrightness(monitors[i].Handle, (uint)level);
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, monitors);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // внешний монитор без DDC
        }
    }

    public static string SleepLocal()
    {
        try
        {
            if (!SetSuspendState(false, false, false))
                return "Сон не включился.";
            return "Ухожу в сон.";
        }
        catch (Exception ex)
        {
            return "Сон не включился: " + ex.Message;
        }
    }

    public static string RebootLocal()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = "/r /t 1",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            return "Перезагрузка через секунду.";
        }
        catch (Exception ex)
        {
            return "Перезагрузка не стартовала: " + ex.Message;
        }
    }

    public static string ShutdownLocal()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown",
                Arguments = "/s /t 1",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            return "Выключение через секунду.";
        }
        catch (Exception ex)
        {
            return "Выключение не стартовало: " + ex.Message;
        }
    }

    public static string SleepBoth()
    {
        bool sent = WorkshopHub.Send(WorkshopHub.PowerSleep, ReadOnlySpan<byte>.Empty);
        string local = SleepLocal();
        return sent ? "Обоим: сон. " + local : "Второму не ушло. " + local;
    }

    public static string RebootBoth()
    {
        bool sent = WorkshopHub.Send(WorkshopHub.PowerReboot, ReadOnlySpan<byte>.Empty);
        string local = RebootLocal();
        return sent ? "Обоим: перезагрузка. " + local : "Второму не ушло. " + local;
    }

    public static string ShutdownBoth()
    {
        bool sent = WorkshopHub.Send(WorkshopHub.PowerOff, ReadOnlySpan<byte>.Empty);
        string local = ShutdownLocal();
        return sent ? "Обоим: выключение. " + local : "Второму не ушло. " + local;
    }

    public static void ApplyPeerVolume(float level, bool mute)
    {
        SetMasterVolume(level);
        SetMasterMute(mute);
    }

    public static void ApplyPeerBrightness(int level) => SetAllBrightness(level);

    /// <summary>Ставит громкость здесь и, если связь есть, на втором. Честный текст результата.</summary>
    public static string SetMasterBoth(float level, bool mute)
    {
        level = Math.Clamp(level, 0f, 1f);
        SetMasterVolume(level);
        SetMasterMute(mute);
        string here = mute ? "Тишина здесь." : ("Громкость здесь " + ((int)Math.Round(level * 100)) + "%.");
        if (!EdgeShareService.Shared.Linked)
            return here + " Второй не на связи.";
        if (!WorkshopHub.SendVolumeToPeer(level, mute))
            return here + " Второму не ушло.";
        return mute
            ? "Тишина на обоих."
            : ("Громкость " + ((int)Math.Round(level * 100)) + "% на обоих.");
    }

    public static string SetBrightnessBoth(int level)
    {
        level = Math.Clamp(level, 0, 100);
        SetAllBrightness(level);
        string here = "Яркость здесь " + level + "%.";
        if (!EdgeShareService.Shared.Linked)
            return here + " Второй не на связи.";
        if (!WorkshopHub.SendBrightnessToPeer(level))
            return here + " Второму не ушло.";
        return "Яркость " + level + "% на обоих.";
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    static extern bool SetMonitorBrightness(IntPtr monitor, uint value);

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
