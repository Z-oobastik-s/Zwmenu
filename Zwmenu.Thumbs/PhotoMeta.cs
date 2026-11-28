using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Zwmenu.Thumbs;

/// <summary>Поля, которые человек может прочитать и поправить. Пиксели при записи не трогаются.</summary>
public sealed class PhotoNote
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Copyright { get; set; } = "";
    public string Taken { get; set; } = "";
    public bool CanWrite { get; set; }
    public string Note { get; set; } = "";
    public List<(string Label, string Value)> Facts { get; } = new();
}

/// <summary>Читает и пишет название, автора и съёмку в JPEG, PNG и WEBP, не перекодируя картинку.</summary>
public static class PhotoMeta
{
    public static PhotoNote Read(string path)
    {
        var note = new PhotoNote();
        string ext = Path.GetExtension(path);
        note.CanWrite = CanWrite(ext);
        note.Note = note.CanWrite
            ? "Поля пишутся в файл, картинка не пережимается."
            : "Смотреть можно. Запись есть у JPEG, PNG и WEBP, чтобы не побить остальные файлы.";
        try
        {
            byte[] file = File.ReadAllBytes(path);
            if (IsPng(ext))
                ReadPng(file, note);
            else if (IsWebp(ext))
                ReadWebp(file, note);
            else if (IsJpeg(ext))
                ReadJpeg(file, note);
            else if (IsTiff(ext))
                Fill(ParseTiff(file, 0, file.Length), note);
        }
        catch
        {
            note.CanWrite = false;
            note.Note = "Сведения не прочитались.";
        }
        return note;
    }

    public static void Write(string path, PhotoNote note)
    {
        string ext = Path.GetExtension(path);
        if (!CanWrite(ext))
            throw new InvalidOperationException("Этот формат не переписываю, чтобы не испортить файл.");
        byte[] file = File.ReadAllBytes(path);
        byte[] next = IsPng(ext) ? WritePng(file, note) : IsWebp(ext) ? WriteWebp(file, note) : WriteJpeg(file, note);
        string tmp = path + ".zwtmp";
        File.WriteAllBytes(tmp, next);
        try
        {
            File.Replace(tmp, path, null);
        }
        catch
        {
            File.Move(tmp, path, true);
        }
    }

    public static void SelfCheck()
    {
        string dir = Path.Combine(Path.GetTempPath(), "zwmenu-meta");
        Directory.CreateDirectory(dir);
        CheckRoundtrip(dir, "a.jpg", SKEncodedImageFormat.Jpeg);
        CheckRoundtrip(dir, "a.png", SKEncodedImageFormat.Png);
        CheckRoundtrip(dir, "a.webp", SKEncodedImageFormat.Webp);
    }

    static void CheckRoundtrip(string dir, string name, SKEncodedImageFormat format)
    {
        string path = Path.Combine(dir, name);
        using var bmp = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bmp.Erase(SKColors.Navy);
        using var data = bmp.Encode(format, 90) ?? throw new InvalidOperationException("кодирование " + name);
        File.WriteAllBytes(path, data.ToArray());
        var note = new PhotoNote
        {
            Title = "Раз",
            Author = "Анна",
            Description = "Текст",
            Keywords = "кот",
            Copyright = "я",
            Taken = "2024-05-01 14:30"
        };
        Write(path, note);
        var back = Read(path);
        if (back.Title != "Раз" || back.Author != "Анна" || back.Description != "Текст" || back.Keywords != "кот" || back.Copyright != "я")
            throw new InvalidOperationException("сведения " + name);
        if (!back.Taken.StartsWith("2024-05-01", StringComparison.Ordinal))
            throw new InvalidOperationException("дата " + name);
        back.Title = "Два";
        Write(path, back);
        var again = Read(path);
        if (again.Title != "Два" || again.Author != "Анна")
            throw new InvalidOperationException("правка " + name);
        using var opened = PhotoCodec.Open(path);
        if (opened.Width != 8 || opened.Height != 8)
            throw new InvalidOperationException("картинка после сведений " + name);
    }

    static bool CanWrite(string ext) => IsJpeg(ext) || IsPng(ext) || IsWebp(ext);

