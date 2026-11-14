using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Zwmenu.Services;

public sealed class StartupItem
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Where { get; init; } = "";
    public bool CanDisable { get; init; }
    public string Kind { get; init; } = "";
    public string Key { get; init; } = "";
}

public sealed class LanPeer
{
    public string Address { get; init; } = "";
    public string Mac { get; init; } = "";
    public string Kind { get; init; } = "";
}

public sealed class DeviceUse
{
    public string Device { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Active { get; init; }
}

public sealed class HeavyFile
{
    public string Path { get; init; } = "";
    public long Bytes { get; init; }

    public string Label => MachineDesk.FormatBytes(Bytes) + "  " + Path;
}

/// <summary>Уход, сеть, автозагрузка, микрофон, камера, место. Удаление только в корзину и только из профиля.</summary>
public static class MachineDesk
{
    const string MicKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    const string CamKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const long HeavyBytes = 30L * 1024 * 1024;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly Regex ArpLine = new(@"(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})", RegexOptions.Compiled);
    static bool _weMuted;

    [DllImport("user32.dll")]
    static extern bool LockWorkStation();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    static extern int SHFileOperation(ref ShFileOp op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int Aborted;
        public IntPtr Mappings;
        public IntPtr Title;
    }

    public static string Leave(bool tellPeer)
    {
        var lines = new List<string>();
        StayAwake.SetManual(false);
        if (tellPeer)
        {
            bool sent = EdgeShareService.Shared.Linked && WorkshopHub.Send(WorkshopHub.Away, ReadOnlySpan<byte>.Empty);
            lines.Add(sent
                ? "Второму тоже отправил уход."
                : "Второй не на связи. Закрываю только этот.");
        }

        try
        {
            EdgeShareService.Shared.PauseTransfers();
            lines.Add("Незаконченные передачи останавливаю.");
        }
        catch
        {
            lines.Add("Передачи не остановились.");
        }

        lines.Add(MuteDown());
        try
        {
            if (!LockWorkStation())
                lines.Add("Экран не закрылся.");
        }
        catch (Exception ex)
        {
            lines.Add("Экран не закрылся: " + ex.Message);
        }

        return string.Join(" ", lines);
    }

    public static string RestoreSound()
    {
        if (!_weMuted)
            return "Звук я не выключал.";
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = false;
            _weMuted = false;
            return "Звук включён.";
        }
        catch (Exception ex)
        {
            return "Звук не включился: " + ex.Message;
        }
    }

    public static IReadOnlyList<LanPeer> ScanLan()
    {
        try
        {
            var psi = new ProcessStartInfo("arp", "-a")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(psi);
            if (process == null)
                return Array.Empty<LanPeer>();
            string text = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(true); } catch { /* arp завис */ }
            }

