using System.Windows.Media.Imaging;
using TagLib;
using File = System.IO.File;
using TagFile = TagLib.File;

namespace Zwmenu.Services;

public sealed class MusicNote
{
    public string Path = "";
    public string Title = "";
    public string Artist = "";
    public string Album = "";
    public string AlbumArtist = "";
    public string Genre = "";
    public string Year = "";
    public string Track = "";
    public string Comment = "";
    public string Lyrics = "";
    public string Facts = "";
    public bool CanWrite;
    public byte[]? Cover;
    public string CoverMime = "";
    public bool CoverChanged;
    public bool ClearCover;
}

/// <summary>Чтение и запись тегов без перекодирования звука.</summary>
public static class MusicMeta
{
    public static MusicNote Read(string path)
    {
        var note = new MusicNote { Path = path };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return note;
        try
        {
            using var file = TagFile.Create(path);
            var tag = file.Tag;
            note.Title = tag.Title ?? "";
            note.Artist = Join(tag.Performers);
            note.Album = tag.Album ?? "";
            note.AlbumArtist = Join(tag.AlbumArtists);
            note.Genre = Join(tag.Genres);
            note.Year = tag.Year > 0 ? tag.Year.ToString() : "";
            note.Track = tag.Track > 0 ? tag.Track.ToString() : "";
            note.Comment = tag.Comment ?? "";
            note.Lyrics = tag.Lyrics ?? "";
            note.CanWrite = true;
            note.Facts = Facts(file, path);
            note.Cover = CopyCover(tag.Pictures, out string mime);
            note.CoverMime = mime;
        }
        catch (Exception ex)
        {
            note.Facts = "Теги не прочитались: " + ex.Message;
            note.CanWrite = WritableGuess(path);
        }
        return note;
    }