    static bool IsJpeg(string ext) =>
        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jpe", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jfif", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".pjp", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".pjpeg", StringComparison.OrdinalIgnoreCase);

    static bool IsPng(string ext) =>
        ext.Equals(".png", StringComparison.OrdinalIgnoreCase) || ext.Equals(".apng", StringComparison.OrdinalIgnoreCase);

    static bool IsWebp(string ext) => ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);

    static bool IsTiff(string ext) =>
        ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);

    sealed class Tag
    {
        public ushort Id;
        public ushort Type;
        public byte[] Data = Array.Empty<byte>();
    }

    sealed class Bag
    {
        public bool Le = true;
        public List<Tag> Root = new();
        public List<Tag> Exif = new();
        public List<Tag> Gps = new();
    }

    static void ReadJpeg(byte[] file, PhotoNote note)
    {
        if (!TryExif(file, out int at, out int len))
            return;
        Fill(ParseTiff(file, at, len), note);
    }

    static bool TryExif(byte[] file, out int tiff, out int len)
    {
        tiff = 0;
        len = 0;
        if (file.Length < 4 || file[0] != 0xFF || file[1] != 0xD8)
            return false;
        int i = 2;
        while (i + 4 < file.Length && file[i] == 0xFF)
        {
            while (i < file.Length && file[i] == 0xFF)
                i++;
            if (i >= file.Length)
                return false;
            byte marker = file[i++];
            if (marker == 0xDA || marker == 0xD9)
                return false;
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;
            if (i + 2 > file.Length)
                return false;
            int size = (file[i] << 8) | file[i + 1];
            if (size < 2 || i + size > file.Length)
                return false;
            if (marker == 0xE1 && size > 8 && HasAscii(file, i + 2, "Exif\0\0"))
            {
                tiff = i + 8;
                len = size - 8;
                return true;
            }
            i += size;
        }
        return false;
    }

    static byte[] WriteJpeg(byte[] file, PhotoNote note)
    {
        if (file.Length < 4 || file[0] != 0xFF || file[1] != 0xD8)
            throw new InvalidOperationException("Это не JPEG, поля не записал.");
        var parts = new List<byte[]>();
        int i = 2;
        int exifAt = -1;
        while (i + 4 < file.Length && file[i] == 0xFF)
        {
            int start = i;
            while (i < file.Length && file[i] == 0xFF)
                i++;
            if (i >= file.Length)
                break;
            byte marker = file[i++];
            if (marker == 0xDA || marker == 0xD9)
            {
                parts.Add(file[start..]);
                i = file.Length;
                break;
            }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                parts.Add(file[start..i]);
                continue;
            }
            if (i + 2 > file.Length)
                break;
            int size = (file[i] << 8) | file[i + 1];
            if (size < 2 || i + size > file.Length)
                break;
            if (marker == 0xE1 && size > 8 && HasAscii(file, i + 2, "Exif\0\0"))
                exifAt = parts.Count;
            parts.Add(file[start..(i + size)]);
            i += size;
        }
        if (i < file.Length)
            parts.Add(file[i..]);

        var bag = exifAt >= 0 ? ParseApp1(parts[exifAt]) : new Bag();
        Apply(bag, note);
        byte[] app1 = BuildApp1(bag);
        if (exifAt >= 0)
            parts[exifAt] = app1;
        else if (parts.Count > 0 && parts[0].Length > 1 && parts[0][1] == 0xE0)
            parts.Insert(1, app1);
        else
            parts.Insert(0, app1);

        var output = new MemoryStream();
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);
        foreach (byte[] part in parts)
            output.Write(part);
        return output.ToArray();
    }

    static Bag ParseApp1(byte[] segment)
    {
        int at = 2;
        while (at < segment.Length && segment[at] == 0xFF)
            at++;
        if (at >= segment.Length)
            return new Bag();
        at++;
        if (at + 2 > segment.Length)
            return new Bag();
        int size = (segment[at] << 8) | segment[at + 1];
        int tiff = at + 8;
        if (tiff >= segment.Length)
            return new Bag();
        return ParseTiff(segment, tiff, Math.Min(size - 8, segment.Length - tiff));
    }

    static void ReadPng(byte[] file, PhotoNote note)
    {
        foreach (var chunk in PngChunks(file))
        {
            if (chunk.Type is "tEXt" or "iTXt" or "zTXt")
                TakeText(chunk.Type, chunk.Data, note);
        }
    }

    static byte[] WritePng(byte[] file, PhotoNote note)
    {
        if (file.Length < 8 || file[0] != 0x89)
            throw new InvalidOperationException("Это не PNG, поля не записал.");
        var output = new MemoryStream();
        output.Write(file, 0, 8);
        bool wrote = false;
        foreach (var chunk in PngChunks(file))
        {
            if (chunk.Type == "IEND" && !wrote)
            {
                WriteTexts(output, note);
                wrote = true;
            }
            if (chunk.Type is "tEXt" or "iTXt" or "zTXt" && IsOurKey(TextKey(chunk.Type, chunk.Data)))
                continue;
            output.Write(Chunk(chunk.Type, chunk.Data));
        }
        return output.ToArray();
    }

    static void ReadWebp(byte[] file, PhotoNote note)
    {
        if (!TryWebpExif(file, out int at, out int len))
            return;
        int tiff = at;
        int size = len;
        if (len > 6 && HasAscii(file, at, "Exif\0\0"))
        {
            tiff = at + 6;
            size = len - 6;
        }
        Fill(ParseTiff(file, tiff, size), note);
    }

    static byte[] WriteWebp(byte[] file, PhotoNote note)
    {
        if (file.Length < 12 || !HasAscii(file, 0, "RIFF") || !HasAscii(file, 8, "WEBP"))
            throw new InvalidOperationException("Это не WEBP, поля не записал.");
        var bag = new Bag();
        if (TryWebpExif(file, out int oldAt, out int oldLen))
        {
            int tiff = oldAt;
            int size = oldLen;
            if (oldLen > 6 && HasAscii(file, oldAt, "Exif\0\0"))
            {
                tiff = oldAt + 6;
                size = oldLen - 6;
            }
            bag = ParseTiff(file, tiff, size);
        }
        Apply(bag, note);
        byte[] exif = BuildTiff(bag);
        var payload = new byte[6 + exif.Length];
        Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(payload, 0);
        exif.CopyTo(payload, 6);

        var output = new MemoryStream();
        output.Write(file, 0, 12);
        bool replaced = false;
        int p = 12;
        while (p + 8 <= file.Length)
        {
            string four = Encoding.ASCII.GetString(file, p, 4);
            int size = ReadLe(file, p + 4);
            int next = p + 8 + size + (size & 1);
            if (next > file.Length)
                break;
            if (four == "EXIF")
            {
                WriteWebpChunk(output, "EXIF", payload);
                replaced = true;
            }
            else
            {
                if (four == "VP8X" && size >= 1)
                {
                    byte[] data = file[(p + 8)..(p + 8 + size)];
                    data[0] |= 0x04;
                    WriteWebpChunk(output, four, data);
                }
                else
                {
                    output.Write(file, p, next - p);
                }
            }
            p = next;
        }
        if (!replaced)
            WriteWebpChunk(output, "EXIF", payload);
        byte[] done = output.ToArray();
        int riff = done.Length - 8;
        done[4] = (byte)riff;
        done[5] = (byte)(riff >> 8);
        done[6] = (byte)(riff >> 16);
        done[7] = (byte)(riff >> 24);
        return done;
    }

    static bool TryWebpExif(byte[] file, out int at, out int len)
    {
        at = 0;
        len = 0;
        if (file.Length < 12 || !HasAscii(file, 0, "RIFF") || !HasAscii(file, 8, "WEBP"))
            return false;
        int p = 12;
        while (p + 8 <= file.Length)
        {
            string four = Encoding.ASCII.GetString(file, p, 4);
            int size = ReadLe(file, p + 4);
            if (p + 8 + size > file.Length)
                return false;
            if (four == "EXIF")
            {
                at = p + 8;
                len = size;
                return true;
            }
            p += 8 + size + (size & 1);
        }
        return false;
    }

    static void WriteWebpChunk(Stream output, string four, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(four));
        int size = data.Length;
        output.WriteByte((byte)size);
        output.WriteByte((byte)(size >> 8));
        output.WriteByte((byte)(size >> 16));
        output.WriteByte((byte)(size >> 24));
        output.Write(data);
        if ((size & 1) != 0)
            output.WriteByte(0);
    }

    static Bag ParseTiff(byte[] file, int origin, int length)
    {
        var bag = new Bag();
        if (length < 8 || origin < 0 || origin + 8 > file.Length)
            return bag;
        bag.Le = file[origin] == (byte)'I';
        int ifd = ReadU32(file, origin + 4, bag.Le);
        bag.Root = ReadIfd(file, origin, length, ifd, bag.Le, out ushort _);
        Pull(bag.Root, 0x8769, file, origin, length, bag.Le, bag.Exif);
        Pull(bag.Root, 0x8825, file, origin, length, bag.Le, bag.Gps);
        bag.Root.RemoveAll(t => t.Id is 0x8769 or 0x8825 or 0x0201 or 0x0202);
        return bag;
    }

    static void Pull(List<Tag> host, ushort id, byte[] file, int origin, int length, bool le, List<Tag> into)
    {
        Tag? tag = host.Find(t => t.Id == id);
        if (tag == null || tag.Data.Length < 4)
            return;
        int offset = ReadU32(tag.Data, 0, le);
        into.AddRange(ReadIfd(file, origin, length, offset, le, out ushort _));
    }

    static List<Tag> ReadIfd(byte[] file, int origin, int length, int offset, bool le, out ushort count)
    {
        count = 0;
        var list = new List<Tag>();
        if (offset < 0 || origin + offset + 2 > file.Length || offset + 2 > length)
            return list;
        count = ReadU16(file, origin + offset, le);
        int entry = origin + offset + 2;
        for (int i = 0; i < count; i++)
        {
            if (entry + 12 > file.Length || entry + 12 > origin + length)
                break;
            ushort id = ReadU16(file, entry, le);
            ushort type = ReadU16(file, entry + 2, le);
            int n = ReadU32(file, entry + 4, le);
            int size = n * TypeSize(type);
            if (n < 0 || size < 0 || size > 1_000_000)
            {
                entry += 12;
                continue;
            }
            byte[] data;
            if (size <= 4)
            {
                data = new byte[size];
                Buffer.BlockCopy(file, entry + 8, data, 0, size);
            }
            else
            {
                int at = ReadU32(file, entry + 8, le);
                if (at < 0 || origin + at + size > file.Length || at + size > length)
                {
                    entry += 12;
                    continue;
                }
                data = new byte[size];
                Buffer.BlockCopy(file, origin + at, data, 0, size);
            }
            if (!le)
                SwapToLe(data, type);
            list.Add(new Tag { Id = id, Type = type, Data = data });
            entry += 12;
        }
        return list;
    }

    static void Apply(Bag bag, PhotoNote note)
    {
        SetXp(bag.Root, 0x9C9B, note.Title);
        SetXp(bag.Root, 0x9C9D, note.Author);
        SetXp(bag.Root, 0x9C9C, note.Description);
        SetXp(bag.Root, 0x9C9E, note.Keywords);
        string rights = note.Copyright.Trim();
        string rightsAscii = AsciiOf(rights);
        SetAscii(bag.Root, 0x8298, rightsAscii);
        SetXp(bag.Root, 0x9C9F, rightsAscii == rights ? "" : rights);
        SetAscii(bag.Root, 0x013B, AsciiOf(note.Author));
        string taken = ExifDate(note.Taken);
        SetAscii(bag.Root, 0x0132, taken);
        SetAscii(bag.Exif, 0x9003, taken);
        SetAscii(bag.Exif, 0x9004, taken);
    }

    static void Fill(Bag bag, PhotoNote note)
    {
        note.Title = First(Xp(bag.Root, 0x9C9B), Ascii(bag.Root, 0x010E));
        note.Author = First(Xp(bag.Root, 0x9C9D), Ascii(bag.Root, 0x013B));
        note.Description = First(Xp(bag.Root, 0x9C9C), UserText(bag.Exif, 0x9286));
        note.Keywords = Xp(bag.Root, 0x9C9E);
        note.Copyright = First(Xp(bag.Root, 0x9C9F), Ascii(bag.Root, 0x8298));
        note.Taken = ShowDate(First(Ascii(bag.Exif, 0x9003), Ascii(bag.Root, 0x0132)));
        string camera = Join(Ascii(bag.Root, 0x010F), Ascii(bag.Root, 0x0110));
        if (camera.Length > 0)
            note.Facts.Add(("Камера", camera));
        string lens = Ascii(bag.Exif, 0xA434);
        if (lens.Length > 0)
            note.Facts.Add(("Объектив", lens));
        string shutter = RationalText(bag.Exif, 0x829A, true);
        if (shutter.Length > 0)
            note.Facts.Add(("Выдержка", shutter));
        string aperture = RationalText(bag.Exif, 0x829D, false);
        if (aperture.Length > 0)
            note.Facts.Add(("Диафрагма", "f/" + aperture));
        string iso = ShortText(bag.Exif, 0x8827);
        if (iso.Length > 0)
            note.Facts.Add(("ISO", iso));
        string focus = RationalText(bag.Exif, 0x920A, false);
        if (focus.Length > 0)
            note.Facts.Add(("Фокус", focus + " мм"));
        string place = GpsText(bag);
        if (place.Length > 0)
            note.Facts.Add(("Место", place));
    }

    static byte[] BuildApp1(Bag bag)
    {
        byte[] tiff = BuildTiff(bag);
        if (tiff.Length + 8 > 65533)
        {
            bag.Exif.RemoveAll(t => t.Id == 0x927C);
            bag.Root.RemoveAll(t => t.Id == 0x927C);
            tiff = BuildTiff(bag);
        }
        if (tiff.Length + 8 > 65533)
            throw new InvalidOperationException("Сведений слишком много для JPEG.");
        int size = 2 + 6 + tiff.Length;
        var app = new byte[2 + 2 + 6 + tiff.Length];
        app[0] = 0xFF;
        app[1] = 0xE1;
        app[2] = (byte)(size >> 8);
        app[3] = (byte)size;
        Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(app, 4);
        tiff.CopyTo(app, 10);
        return app;
    }

    static byte[] BuildTiff(Bag bag)
    {
        var root = bag.Root.Where(t => t.Id is not (0x8769 or 0x8825)).OrderBy(t => t.Id).ToList();
        var exif = bag.Exif.OrderBy(t => t.Id).ToList();
        var gps = bag.Gps.OrderBy(t => t.Id).ToList();
        if (exif.Count > 0)
            root.Add(new Tag { Id = 0x8769, Type = 4, Data = new byte[4] });
        if (gps.Count > 0)
            root.Add(new Tag { Id = 0x8825, Type = 4, Data = new byte[4] });
        root = root.OrderBy(t => t.Id).ToList();

        int at = 8;
        int rootAt = at;
        at += 2 + root.Count * 12 + 4;
        int exifAt = exif.Count > 0 ? at : 0;
        if (exif.Count > 0)
            at += 2 + exif.Count * 12 + 4;
        int gpsAt = gps.Count > 0 ? at : 0;
        if (gps.Count > 0)
            at += 2 + gps.Count * 12 + 4;
        var blobs = new List<byte[]>();
        var output = new byte[at + BlobBytes(root) + BlobBytes(exif) + BlobBytes(gps) + 8];
        output[0] = (byte)'I';
        output[1] = (byte)'I';
        output[2] = 42;
        WriteU32(output, 4, rootAt);
        int dataStart = at;
        WriteIfd(output, rootAt, root, ref at, blobs);
        if (exif.Count > 0)
            WriteIfd(output, exifAt, exif, ref at, blobs);
        if (gps.Count > 0)
            WriteIfd(output, gpsAt, gps, ref at, blobs);
        PatchOffset(output, rootAt, root, 0x8769, exifAt);
        PatchOffset(output, rootAt, root, 0x8825, gpsAt);
        int cursor = dataStart;
        foreach (byte[] blob in blobs)
        {
            blob.CopyTo(output, cursor);
            cursor += blob.Length;
        }
        if (cursor != output.Length)
            Array.Resize(ref output, cursor);
        return output;
    }

    static int BlobBytes(List<Tag> tags)
    {
        int n = 0;
        foreach (var tag in tags)
        {
            if (tag.Id is 0x8769 or 0x8825)
                continue;
            if (tag.Data.Length > 4)
                n += tag.Data.Length + (tag.Data.Length & 1);
        }
        return n;
    }

    static void WriteIfd(byte[] output, int at, List<Tag> tags, ref int dataAt, List<byte[]> blobs)
    {
        WriteU16(output, at, (ushort)tags.Count);
        int entry = at + 2;
        foreach (var tag in tags)
        {
            WriteU16(output, entry, tag.Id);
            WriteU16(output, entry + 2, tag.Type);
            int count = tag.Id is 0x8769 or 0x8825 ? 1 : Math.Max(1, tag.Data.Length / Math.Max(1, TypeSize(tag.Type)));
            if (tag.Type == 2)
                count = tag.Data.Length;
            if (tag.Type == 1 || tag.Type == 7)
                count = tag.Data.Length;
            WriteU32(output, entry + 4, count);
            if (tag.Id is 0x8769 or 0x8825 || tag.Data.Length <= 4)
            {
                var inline = new byte[4];
                if (tag.Id is not (0x8769 or 0x8825))
                    Buffer.BlockCopy(tag.Data, 0, inline, 0, tag.Data.Length);
                Buffer.BlockCopy(inline, 0, output, entry + 8, 4);
            }
            else
            {
                WriteU32(output, entry + 8, dataAt);
                blobs.Add(tag.Data);
                dataAt += tag.Data.Length + (tag.Data.Length & 1);
                if ((tag.Data.Length & 1) != 0)
                    blobs.Add(new byte[] { 0 });
            }
            entry += 12;
        }
        WriteU32(output, entry, 0);
    }

    static void PatchOffset(byte[] output, int ifdAt, List<Tag> tags, ushort id, int offset)
    {
        if (offset == 0)
            return;
        for (int i = 0; i < tags.Count; i++)
        {
            if (tags[i].Id != id)
                continue;
            WriteU32(output, ifdAt + 2 + i * 12 + 8, offset);
        }
    }

    static void SetXp(List<Tag> tags, ushort id, string text)
    {
        tags.RemoveAll(t => t.Id == id);
        if (string.IsNullOrWhiteSpace(text))
            return;
        byte[] data = Encoding.Unicode.GetBytes(text + "\0");
        tags.Add(new Tag { Id = id, Type = 1, Data = data });
    }

    static void SetAscii(List<Tag> tags, ushort id, string text)
    {
        tags.RemoveAll(t => t.Id == id);
        if (string.IsNullOrEmpty(text))
            return;
        tags.Add(new Tag { Id = id, Type = 2, Data = Encoding.ASCII.GetBytes(text + "\0") });
    }

    static string Xp(List<Tag> tags, ushort id)
    {
        Tag? tag = tags.Find(t => t.Id == id);
        if (tag == null || tag.Data.Length < 2)
            return "";
        string text = Encoding.Unicode.GetString(tag.Data);
        return text.TrimEnd('\0', ' ');
    }

    static string Ascii(List<Tag> tags, ushort id)
    {
        Tag? tag = tags.Find(t => t.Id == id);
        if (tag == null || tag.Data.Length == 0)
            return "";
        return Encoding.Latin1.GetString(tag.Data).TrimEnd('\0', ' ');
    }

    static string UserText(List<Tag> tags, ushort id)
    {
        Tag? tag = tags.Find(t => t.Id == id);
        if (tag == null || tag.Data.Length <= 8)
            return "";
        string code = Encoding.ASCII.GetString(tag.Data, 0, 8);
        byte[] rest = tag.Data[8..];
        if (code.StartsWith("UNICODE", StringComparison.Ordinal))
            return Encoding.BigEndianUnicode.GetString(rest).TrimEnd('\0');
        return Encoding.Latin1.GetString(rest).TrimEnd('\0', ' ');
    }

    static string First(string a, string b) => a.Length > 0 ? a : b;

    static string Join(string a, string b)
    {
        if (a.Length == 0)
            return b;
        if (b.Length == 0)
            return a;
        return a + " " + b;
    }

    static string AsciiOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        foreach (char c in text)
        {
            if (c > 127)
                return "";
        }
        return text.Trim();
    }

    static string ExifDate(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return "";
        string[] shapes = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy-MM-dd" };
        if (DateTime.TryParseExact(text, shapes, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            return when.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
        return "";
    }

    static string ShowDate(string text)
    {
        if (text.Length == 0)
            return "";
        if (DateTime.TryParseExact(text.Trim(), new[] { "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            return when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return text.Trim();
    }

    static string ShortText(List<Tag> tags, ushort id)
    {
        Tag? tag = tags.Find(t => t.Id == id);
        if (tag == null || tag.Data.Length < 2)
            return "";
        return ReadU16(tag.Data, 0, true).ToString(CultureInfo.InvariantCulture);
    }

    static string RationalText(List<Tag> tags, ushort id, bool shutter)
    {
        Tag? tag = tags.Find(t => t.Id == id);
        if (tag == null || tag.Data.Length < 8)
            return "";
        uint num = (uint)ReadU32(tag.Data, 0, true);
        uint den = (uint)ReadU32(tag.Data, 4, true);
        if (den == 0)
            return "";
        if (shutter && num < den)
            return "1/" + Math.Max(1, (int)Math.Round(den / (double)num)) + " с";
        double value = num / (double)den;
        return value.ToString(value >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture);
    }

    static string GpsText(Bag bag)
    {
        if (bag.Gps.Count == 0)
            return "";
        string lat = Coord(bag.Gps, 2, Ascii(bag.Gps, 1));
        string lon = Coord(bag.Gps, 4, Ascii(bag.Gps, 3));
        if (lat.Length == 0 || lon.Length == 0)
            return "";
        return lat + ", " + lon;
    }

    static string Coord(List<Tag> tags, ushort id, string way)
    {
        Tag? tag = tags.Find(t => t.Id == id);
        if (tag == null || tag.Data.Length < 24)
            return "";
        double deg = Ratio(tag.Data, 0) + Ratio(tag.Data, 8) / 60 + Ratio(tag.Data, 16) / 3600;
        if (way is "S" or "W")
            deg = -deg;
        return deg.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    static double Ratio(byte[] data, int at)
    {
        uint den = (uint)ReadU32(data, at + 4, true);
        if (den == 0)
            return 0;
        return ReadU32(data, at, true) / (double)den;
    }

    static void TakeText(string type, byte[] data, PhotoNote note)
    {
        string key = TextKey(type, data);
        string value = TextValue(type, data);
        if (key.Length == 0 || value.Length == 0)
            return;
        if (key.Equals("Title", StringComparison.OrdinalIgnoreCase) && note.Title.Length == 0)
            note.Title = value;
        else if (key.Equals("Author", StringComparison.OrdinalIgnoreCase) && note.Author.Length == 0)
            note.Author = value;
        else if ((key.Equals("Description", StringComparison.OrdinalIgnoreCase) || key.Equals("Comment", StringComparison.OrdinalIgnoreCase)) && note.Description.Length == 0)
            note.Description = value;
        else if (key.Equals("Keywords", StringComparison.OrdinalIgnoreCase) && note.Keywords.Length == 0)
            note.Keywords = value;
        else if (key.Equals("Copyright", StringComparison.OrdinalIgnoreCase) && note.Copyright.Length == 0)
            note.Copyright = value;
        else if (key.Equals("Taken", StringComparison.OrdinalIgnoreCase) && note.Taken.Length == 0)
            note.Taken = value;
    }

    static string TextKey(string type, byte[] data)
    {
        int cut = Array.IndexOf(data, (byte)0);
        if (cut <= 0)
            return "";
        return Encoding.Latin1.GetString(data, 0, cut);
    }

    static string TextValue(string type, byte[] data)
    {
        int cut = Array.IndexOf(data, (byte)0);
        if (cut < 0)
            return "";
        if (type == "tEXt")
            return Encoding.Latin1.GetString(data, cut + 1, data.Length - cut - 1);
        if (type == "zTXt")
        {
            if (cut + 2 >= data.Length)
                return "";
            return Encoding.UTF8.GetString(Inflate(data[(cut + 2)..]));
        }
        int p = cut + 3;
        if (p >= data.Length)
            return "";
        int lang = Array.IndexOf(data, (byte)0, p);
        if (lang < 0)
            return "";
        int translated = Array.IndexOf(data, (byte)0, lang + 1);
        if (translated < 0 || translated + 1 > data.Length)
            return "";
        byte[] text = data[(translated + 1)..];
        if (data[cut + 1] == 1)
            text = Inflate(text);
        return Encoding.UTF8.GetString(text);
    }

    static byte[] Inflate(byte[] data)
    {
        if (data.Length < 6)
            return Array.Empty<byte>();
        using var input = new MemoryStream(data, 2, data.Length - 6);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    static bool IsOurKey(string key) =>
        key.Equals("Title", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Author", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Description", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Comment", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Keywords", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Copyright", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Taken", StringComparison.OrdinalIgnoreCase);

    static void WriteTexts(Stream output, PhotoNote note)
    {
        WriteOne(output, "Title", note.Title);
        WriteOne(output, "Author", note.Author);
        WriteOne(output, "Description", note.Description);
        WriteOne(output, "Keywords", note.Keywords);
        WriteOne(output, "Copyright", note.Copyright);
        WriteOne(output, "Taken", note.Taken);
    }

    static void WriteOne(Stream output, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        var data = new MemoryStream();
        data.Write(Encoding.ASCII.GetBytes(key));
        data.WriteByte(0);
        data.WriteByte(0);
        data.WriteByte(0);
        data.WriteByte(0);
        data.WriteByte(0);
        data.Write(Encoding.UTF8.GetBytes(value.Trim()));
        output.Write(Chunk("iTXt", data.ToArray()));
    }

    readonly record struct PngChunk(string Type, byte[] Data);

    static IEnumerable<PngChunk> PngChunks(byte[] file)
    {
        int p = 8;
        while (p + 12 <= file.Length)
        {
            int size = (file[p] << 24) | (file[p + 1] << 16) | (file[p + 2] << 8) | file[p + 3];
            if (size < 0 || p + 12 + size > file.Length)
                yield break;
            string type = Encoding.ASCII.GetString(file, p + 4, 4);
            byte[] data = file[(p + 8)..(p + 8 + size)];
            yield return new PngChunk(type, data);
            p += 12 + size;
            if (type == "IEND")
                yield break;
        }
    }

    static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        chunk[0] = (byte)(data.Length >> 24);
        chunk[1] = (byte)(data.Length >> 16);
        chunk[2] = (byte)(data.Length >> 8);
        chunk[3] = (byte)data.Length;
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        uint crc = Crc(chunk, 4, 4 + data.Length);
        int c = 8 + data.Length;
        chunk[c] = (byte)(crc >> 24);
        chunk[c + 1] = (byte)(crc >> 16);
        chunk[c + 2] = (byte)(crc >> 8);
        chunk[c + 3] = (byte)crc;
        return chunk;
    }

    static uint Crc(byte[] data, int at, int len)
    {
        uint c = 0xFFFFFFFF;
        for (int i = 0; i < len; i++)
        {
            c ^= data[at + i];
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    static void SwapToLe(byte[] data, ushort type)
    {
        int unit = TypeSize(type);
        if (unit <= 1)
            return;
        for (int i = 0; i + unit <= data.Length; i += unit)
            Array.Reverse(data, i, unit);
    }

    static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        5 or 10 or 12 => 8,
        _ => 1
    };

    static bool HasAscii(byte[] file, int at, string text)
    {
        if (at < 0 || at + text.Length > file.Length)
            return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (file[at + i] != (byte)text[i])
                return false;
        }
        return true;
    }

    static int ReadLe(byte[] file, int at) => file[at] | (file[at + 1] << 8) | (file[at + 2] << 16) | (file[at + 3] << 24);

    static ushort ReadU16(byte[] file, int at, bool le) =>
        le ? (ushort)(file[at] | (file[at + 1] << 8)) : (ushort)((file[at] << 8) | file[at + 1]);

    static int ReadU32(byte[] file, int at, bool le) =>
        le
            ? file[at] | (file[at + 1] << 8) | (file[at + 2] << 16) | (file[at + 3] << 24)
            : (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3];

    static void WriteU16(byte[] file, int at, ushort value)
    {
        file[at] = (byte)value;
        file[at + 1] = (byte)(value >> 8);
    }

    static void WriteU32(byte[] file, int at, int value)
    {
        file[at] = (byte)value;
        file[at + 1] = (byte)(value >> 8);
        file[at + 2] = (byte)(value >> 16);
        file[at + 3] = (byte)(value >> 24);
    }
}
