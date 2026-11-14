using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Zwmenu.Services;

/// <summary>
/// Снимок через дублирование вывода DXGI. GDI видит только старый рабочий стол
/// и пропускает игру на весь экран. Здесь берётся кадр, который реально рисует монитор.
/// </summary>
static class ScreenDxgi
{
    const int WaitTimeout = unchecked((int)0x887A0027);
    const uint BgraSupport = 0x20;
    const int SdkVersion = 7;
    const uint Staging = 3;
    const uint CpuRead = 0x20000;
    const uint MapRead = 1;

    public static string FailReason { get; private set; } = "";

    public static Bitmap? TryCapture(Rectangle bounds)
    {
        FailReason = "";
        if (bounds.Width < 2 || bounds.Height < 2)
            return Miss("пустой");
        int frameSize = Marshal.SizeOf<FrameInfo>();
        int outSize = Marshal.SizeOf<OutputDesc>();
        if (frameSize != 48 || outSize != 96)
            return Miss($"size frame={frameSize} out={outSize}");

        IntPtr factory = IntPtr.Zero;
        var keep = new List<IntPtr>(8);
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            int factoryHr = CreateDXGIFactory1(ref iid, out factory);
            if (factoryHr != 0 || factory == IntPtr.Zero)
                return Miss("factory " + factoryHr.ToString("X8"));
            keep.Add(factory);

            var seen = new System.Text.StringBuilder();
            for (uint adapterIndex = 0; adapterIndex < 8; adapterIndex++)
            {
                int adHr = Call<EnumByIndex>(factory, 12)(factory, adapterIndex, out var adapter);
                if (adHr != 0 || adapter == IntPtr.Zero)
                {
                    seen.Append($" ad{adapterIndex}:{adHr:X8}");
                    break;
                }
                keep.Add(adapter);
                seen.Append($" ad{adapterIndex}");

                for (uint outputIndex = 0; outputIndex < 8; outputIndex++)
                {
                    int outHr = Call<EnumByIndex>(adapter, 7)(adapter, outputIndex, out var output);
                    if (outHr != 0 || output == IntPtr.Zero)
                    {
                        seen.Append($" enumOut {outHr:X8}");
                        break;
                    }
                    keep.Add(output);

                    int descHr = Call<GetOutputDescFn>(output, 7)(output, out var desc);
                    int width = desc.Right - desc.Left;
                    int height = desc.Bottom - desc.Top;
                    seen.Append($" out {desc.Left},{desc.Top} {width}x{height} rot={desc.Rotation} att={desc.Attached} hr={descHr:X8}");
                    if (descHr != 0 || desc.Attached == 0)
                        continue;
                    if (desc.Rotation != 0 && desc.Rotation != 1)
                        continue;
                    if (width != bounds.Width || height != bounds.Height)
                        continue;
                    if (Math.Abs(desc.Left - bounds.X) > 2 || Math.Abs(desc.Top - bounds.Y) > 2)
                        continue;

                    var bmp = CaptureOutput(adapter, output, width, height, keep);
                    if (bmp != null)
                        return bmp;
                    seen.Append(" cap=" + FailReason);
                }
            }

            return Miss($"bounds {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}" + seen);
        }
        catch (Exception ex)
        {
            return Miss(ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            for (int i = keep.Count - 1; i >= 0; i--)
                Release(keep[i]);
        }
    }

    static Bitmap? CaptureOutput(IntPtr adapter, IntPtr output, int width, int height, List<IntPtr> keep)
    {
        var iid = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        int qiHr = Call<QueryFn>(output, 0)(output, ref iid, out var output1);
        if (qiHr != 0 || output1 == IntPtr.Zero)
            return Miss("qi " + qiHr.ToString("X8"));
        keep.Add(output1);

        int devHr = D3D11CreateDevice(adapter, 0, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0, SdkVersion, out var device, out _, out var context);
        if (devHr != 0 || device == IntPtr.Zero || context == IntPtr.Zero)
            return Miss("device " + devHr.ToString("X8"));
        keep.Add(device);
        keep.Add(context);

        int dupHr = Call<DuplicateFn>(output1, 22)(output1, device, out var dup);
        if (dupHr != 0 || dup == IntPtr.Zero)
            return Miss("dup " + dupHr.ToString("X8"));
        keep.Add(dup);

        var acquire = Call<AcquireFn>(dup, 8);
        var releaseFrame = Call<ReleaseFrameFn>(dup, 14);
        IntPtr stage = IntPtr.Zero;
        bool held = false;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { DwmFlush(); } catch { /* нет композитора */ }

                int hr = acquire(dup, 120, out var info, out var resource);
                if (hr == WaitTimeout || resource == IntPtr.Zero)
                    continue;
                if (hr != 0)
                    return Miss("acquire " + hr.ToString("X8"));

                held = true;
                IntPtr tex = IntPtr.Zero;
                bool mapped = false;
                try
                {
                    var texIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    int texHr = Call<QueryFn>(resource, 0)(resource, ref texIid, out tex);
                    if (texHr != 0 || tex == IntPtr.Zero)
                        return Miss("tex " + texHr.ToString("X8"));

                    if (stage == IntPtr.Zero)
                    {
                        Call<GetTexDescFn>(tex, 10)(tex, out var srcDesc);
                        if (srcDesc.Width != (uint)width || srcDesc.Height != (uint)height)
                            return Miss($"texsize {srcDesc.Width}x{srcDesc.Height}");
                        if (srcDesc.Format != 87 && srcDesc.Format != 91)
                            return Miss("fmt " + srcDesc.Format);

                        var staging = new TexDesc
                        {
                            Width = srcDesc.Width,
                            Height = srcDesc.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = srcDesc.Format,
                            SampleCount = 1,
                            SampleQuality = 0,
                            Usage = Staging,
                            BindFlags = 0,
                            CpuAccess = CpuRead,
                            MiscFlags = 0
                        };
                        int stageHr = Call<CreateTexFn>(device, 5)(device, ref staging, IntPtr.Zero, out stage);
                        if (stageHr != 0 || stage == IntPtr.Zero)
                            return Miss("stage " + stageHr.ToString("X8"));
                        keep.Add(stage);
                    }

                    Call<CopyVoidFn>(context, 47)(context, stage, tex);
                    int mapHr = Call<MapFn>(context, 14)(context, stage, 0, MapRead, 0, out var map);
                    if (mapHr != 0 || map.Data == IntPtr.Zero)
                        return Miss("map " + mapHr.ToString("X8"));
                    mapped = true;
                    if (map.RowPitch < (uint)width * 4)
                        return Miss("pitch " + map.RowPitch);

                    var copied = CopyBitmap(map.Data, (int)map.RowPitch, width, height);
                    if (copied != null)
                        return copied;

                    FailReason = $"пустой кадр {attempt} frames={info.AccumulatedFrames} prot={info.ProtectedContentMaskedOut} pitch={map.RowPitch}";
                }
                finally
                {
                    if (mapped)
                        Call<UnmapFn>(context, 15)(context, stage, 0);
                    if (held)
                    {
                        releaseFrame(dup);
                        held = false;
                    }
                    if (tex != IntPtr.Zero)
                        Release(tex);
                    if (resource != IntPtr.Zero)
                        Release(resource);
                }
            }