    public static void Write(string path, MusicNote note)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("Файл не найден.");
        using var file = TagFile.Create(path);
        var tag = file.Tag;
        tag.Title = Clean(note.Title);
        tag.Performers = Split(note.Artist);
        tag.Album = Clean(note.Album);
        tag.AlbumArtists = Split(note.AlbumArtist);
        tag.Genres = Split(note.Genre);
        tag.Comment = Clean(note.Comment);
        tag.Lyrics = Clean(note.Lyrics);
        tag.Year = ParseUInt(note.Year);
        tag.Track = ParseUInt(note.Track);
        if (note.ClearCover)
            tag.Pictures = Array.Empty<IPicture>();
        else if (note.CoverChanged && note.Cover is { Length: > 0 })
        {
            string mime = string.IsNullOrWhiteSpace(note.CoverMime) ? MimeOf(note.Cover) : note.CoverMime;
            tag.Pictures = new IPicture[]
            {
                new Picture(new ByteVector(note.Cover))
                {
                    Type = PictureType.FrontCover,
                    MimeType = mime,
                    Description = "Cover"
                }
            };
        }
        file.Save();
    }

    public static byte[]? LoadCoverFile(string imagePath, out string mime)
    {
        mime = "";
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return null;
        var info = new FileInfo(imagePath);
        if (info.Length <= 0 || info.Length > 12 * 1024 * 1024)
            throw new InvalidOperationException("Картинка слишком большая.");
        byte[] bytes = File.ReadAllBytes(imagePath);
        mime = MimeOf(bytes);
        if (mime.Length == 0)
            throw new InvalidOperationException("Нужен jpg, png или webp.");
        if (CoverImage(bytes, 64) == null)
            throw new InvalidOperationException("Это не картинка.");
        return bytes;
    }

    public static BitmapSource? CoverImage(byte[]? bytes, int edge = 320)
    {
        if (bytes == null || bytes.Length == 0)
            return null;
        try
        {
            // Копия буфера: поток тега уже мог закрыться, а BitmapDecoder читает лениво.
            byte[] copy = (byte[])bytes.Clone();
            using var input = new MemoryStream(copy, 0, copy.Length, writable: false, publiclyVisible: true);
            var decoder = BitmapDecoder.Create(
                input,
                BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames[0];
            int w = frame.PixelWidth;
            int h = frame.PixelHeight;
            if (w <= 0 || h <= 0)
                return null;
            double scale = Math.Min(1.0, edge / (double)Math.Max(w, h));
            BitmapSource source = frame;
            if (scale < 0.999)
            {
                source = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
            }
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    static byte[]? CopyCover(IPicture[]? pictures, out string mime)
    {
        mime = "";
        if (pictures == null || pictures.Length == 0)
            return null;
        IPicture? pick = null;
        for (int i = 0; i < pictures.Length; i++)
        {
            var pic = pictures[i];
            if (pic?.Data == null || pic.Data.Count == 0)
                continue;
            if (pic.Type == PictureType.FrontCover)
            {
                pick = pic;
                break;
            }
            pick ??= pic;
        }
        if (pick?.Data == null || pick.Data.Count == 0)
            return null;
        byte[] bytes = pick.Data.ToArray();
        mime = string.IsNullOrWhiteSpace(pick.MimeType) ? MimeOf(bytes) : pick.MimeType;
        return bytes;
    }

    public static void SelfCheck()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zwmenu-music-meta");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "probe.wav");
        WriteTinyWav(path);
        Write(path, new MusicNote
        {
            Title = "Раз",
            Artist = "Анна",
            Album = "Тест",
            Genre = "рок",
            Year = "2024",
            Track = "3",
            Comment = "ок",
            Lyrics = "ляля"
        });
        var note = Read(path);
        if (note.Title != "Раз" || note.Artist != "Анна" || note.Album != "Тест")
            throw new InvalidOperationException("музыка теги");
        if (note.Year != "2024" || note.Track != "3")
            throw new InvalidOperationException("музыка номер");
        byte[] cover = TinyPng();
        Write(path, new MusicNote
        {
            Title = "Два",
            Artist = note.Artist,
            Album = note.Album,
            Genre = note.Genre,
            Year = note.Year,
            Track = note.Track,
            Comment = note.Comment,
            Lyrics = "ляля",
            Cover = cover,
            CoverMime = "image/png",
            CoverChanged = true
        });
        note = Read(path);
        if (note.Title != "Два")
            throw new InvalidOperationException("музыка повторная запись");
        if (note.Cover == null || note.Cover.Length < 8)
            throw new InvalidOperationException("музыка обложка");
        Write(path, new MusicNote
        {
            Title = note.Title,
            Artist = note.Artist,
            Album = note.Album,
            ClearCover = true
        });
        note = Read(path);
        if (note.Cover != null && note.Cover.Length > 0)
            throw new InvalidOperationException("музыка снятие обложки");
    }

    static string MimeOf(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        return "";
    }

    static byte[] TinyPng()
    {
        // 1x1 PNG, красный пиксель.
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    }

    static string Facts(TagFile file, string path)
    {
        var bits = new List<string>();
        try
        {
            var props = file.Properties;
            if (props.Duration.TotalSeconds > 0)
                bits.Add(Clock(props.Duration));
            if (props.AudioBitrate > 0)
                bits.Add(props.AudioBitrate + " кбит/с");
            if (props.AudioSampleRate > 0)
                bits.Add(props.AudioSampleRate + " Гц");
            if (props.AudioChannels > 0)
                bits.Add(props.AudioChannels == 1 ? "моно" : props.AudioChannels + " кан.");
            string codecs = string.Join(", ", props.Codecs.Where(c => c != null).Select(c => c!.Description).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct());
            if (codecs.Length > 0)
                bits.Add(codecs);
        }
        catch
        {
            // Свойства не обязательны.
        }
        try
        {
            var info = new FileInfo(path);
            bits.Add(SizeText(info.Length));
        }
        catch { /* нет размера */ }
        return string.Join(" · ", bits);
    }

    static string Clock(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return ((int)time.TotalHours) + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00");
        return time.Minutes + ":" + time.Seconds.ToString("00");
    }

    static string SizeText(long bytes)
    {
        if (bytes < 1024)
            return bytes + " Б";
        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.#") + " КБ";
        return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " МБ";
    }

    static string Join(string[]? values)
    {
        if (values == null || values.Length == 0)
            return "";
        return string.Join("; ", values.Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    static string[] Split(string text)
    {
        text = Clean(text) ?? "";
        if (text.Length == 0)
            return Array.Empty<string>();
        return text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    static string? Clean(string? text)
    {
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    static uint ParseUInt(string text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
            return 0;
        return uint.TryParse(text, out uint value) ? value : 0;
    }

    static bool WritableGuess(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wma", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aif", StringComparison.OrdinalIgnoreCase);
    }

    static void WriteTinyWav(string path)
    {
        // 0.01 с тишины, 8 кГц mono 8-bit PCM.
        byte[] data = new byte[80];
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + data.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(8000);
        bw.Write(8000);
        bw.Write((short)1);
        bw.Write((short)8);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(data.Length);
        bw.Write(data);
        File.WriteAllBytes(path, ms.ToArray());
    }
}