            return ParseArp(text);
        }
        catch
        {
            return Array.Empty<LanPeer>();
        }
    }

    public static IReadOnlyList<LanPeer> ParseArp(string text)
    {
        var list = new List<LanPeer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ArpLine.Matches(text ?? ""))
        {
            string ip = match.Groups[1].Value;
            if (ip.StartsWith("224.", StringComparison.Ordinal) || ip.StartsWith("239.", StringComparison.Ordinal) || ip.StartsWith("255.", StringComparison.Ordinal))
                continue;
            if (!seen.Add(ip))
                continue;
            string mac = match.Groups[2].Value.ToLowerInvariant();
            if (mac == "ff-ff-ff-ff-ff-ff")
                continue;
            list.Add(new LanPeer { Address = ip, Mac = mac, Kind = mac.StartsWith("01-00-5e", StringComparison.Ordinal) ? "служебный" : "устройство" });
        }

        return list;
    }

    public static IReadOnlyList<StartupItem> Startup()
    {
        var list = new List<StartupItem>();
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (run != null)
            {
                foreach (string name in run.GetValueNames())
                {
                    string command = run.GetValue(name)?.ToString() ?? "";
                    list.Add(new StartupItem
                    {
                        Name = name,
                        Command = command,
                        Where = "твоя автозагрузка",
                        CanDisable = true,
                        Kind = "run",
                        Key = name
                    });
                }
            }
        }
        catch { /* раздел не открылся */ }

        AddFolder(list, Environment.SpecialFolder.Startup, "папка автозагрузки", canDisable: true);
        AddFolder(list, Environment.SpecialFolder.CommonStartup, "для всех пользователей", canDisable: false);

        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(RunKey, false);
            if (machine != null)
            {
                foreach (string name in machine.GetValueNames())
                {
                    list.Add(new StartupItem
                    {
                        Name = name,
                        Command = machine.GetValue(name)?.ToString() ?? "",
                        Where = "для всех, только просмотр",
                        CanDisable = false,
                        Kind = "machine",
                        Key = name
                    });
                }
            }
        }
        catch { /* без прав администратора этого списка нет */ }

        return list;
    }

    public static string DisableStartup(StartupItem item)
    {
        if (item == null || !item.CanDisable)
            return "Эту запись убрать нельзя. Она для всех пользователей.";
        try
        {
            if (item.Kind == "run")
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, true)
                    ?? throw new InvalidOperationException("Раздел автозагрузки не открылся.");
                run.DeleteValue(item.Key, throwOnMissingValue: false);
                Remember(new Parked { Kind = "run", Name = item.Name, Command = item.Command });
                return "«" + item.Name + "» убрана. Вернуть можно кнопкой ниже.";
            }

            if (item.Kind == "folder" && File.Exists(item.Key))
            {
                string parked = ParkPath(Path.GetFileName(item.Key));
                File.Move(item.Key, parked);
                Remember(new Parked { Kind = "folder", Name = item.Name, OriginalPath = item.Key, ParkedPath = parked });
                return "Ярлык убран из папки. Вернуть можно кнопкой ниже.";
            }

            return "Эту запись уже нет на месте.";
        }
        catch (Exception ex)
        {
            return "Не убрал: " + ex.Message;
        }
    }

    public static string RestoreStartup()
    {
        var parked = ReadParked();
        if (parked.Count == 0)
            return "Выключенных записей нет.";
        int back = 0;
        var left = new List<Parked>();
        foreach (var item in parked)
        {
            try
            {
                if (item.Kind == "run" && !string.IsNullOrWhiteSpace(item.Name))
                {
                    using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
                    run?.SetValue(item.Name, item.Command ?? "");
                    back++;
                }
                else if (item.Kind == "folder" && File.Exists(item.ParkedPath) && !string.IsNullOrWhiteSpace(item.OriginalPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                    if (!File.Exists(item.OriginalPath))
                        File.Move(item.ParkedPath, item.OriginalPath);
                    back++;
                }
                else
                {
                    left.Add(item);
                }
            }
            catch
            {
                left.Add(item);
            }
        }

        WriteParked(left);
        return "Вернул записей: " + back + ".";
    }

    public static IReadOnlyList<DeviceUse> Devices()
    {
        var list = new List<DeviceUse>();
        WalkDevice(list, "Микрофон", MicKey);
        WalkDevice(list, "Камера", CamKey);
        return list;
    }

    public static (bool Mic, bool Cam) ClosedDevices() => (IsDeny(MicKey), IsDeny(CamKey));

    public static string SetDeviceLock(bool micClosed, bool camClosed)
    {
        var lines = new List<string>
        {
            SetOne(MicKey, micClosed, "Микрофон"),
            SetOne(CamKey, camClosed, "Камера")
        };
        lines.Add("Уже открытая программа может держать устройство, пока её не закроешь.");
        return string.Join(" ", lines);
    }

    public static IReadOnlyList<HeavyFile> FindHeavy(CancellationToken ct)
    {
        var found = new List<HeavyFile>();
        foreach (string root in HeavyRoots())
        {
            if (ct.IsCancellationRequested)
                break;
            if (!Directory.Exists(root))
                continue;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (string path in Directory.EnumerateFiles(root, "*", options))
                {
                    if (ct.IsCancellationRequested)
                        break;
                    if (path.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase)
                        || path.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase)
                        || path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        long size = new FileInfo(path).Length;
                        if (size >= HeavyBytes && CanRecycle(path))
                            found.Add(new HeavyFile { Path = path, Bytes = size });
                    }
                    catch { /* файл занят */ }
                }
            }
            catch { /* папка не читается */ }
        }

        return found.OrderByDescending(f => f.Bytes).Take(80).ToList();
    }

    public static bool CanRecycle(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full))
                return false;
            string profile = ProfileRoot();
            if (!full.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
                return false;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && full.StartsWith(Path.GetFullPath(windows), StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Recycle(IReadOnlyList<string> paths)
    {
        var ok = new List<string>();
        int refused = 0;
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) || !CanRecycle(path))
            {
                refused++;
                continue;
            }

            ok.Add(Path.GetFullPath(path));
        }

        if (ok.Count == 0)
            return refused > 0 ? "Эти файлы удалять нельзя." : "Ничего не отмечено.";

        string packed = string.Join("\0", ok) + "\0\0";
        byte[] bytes = Encoding.Unicode.GetBytes(packed);
        IntPtr buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var op = new ShFileOp
            {
                Func = 3,
                From = buffer,
                Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400
            };
            int code = SHFileOperation(ref op);
            if (code != 0 || op.Aborted != 0)
                return "Корзина не приняла файлы (код " + code + ").";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return "В корзину: " + ok.Count + "." + (refused > 0 ? " Пропущено: " + refused + "." : "");
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return (bytes / (1024d * 1024 * 1024)).ToString("0.0") + " ГБ";
        if (bytes >= 1024L * 1024)
            return (bytes / (1024d * 1024)).ToString("0") + " МБ";
        return (bytes / 1024d).ToString("0") + " КБ";
    }

    public static void SelfCheck()
    {
        var peers = ParseArp("  192.168.1.20          aa-bb-cc-dd-ee-ff     dynamic\r\n  224.0.0.22            01-00-5e-00-00-16     static\r\n  10.0.0.2              11-22-33-44-55-66     dynamic\r\n");
        if (peers.Count != 2 || peers[0].Address != "192.168.1.20")
            throw new InvalidOperationException("arp разобран неверно");
        string windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        if (CanRecycle(windows))
            throw new InvalidOperationException("Windows нельзя в корзину из этого списка");
        string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "нет-такого.txt");
        if (!CanRecycle(desk))
            throw new InvalidOperationException("файл на столе должен быть допустим");
        if (CanRecycle(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)))
            throw new InvalidOperationException("папку целиком нельзя");
        string junkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu");
        Directory.CreateDirectory(junkDir);
        string junk = Path.Combine(junkDir, "recycle-check.txt");
        try
        {
            File.WriteAllText(junk, "x");
            string msg = Recycle(new[] { junk });
            if (File.Exists(junk))
                throw new InvalidOperationException("корзина не забрала: " + msg);
        }
        finally
        {
            try { if (File.Exists(junk)) File.Delete(junk); } catch { /* проверка закончена */ }
        }
    }

    static string MuteDown()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device.AudioEndpointVolume.Mute)
                return "Звук уже был выключен.";
            device.AudioEndpointVolume.Mute = true;
            _weMuted = true;
            return "Звук выключен.";
        }
        catch (Exception ex)
        {
            return "Звук не выключился: " + ex.Message;
        }
    }

    static void AddFolder(List<StartupItem> list, Environment.SpecialFolder folder, string where, bool canDisable)
    {
        try
        {
            string dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir))
                return;
            foreach (string path in Directory.EnumerateFiles(dir))
            {
                list.Add(new StartupItem
                {
                    Name = Path.GetFileName(path),
                    Command = path,
                    Where = where,
                    CanDisable = canDisable,
                    Kind = "folder",
                    Key = path
                });
            }
        }
        catch { /* папка недоступна */ }
    }

    static void WalkDevice(List<DeviceUse> list, string device, string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, false);
            if (key == null)
                return;
            WalkKey(list, device, key, depth: 0);
            if (!list.Any(d => d.Device == device && d.Active) && Active(key))
            {
                list.Add(new DeviceUse { Device = device, Name = "программа без имени", Active = true });
            }
        }
        catch { /* нет раздела */ }
    }

    static void WalkKey(List<DeviceUse> list, string device, RegistryKey key, int depth)
    {
        foreach (string name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name, false);
            if (child == null)
                continue;
            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                if (depth < 2)
                    WalkKey(list, device, child, depth + 1);
                continue;
            }

            if (Active(child))
                list.Add(new DeviceUse { Device = device, Name = Pretty(name), Active = true });
            else if (depth < 2)
                WalkKey(list, device, child, depth + 1);
        }
    }

    static bool Active(RegistryKey key)
    {
        long start = ReadTime(key, "LastUsedTimeStart");
        long stop = ReadTime(key, "LastUsedTimeStop");
        return start > 0 && start > stop;
    }

    static long ReadTime(RegistryKey key, string name)
    {
        object? value = key.GetValue(name);
        return value switch
        {
            long l => l,
            int i => i,
            byte[] bytes when bytes.Length >= 8 => BitConverter.ToInt64(bytes, 0),
            string text when long.TryParse(text, out long n) => n,
            _ => 0
        };
    }

    static string Pretty(string name)
    {
        string text = name.Replace('#', '\\');
        try
        {
            if (text.Contains('\\'))
                return Path.GetFileName(text);
        }
        catch { /* странное имя */ }
        return text;
    }

    static bool IsDeny(string keyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, false);
            return string.Equals(key?.GetValue("Value")?.ToString(), "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static string SetOne(string keyPath, bool closed, string title)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, true);
            if (key == null)
                return title + ": Windows не открыл раздел.";
            string current = key.GetValue("Value")?.ToString() ?? "Allow";
            if (closed)
            {
                if (!string.Equals(current, "Deny", StringComparison.OrdinalIgnoreCase))
                    RememberConsent(keyPath, current);
                key.SetValue("Value", "Deny");
                return title + " закрыт.";
            }

            string back = RecallConsent(keyPath);
            if (back != "Allow" && back != "Prompt")
                back = "Allow";
            key.SetValue("Value", back);
            return title + " открыт.";
        }
        catch (Exception ex)
        {
            return title + " не изменился: " + ex.Message;
        }
    }

    static IEnumerable<string> HeavyRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
            yield return Path.Combine(profile, "Downloads");
    }

    static string ProfileRoot()
    {
        string profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!profile.EndsWith(Path.DirectorySeparatorChar))
            profile += Path.DirectorySeparatorChar;
        return profile;
    }

    static string ParkPath(string name)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "startup-off");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, name);
        if (!File.Exists(dest))
            return dest;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 2; i < 100; i++)
        {
            dest = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(dest))
                return dest;
        }

        return dest;
    }

    static void Remember(Parked item)
    {
        var list = ReadParked();
        list.Add(item);
        WriteParked(list);
    }

    static List<Parked> ReadParked()
    {
        try
        {
            string path = ParkFile();
            if (!File.Exists(path))
                return new List<Parked>();
            return JsonSerializer.Deserialize<List<Parked>>(File.ReadAllText(path)) ?? new List<Parked>();
        }
        catch
        {
            return new List<Parked>();
        }
    }

    static void WriteParked(List<Parked> list)
    {
        string path = ParkFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(list, Json));
    }

    static string ParkFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "startup-off.json");

    static void RememberConsent(string key, string value)
    {
        try
        {
            var map = ReadConsent();
            map[key] = value;
            File.WriteAllText(ConsentFile(), JsonSerializer.Serialize(map, Json));
        }
        catch { /* замок всё равно ставится */ }
    }

    static string RecallConsent(string key)
    {
        var map = ReadConsent();
        return map.TryGetValue(key, out string? value) ? value : "Allow";
    }

    static Dictionary<string, string> ReadConsent()
    {
        try
        {
            string path = ConsentFile();
            if (!File.Exists(path))
                return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    static string ConsentFile() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "device-lock.json");

    sealed class Parked
    {
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public string ParkedPath { get; set; } = "";
    }
}

/// <summary>Не даёт экрану погаснуть, пока включено вручную или идёт передача файлов.</summary>
public static class StayAwake
{
    const uint Continuous = 0x80000000;
    const uint SystemRequired = 0x00000001;
    const uint DisplayRequired = 0x00000002;

    static System.Threading.Timer? _timer;
    static int _manual;
    static int _held;

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    public static bool Manual => Volatile.Read(ref _manual) == 1;

    public static void Start()
    {
        if (_timer != null)
            return;
        _timer = new System.Threading.Timer(_ => Tick(), null, 5000, 5000);
    }

    public static void SetManual(bool on)
    {
        Volatile.Write(ref _manual, on ? 1 : 0);
        Tick();
    }

    static void Tick()
    {
        bool hold = Volatile.Read(ref _manual) == 1 || Zwmenu.Overlay.EdgeTransferHud.Busy;
        if (hold)
        {
            Volatile.Write(ref _held, 1);
            SetThreadExecutionState(Continuous | SystemRequired | DisplayRequired);
        }
        else if (Volatile.Read(ref _held) == 1)
        {
            Volatile.Write(ref _held, 0);
            SetThreadExecutionState(Continuous);
        }
    }
}