            return Miss(string.IsNullOrEmpty(FailReason) ? "нет кадра" : FailReason);
        }
        finally
        {
            if (held)
                releaseFrame(dup);
        }
    }

    static unsafe Bitmap? CopyBitmap(IntPtr source, int rowPitch, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int row = width * 4;
            byte* src = (byte*)source;
            byte* dst = (byte*)bits.Scan0;
            bool any = false;
            for (int y = 0; y < height; y++)
            {
                byte* s = src + y * rowPitch;
                byte* d = dst + y * bits.Stride;
                for (int x = 0; x < row; x += 4)
                {
                    byte b = s[x];
                    byte g = s[x + 1];
                    byte r = s[x + 2];
                    byte a = s[x + 3];
                    // Часть драйверов отдаёт цвет с нулевой альфой. Для снимка это непрозрачный пиксель.
                    if (a == 0 && (b | g | r) != 0)
                        a = 255;
                    d[x] = b;
                    d[x + 1] = g;
                    d[x + 2] = r;
                    d[x + 3] = a;
                    if ((b | g | r | a) != 0)
                        any = true;
                }
            }

            if (!any)
            {
                bmp.UnlockBits(bits);
                bmp.Dispose();
                FailReason = "пустой кадр";
                return null;
            }

            byte* probe = dst + (height / 2) * bits.Stride + (width / 2) * 4;
            if (probe[0] == 0x11 && probe[1] == 0x22 && probe[2] == 0x33)
            {
                bmp.UnlockBits(bits);
                bmp.Dispose();
                FailReason = "копия не записалась";
                return null;
            }
        }
        catch
        {
            try { bmp.UnlockBits(bits); } catch { /* уже закрыт */ }
            bmp.Dispose();
            throw;
        }

        bmp.UnlockBits(bits);
        return bmp;
    }

    static Bitmap? Miss(string why)
    {
        FailReason = why;
        return null;
    }

    static void Release(IntPtr com)
    {
        if (com == IntPtr.Zero)
            return;
        try
        {
            Call<ReleaseFn>(com, 2)(com);
        }
        catch
        {
            // уже отпущен
        }
    }

    static T Call<T>(IntPtr com, int slot) where T : Delegate
    {
        IntPtr table = Marshal.ReadIntPtr(com);
        IntPtr fn = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    [DllImport("dwmapi.dll")]
    static extern int DwmFlush();

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Attached;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX;
        public int PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TexDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccess;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MappedSub
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int QueryFn(IntPtr self, ref Guid iid, out IntPtr punk);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int EnumByIndex(IntPtr self, uint index, out IntPtr punk);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetOutputDescFn(IntPtr self, out OutputDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int DuplicateFn(IntPtr self, IntPtr device, out IntPtr dup);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int AcquireFn(IntPtr self, uint timeout, out FrameInfo info, out IntPtr resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int ReleaseFrameFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int CreateTexFn(IntPtr self, ref TexDesc desc, IntPtr initial, out IntPtr tex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void CopyVoidFn(IntPtr self, IntPtr dst, IntPtr src);

    delegate void UpdateFn(IntPtr self, IntPtr dst, uint sub, IntPtr box, IntPtr data, uint rowPitch, uint depthPitch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int MapFn(IntPtr self, IntPtr resource, uint sub, uint mapType, uint flags, out MappedSub mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int UnmapFn(IntPtr self, IntPtr resource, uint sub);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void GetTexDescFn(IntPtr self, out TexDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint ReleaseFn(IntPtr self);
}
