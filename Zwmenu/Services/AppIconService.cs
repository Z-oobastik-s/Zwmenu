using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Zwmenu.Models;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using MediaImageSource = System.Windows.Media.ImageSource;

namespace Zwmenu.Services;

/// <summary>
/// Достаёт крупную иконку (до 256px) через системный image list Windows
/// и кладёт PNG в %LOCALAPPDATA%\Zwmenu\Icons.
/// </summary>
public static class AppIconService
{
    private const int IconPx = 256;

    public static string IconsDirectory =>
        Path.Combine(MenuStore.DirectoryPath, "Icons");

    public static string TryExtract(string target)
    {
        try
        {
            string path = Resolve(target);
            if (path.Length == 0)
                return "";

            string dest = DestinationFor(path);
            if (File.Exists(dest))
                return dest;

            Directory.CreateDirectory(IconsDirectory);

            if (!TryGetIconBitmap(path, out var bitmap) || bitmap == null)
                return "";

            // Jumbo часто с чёрной/прозрачной каймой: вычищаем края, режем по контенту, вписываем в 96×96.
            using (bitmap)
            {
                PunchEdgeBlackToTransparent(bitmap);
                using var normalized = NormalizeSquare(bitmap, 96);
                normalized.Save(dest, ImageFormat.Png);
            }

            return File.Exists(dest) ? dest: "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// DrawIconEx часто заливает поле чёрным (A=255). С краёв заливаем «почти чёрное» в прозрачность,
    /// чтобы FindOpaqueBounds увидел реальный контент, а не весь квадрат.
    /// </summary>
    private static void PunchEdgeBlackToTransparent(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, DrawingPixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var visited = new bool[w * h];
            var queue = new Queue<int>();

            void EnqueueIfBlack(int x, int y)
            {
                if ((uint)x >= (uint)w || (uint)y >= (uint)h)
                    return;
                int i = y * w + x;
                if (visited[i])
                    return;
                IntPtr row = data.Scan0 + y * stride;
                byte b = Marshal.ReadByte(row, x * 4);
                byte g = Marshal.ReadByte(row, x * 4 + 1);
                byte r = Marshal.ReadByte(row, x * 4 + 2);
                byte a = Marshal.ReadByte(row, x * 4 + 3);
                // Уже прозрачное или почти чёрное поле.
                bool punch = a < 8 || (r < 18 && g < 18 && b < 18 && a > 0);
                if (!punch)
                    return;
                visited[i] = true;
                queue.Enqueue(i);
            }

            for (int x = 0; x < w; x++)
            {
                EnqueueIfBlack(x, 0);
                EnqueueIfBlack(x, h - 1);
            }
            for (int y = 0; y < h; y++)
            {
                EnqueueIfBlack(0, y);
                EnqueueIfBlack(w - 1, y);
            }

            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w;
                int y = i / w;
                IntPtr row = data.Scan0 + y * stride;
                Marshal.WriteByte(row, x * 4, 0);
                Marshal.WriteByte(row, x * 4 + 1, 0);
                Marshal.WriteByte(row, x * 4 + 2, 0);
                Marshal.WriteByte(row, x * 4 + 3, 0);
                EnqueueIfBlack(x - 1, y);
                EnqueueIfBlack(x + 1, y);
                EnqueueIfBlack(x, y - 1);
                EnqueueIfBlack(x, y + 1);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Jumbo 256px часто: мелкий значок по центру и море прозрачности.
    /// Режем по непрозрачным пикселям и масштабируем контент на весь квадрат.
    /// </summary>
    private static Bitmap NormalizeSquare(Bitmap source, int box)
    {
        var bounds = FindOpaqueBounds(source);
        using var cropped = source.Clone(bounds, DrawingPixelFormat.Format32bppArgb);

        var result = new Bitmap(box, box, DrawingPixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.Clear(System.Drawing.Color.Transparent);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        int inner = (int)(box * 0.9);
        double scale = Math.Min((double)inner / cropped.Width, (double)inner / cropped.Height);
        int w = Math.Max(1, (int)Math.Round(cropped.Width * scale));
        int h = Math.Max(1, (int)Math.Round(cropped.Height * scale));
        int x = (box - w) / 2;
        int y = (box - h) / 2;
        g.DrawImage(cropped, x, y, w, h);
        return result;
    }

    private static Rectangle FindOpaqueBounds(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int minX = w, minY = h, maxX = -1, maxY = -1;

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            for (int y = 0; y < h; y++)
            {
                IntPtr row = data.Scan0 + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte a = Marshal.ReadByte(row, x * 4 + 3);
                    if (a <= 24)
                        continue;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        if (maxX < minX || maxY < minY)
            return new Rectangle(0, 0, w, h);

        minX = Math.Max(0, minX - 1);
        minY = Math.Max(0, minY - 1);
        maxX = Math.Min(w - 1, maxX + 1);
        maxY = Math.Min(h - 1, maxY + 1);
        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public static void EnsureIcons(IEnumerable<MenuAction> items)
    {
        string appsIcon = EnsureBuiltinGroupIcon();
        foreach (var item in items)
        {
            if (item.Kind == ActionKind.Group)
                EnsureGroupIcon(item, appsIcon);
            else
                EnsureIcon(item);

            foreach (var child in item.Children)
                EnsureIcon(child);
        }
    }

    /// <summary>Иконка «игры/программы» из Assets: для групп apps.</summary>
    public static string EnsureBuiltinGroupIcon()
    {
        string dest = Path.Combine(IconsDirectory, "builtin_group_apps_v4.png");
        try
        {
            Directory.CreateDirectory(IconsDirectory);
            string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "group-apps.png");
            // Не копируем на каждый Load: файл уже на месте.
            if (File.Exists(bundled) && !File.Exists(dest))
                File.Copy(bundled, dest, overwrite: false);

            foreach (var legacy in Directory.EnumerateFiles(IconsDirectory, "builtin_group_apps_v*.png"))
            {
                if (!string.Equals(legacy, dest, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(legacy); } catch { /* locked */ }
                }
            }
        }
        catch
        {
            // ignore
        }
        return File.Exists(dest) ? dest: "";
    }

    private static void EnsureGroupIcon(MenuAction item, string appsBuiltinPath)
    {
        // Своя валидная иконка (не builtin): не трогаем.
        if (!string.IsNullOrWhiteSpace(item.IconPath)
            && File.Exists(item.IconPath)
            && !item.IconPath.Contains("builtin_group_", StringComparison.OrdinalIgnoreCase))
            return;

        if (item.IsProcessGroup)
        {
            string extracted = TryExtract("taskmgr.exe");
            if (extracted.Length > 0)
            {
                item.IconPath = extracted;
                return;
            }
        }

        if (item.IsFoldersGroup)
        {
            // Иконка группы папок: реальная иконка первой папки или «Изображения».
            string seed = item.Children
                .Select(c => c.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && Directory.Exists(v))
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            string extracted = TryExtract(seed);
            if (extracted.Length > 0)
            {
                item.IconPath = extracted;
                return;
            }
        }

        if (appsBuiltinPath.Length > 0)
            item.IconPath = appsBuiltinPath;
    }

    public static void EnsureIcon(MenuAction item)
    {
        if (item.Kind != ActionKind.Launch)
            return;
        if (string.IsNullOrWhiteSpace(item.Value))
            return;
        if (MenuAction.IsWebAddress(item.Value))
            return;

        string resolved = Resolve(item.Value);
        if (resolved.Length == 0 && MenuAction.IsShellAppTarget(item.Value))
            resolved = ResolveStoreAppExe(item.Value) ?? "";

        // Панель управления тоже explorer.exe — иначе иконка один в один с «Проводником».
        string iconSource = resolved;
        if (IsControlPanelItem(item))
        {
            string control = Path.Combine(Environment.SystemDirectory, "control.exe");
            if (File.Exists(control))
                iconSource = control;
        }

        // Кэш подходит только если PNG соответствует ТЕКУЩЕМУ пути (иначе Notepad → Notepad++ остаётся старой иконкой).
        if (!string.IsNullOrWhiteSpace(item.IconPath)
            && item.IconPath.Contains("_v5.png", StringComparison.OrdinalIgnoreCase)
            && File.Exists(item.IconPath)
            && iconSource.Length > 0
            && string.Equals(item.IconPath, DestinationFor(iconSource), StringComparison.OrdinalIgnoreCase))
            return;

        string extracted = iconSource.Length > 0 ? TryExtract(iconSource): "";
        if (extracted.Length == 0 && MenuAction.IsShellAppTarget(item.Value))
            extracted = TryExtractStoreApp(item.Value);
        if (extracted.Length > 0)
            item.IconPath = extracted;
        else if (!string.IsNullOrWhiteSpace(item.IconPath) && !File.Exists(item.IconPath))
            item.IconPath = "";
    }

    private static bool IsControlPanelItem(MenuAction item)
    {
        string args = item.Arguments ?? "";
        if (args.Contains("21EC2020", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(item.Label, "Панель управления", StringComparison.OrdinalIgnoreCase)
            && IsExplorerFile(item.Value);
    }

    private static bool IsExplorerFile(string? value)
    {
        string name = Path.GetFileName((value ?? "").Trim().Trim('"'));
        return name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Снести старый мелкий кэш (32px): следующий EnsureIcons вытянет 256px.</summary>
    public static void ClearCache()
    {
        try
        {
            if (!Directory.Exists(IconsDirectory))
                return;
            foreach (var file in Directory.EnumerateFiles(IconsDirectory, "*.png"))
            {
                try { File.Delete(file); }
                catch { /* locked */ }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string Resolve(string target)
    {
        string path = (target ?? "").Trim().Trim('"');
        if (path.Length == 0)
            return "";

        if (MenuAction.IsShellAppTarget(path))
            return ResolveStoreAppExe(path) ?? "";

        if (File.Exists(path) || Directory.Exists(path))
            return Path.GetFullPath(path);

        return FindOnPath(path) ?? "";
    }

    /// <summary>Приложение из Store → путь к exe в WindowsApps для извлечения иконки.</summary>
    private static string TryExtractStoreApp(string target)
    {
        string? exe = ResolveStoreAppExe(target);
        return exe == null ? "": TryExtract(exe);
    }

    private static string? ResolveStoreAppExe(string target)
    {
        try
        {
            string aumid = MenuAction.NormalizeShellAppTarget(target);
            const string prefix = "shell:AppsFolder\\";
            if (aumid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                aumid = aumid[prefix.Length..];

            int bang = aumid.IndexOf('!');
            if (bang <= 0)
                return null;

            string family = aumid[..bang]; // Claude_pzs8sxrjxfjjc
            int us = family.LastIndexOf('_');
            if (us <= 0)
                return null;

            string name = family[..us];
            string pub = family[(us + 1)..];
            string winApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (!Directory.Exists(winApps))
                return null;

            foreach (var dir in Directory.EnumerateDirectories(winApps, $"{name}_*_{pub}"))
            {
                string appDir = Path.Combine(dir, "app");
                string named = Path.Combine(appDir, name + ".exe");
                if (File.Exists(named))
                    return named;

                if (Directory.Exists(appDir))
                {
                    string? first = Directory.EnumerateFiles(appDir, "*.exe").FirstOrDefault();
                    if (first != null)
                        return first;
                }
            }
        }
        catch
        {
            // WindowsApps иногда недоступен
        }

        return null;
    }

    private static string? FindOnPath(string name)
    {
        if (name.IndexOfAny(['\\', '/', ':']) >= 0)
            return null;

        string file = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name: name + ".exe";
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, file);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { /* skip */ }
            }
        }

        string sys = Path.Combine(Environment.SystemDirectory, file);
        return File.Exists(sys) ? sys: null;
    }

    private static string DestinationFor(string sourcePath)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())))[..16];
        var sb = new StringBuilder();
        foreach (char ch in Path.GetFileNameWithoutExtension(sourcePath))
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                sb.Append(ch);
            if (sb.Length >= 40)
                break;
        }

        string safe = sb.Length == 0 ? "icon": sb.ToString();
        return Path.Combine(IconsDirectory, $"{safe}_{hash}_v5.png");
    }

    /// <summary>Иконка/превью в памяти без записи в Icons\ (для выезда содержимого папки).</summary>
    public static MediaImageSource? CreateLiveIcon(string path, int edge = 56)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
                return null;
            if (!TryGetIconBitmap(path, out var bitmap) || bitmap == null)
                return null;

            using (bitmap)
            {
                IntPtr hBitmap = bitmap.GetHbitmap();
                try
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(edge, edge));
                    src.Freeze();
                    return src;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetIconBitmap(string path, out Bitmap? bitmap)
    {
        bitmap = null;

        // Jumbo 256px через системный image list: не мыльный LARGEICON 32px.
        if (TryGetFromImageList(path, SHIL_JUMBO, out bitmap))
            return true;
        if (TryGetFromImageList(path, SHIL_EXTRALARGE, out bitmap))
            return true;

        var shfi = new SHFILEINFO();
        _ = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (shfi.hIcon != IntPtr.Zero)
        {
            try
            {
                bitmap = IconToAlphaBitmap(shfi.hIcon);
                return bitmap != null;
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }

        try
        {
            using var extracted = Icon.ExtractAssociatedIcon(path);
            if (extracted != null)
            {
                bitmap = IconToAlphaBitmap(extracted.Handle);
                return bitmap != null;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    /// <summary>Icon.ToBitmap() часто заливает прозрачность чёрным: рисуем на ARGB сами.</summary>
    private static Bitmap? IconToAlphaBitmap(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
            return null;

        if (!GetIconInfo(hIcon, out var info))
            return null;

        try
        {
            int w = 0, h = 0;
            if (info.hbmColor != IntPtr.Zero)
            {
                using var color = Image.FromHbitmap(info.hbmColor);
                w = color.Width;
                h = color.Height;
            }
            else if (info.hbmMask != IntPtr.Zero)
            {
                using var mask = Image.FromHbitmap(info.hbmMask);
                w = mask.Width;
                h = mask.Height / 2;
            }

            if (w <= 0 || h <= 0)
                return null;

            var bmp = new Bitmap(w, h, DrawingPixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (!DrawIconEx(hdc, 0, 0, hIcon, w, h, 0, IntPtr.Zero, DI_NORMAL))
                    {
                        bmp.Dispose();
                        return null;
                    }
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            return bmp;
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    private static bool TryGetFromImageList(string path, int shil, out Bitmap? bitmap)
    {
        bitmap = null;
        try
        {
            var shfi = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
            if (result == IntPtr.Zero)
                return false;

            int hr = SHGetImageList(shil, typeof(IImageList).GUID, out var listObj);
            if (hr != 0 || listObj is not IImageList list)
                return false;

            IntPtr hIcon = IntPtr.Zero;
            list.GetIcon(shfi.iIcon, ILD_TRANSPARENT, ref hIcon);
            if (hIcon == IntPtr.Zero)
                return false;

            try
            {
                bitmap = IconToAlphaBitmap(hIcon);
                return bitmap != null;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return false;
        }
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const int SHIL_EXTRALARGE = 0x2; // 48px
    private const int SHIL_JUMBO = 0x4;      // 256px
    private const int ILD_TRANSPARENT = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(ref IMAGELISTDRAWPARAMS pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IMAGELISTDRAWPARAMS
    {
        public int cbSize, himl, i, hdcDst, x, y, cx, cy, xBitmap, yBitmap, rgbBk, rgbFg, fStyle, dwRop, fState, Frame, crEffect;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IImageList ppv);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
}
