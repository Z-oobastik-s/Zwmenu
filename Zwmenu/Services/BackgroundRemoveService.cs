using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Zwmenu.Services;

/// <summary>Снятие фона через лёгкую модель u2netp (ONNX, ~4 МБ).</summary>
public static class BackgroundRemoveService
{
    private const int ModelSize = 320;
    private static readonly object Gate = new();
    private static InferenceSession? _session;
    private static string? _inputName;
    private static string? _outputName;
    private static string? _loadError;

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <summary>Грузим ONNX заранее, не в момент клика «Фон».</summary>
    public static void Warmup()
    {
        try { EnsureSession(); }
        catch { /* модель опциональна */ }
    }

    public static (string? OutputPath, string? Error) Remove(string sourcePath)
    {
        if (!ImageConvertService.IsImageFile(sourcePath))
            return (null, "Это не изображение.");

        try
        {
            EnsureSession();
            if (_session == null)
                return (null, _loadError ?? "Модель снятия фона не найдена.");

            using var src = Decode(sourcePath);
            if ((long)src.Width * src.Height > 40_000_000)
                return (null, "Картинка слишком большая для снятия фона.");
            float[] mask = RunMask(src);

            using var cut = ApplyMask(src, mask);
            string dir = Path.GetDirectoryName(sourcePath) ?? "";
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            string dest = FileOutPath.NextFree(Path.Combine(dir, name + " без фона.png"));

            using var image = SKImage.FromBitmap(cut);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Не удалось сохранить PNG.");
            using var fs = File.Create(dest);
            data.SaveTo(fs);
            return (dest, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static void EnsureSession()
    {
        if (_session != null)
            return;

        lock (Gate)
        {
            if (_session != null)
                return;

            // Повторная попытка, если модель появилась после прошлой ошибки.
            if (_loadError != null)
            {
                string retry = ResolveModelPath();
                if (retry.Length == 0 || !File.Exists(retry))
                    return;
                _loadError = null;
            }

            try
            {
                string path = ResolveModelPath();
                if (path.Length == 0 || !File.Exists(path))
                {
                    _loadError = "Нет файла модели Assets\\Models\\u2netp.onnx";
                    return;
                }

                var opts = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
                };
                _session = new InferenceSession(path, opts);
                _inputName = _session.InputMetadata.Keys.First();
                _outputName = _session.OutputMetadata.Keys.First();
            }
            catch (Exception ex)
            {
                _loadError = "Модель фона: " + ex.Message;
                _session?.Dispose();
                _session = null;
            }
        }
    }

    private static string ResolveModelPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "Assets", "Models", "u2netp.onnx"),
            Path.Combine(baseDir, "Models", "u2netp.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Models", "u2netp.onnx")
        ];
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static unsafe float[] RunMask(SKBitmap src)
    {
        var input = new DenseTensor<float>(new[] { 1, 3, ModelSize, ModelSize });
        using var scaled = new SKBitmap(ModelSize, ModelSize, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(src, new SKRect(0, 0, ModelSize, ModelSize), paint);
        }

        IntPtr ptr = scaled.GetPixels();
        int stride = scaled.RowBytes;
        byte* basePtr = (byte*)ptr;
        for (int y = 0; y < ModelSize; y++)
        {
            byte* row = basePtr + y * stride;
            for (int x = 0; x < ModelSize; x++)
            {
                int o = x * 4;
                input[0, 0, y, x] = ((row[o] / 255f) - Mean[0]) / Std[0];
                input[0, 1, y, x] = ((row[o + 1] / 255f) - Mean[1]) / Std[1];
                input[0, 2, y, x] = ((row[o + 2] / 255f) - Mean[2]) / Std[2];
            }
        }

        using var results = _session!.Run(
        [
            NamedOnnxValue.CreateFromTensor(_inputName!, input)
        ]);

        var tensor = results.First(r => r.Name == _outputName).AsTensor<float>();
        var mask = new float[ModelSize * ModelSize];
        float min = float.MaxValue, max = float.MinValue;
        int i = 0;
        foreach (float v in tensor)
        {
            if (i >= mask.Length) break;
            mask[i++] = v;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        float range = Math.Max(1e-6f, max - min);
        for (int p = 0; p < mask.Length; p++)
            mask[p] = (mask[p] - min) / range;

        return mask;
    }

    private static unsafe SKBitmap ApplyMask(SKBitmap src, float[] mask320)
    {
        // Гарантируем Bgra8888 для прямого доступа к байтам.
        SKBitmap work = src;
        SKBitmap? owned = null;
        if (src.ColorType != SKColorType.Bgra8888 || src.AlphaType != SKAlphaType.Unpremul)
        {
            owned = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var c = new SKCanvas(owned);
            c.DrawBitmap(src, 0, 0);
            work = owned;
        }

        var dst = new SKBitmap(work.Width, work.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        byte* srcBase = (byte*)work.GetPixels();
        byte* dstBase = (byte*)dst.GetPixels();
        int srcStride = work.RowBytes;
        int dstStride = dst.RowBytes;
        int w = work.Width;
        int h = work.Height;

        for (int y = 0; y < h; y++)
        {
            float v = (y + 0.5f) * ModelSize / h - 0.5f;
            int y0 = (int)Math.Floor(v);
            int y1 = Math.Min(ModelSize - 1, y0 + 1);
            float fy = v - y0;
            y0 = Math.Clamp(y0, 0, ModelSize - 1);

            byte* srcRow = srcBase + y * srcStride;
            byte* dstRow = dstBase + y * dstStride;

            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) * ModelSize / w - 0.5f;
                int x0 = (int)Math.Floor(u);
                int x1 = Math.Min(ModelSize - 1, x0 + 1);
                float fx = u - x0;
                x0 = Math.Clamp(x0, 0, ModelSize - 1);

                float m00 = mask320[y0 * ModelSize + x0];
                float m10 = mask320[y0 * ModelSize + x1];
                float m01 = mask320[y1 * ModelSize + x0];
                float m11 = mask320[y1 * ModelSize + x1];
                float m = m00 * (1 - fx) * (1 - fy)
                        + m10 * fx * (1 - fy)
                        + m01 * (1 - fx) * fy
                        + m11 * fx * fy;

                m = Smoothstep(0.18f, 0.72f, m);

                int o = x * 4;
                byte b = srcRow[o];
                byte g = srcRow[o + 1];
                byte r = srcRow[o + 2];
                byte aSrc = srcRow[o + 3];
                byte a = (byte)Math.Clamp((int)Math.Round(m * aSrc), 0, 255);
                dstRow[o] = b;
                dstRow[o + 1] = g;
                dstRow[o + 2] = r;
                dstRow[o + 3] = a;
            }
        }

        owned?.Dispose();
        return dst;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static SKBitmap Decode(string path)
    {
        var sk = SKBitmap.Decode(path);
        if (sk != null)
            return sk;
        throw new InvalidOperationException("Не удалось прочитать изображение.");
    }

    public static void SelfCheck()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zwmenu-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string text = Path.Combine(dir, "нет.txt");
            File.WriteAllText(text, "x");
            var rejected = Remove(text);
            if (rejected.OutputPath != null || rejected.Error == null)
                throw new InvalidOperationException("текст принят как картинка");

            string source = Path.Combine(dir, "кадр.png");
            using (var bitmap = new System.Drawing.Bitmap(48, 32))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.White);
                graphics.FillEllipse(System.Drawing.Brushes.DarkRed, 12, 6, 24, 20);
                bitmap.Save(source, System.Drawing.Imaging.ImageFormat.Png);
            }

            var (path, error) = Remove(source);
            if (error != null || path == null || !File.Exists(path))
                throw new InvalidOperationException(error ?? "фон не снялся");
            using var check = SKBitmap.Decode(path) ?? throw new InvalidOperationException("png фона не читается");
            if (check.Width != 48 || check.Height != 32)
                throw new InvalidOperationException("размер после фона другой");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* проверка закончена */ }
        }
    }
}
