using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using TesseractOCR.Enums;
using TessEngine = TesseractOCR.Engine;
using TessImage = TesseractOCR.Pix.Image;

namespace Zwmenu.Services;

/// <summary>Текст с куска снимка: распознавание на машине, перевод на русский по сети.</summary>
public static class ScreenText
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly object EngineGate = new();
    private static TessEngine? _engine;

    static ScreenText()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Zwmenu/1.0");
    }

    public static Task<string> ReadAsync(byte[] bgra, int width, int height)
    {
        if (width < 4 || height < 4 || bgra.Length < width * height * 4)
            return Task.FromResult("");

        return Task.Run(() => Recognize(bgra, width, height));
    }

    private static string Recognize(byte[] bgra, int width, int height)
    {
        byte[] png = ToPng(bgra, width, height);
        lock (EngineGate)
        {
            var engine = Engine();
            string text;
            using (var image = TessImage.LoadFromMemory(png))
            using (var page = engine.Process(image))
                text = (page.Text ?? "").Trim();
            engine.ClearAdaptiveClassifier();
            return text;
        }
    }

    public static byte[] CopyPixels(Bitmap source, out int width, out int height)
    {
        width = source.Width;
        height = source.Height;
        var data = source.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int packed = width * 4;
            if (data.Stride < packed)
                return Array.Empty<byte>();
            var pixels = new byte[packed * height];
            for (int y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * packed, packed);
            return pixels;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    private static TessEngine Engine()
    {
        if (_engine != null)
            return _engine;

        string dir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(dir, "eng.traineddata"))
            || !File.Exists(Path.Combine(dir, "rus.traineddata")))
            throw new InvalidOperationException("Нет файлов распознавания рядом с программой.");

        try
        {
            var engine = new TessEngine(dir, "eng+rus", EngineMode.LstmOnly);
            engine.DefaultPageSegMode = PageSegMode.SingleBlock;
            engine.SetVariable("preserve_interword_spaces", "1");
            engine.SetVariable("user_defined_dpi", "300");
            _engine = engine;
            return engine;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            throw new InvalidOperationException("Не удалось загрузить движок распознавания.", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Распознавание не запустилось.", ex);
        }
    }

    private static byte[] ToPng(byte[] pixels, int width, int height)
    {
        double scale = 1;
        int longEdge = Math.Max(width, height);
        if (longEdge > 2000)
            scale = 2000.0 / longEdge;
        else if (height < 80)
            scale = Math.Min(3, 120.0 / Math.Max(1, height));

        int nw = Math.Max(4, (int)Math.Round(width * scale));
        int nh = Math.Max(4, (int)Math.Round(height * scale));
        const int pad = 16;
        using var surface = SKSurface.Create(new SKImageInfo(nw + pad * 2, nh + pad * 2, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        var srcInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var src = SKImage.FromPixelCopy(srcInfo, pixels, width * 4);
        canvas.DrawImage(src, new SKRect(pad, pad, pad + nw, pad + nh));
        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Png, 90);
        if (data == null)
            throw new InvalidOperationException("Распознавание не запустилось.");
        return data.ToArray();
    }

    public static async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0)
            return "";
        if (IsMostlyCyrillic(text))
            return text;

        if (text.Length > 1800)
            text = text[..1800];

        string? google = await TryTranslate(text, GoogleAsync, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(google))
            return google;
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        return await TryTranslate(text, MemoryAsync, ct).ConfigureAwait(false) ?? "";
    }

    private static async Task<string?> TryTranslate(string text, Func<string, CancellationToken, Task<string>> call, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            return await call(text, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Букв кириллицы не меньше половины всех букв: переводить такой текст незачем.</summary>
    public static bool IsMostlyCyrillic(string? text) => MostlyCyrillic(text ?? "");

    private static bool MostlyCyrillic(string text)
    {
        int letters = 0;
        int cyr = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c))
                continue;
            letters++;
            if (c is (>= 'А' and <= 'я') or 'Ё' or 'ё')
                cyr++;
        }

        return letters > 0 && cyr * 2 >= letters;
    }

    private static async Task<string> GoogleAsync(string text, CancellationToken ct)
    {
        string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=ru&dt=t&q="
            + Uri.EscapeDataString(text);
        string json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        foreach (var part in doc.RootElement[0].EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Array || part.GetArrayLength() == 0)
                continue;
            if (part[0].ValueKind == JsonValueKind.String)
                sb.Append(part[0].GetString());
        }

        return sb.ToString().Trim();
    }

    private static async Task<string> MemoryAsync(string text, CancellationToken ct)
    {
        string url = "https://api.mymemory.translated.net/get?langpair=en|ru&q=" + Uri.EscapeDataString(text);
        string json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        string value = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
        value = value.Trim();
        if (value.Length == 0 || value.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            return "";
        return value;
    }
}
