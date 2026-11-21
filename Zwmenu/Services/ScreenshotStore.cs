using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Input;

namespace Zwmenu.Services;

public sealed class ScreenshotSettings
{
    /// <summary>Виртуальная клавиша снимка. По умолчанию F4.</summary>
    public int Hotkey { get; set; } = 0x73;

    /// <summary>Виртуальная клавиша короткого клипа. По умолчанию F8.</summary>
    public int ClipHotkey { get; set; } = 0x77;

    /// <summary>Клавиша перевода буфера. По умолчанию F10. Обычное копирование её не касается.</summary>
    public int TranslateHotkey { get; set; } = 0x79;

    /// <summary>Сектор «Перевод» уже предложен этому меню. Повторно не вставляем, если его убрали.</summary>
    public bool TranslateWheelSeeded { get; set; }

    /// <summary>Сектор «Экраны» уже предложен. Если его убрали, обратно не вставляем.</summary>
    public bool EdgeWheelSeeded { get; set; }

    /// <summary>Сектор «Шаблоны» уже предложен. Если его убрали, обратно не вставляем.</summary>
    public bool SnippetWheelSeeded { get; set; }

    public bool VoiceRecWheelSeeded { get; set; }

    /// <summary>Сектор «Скрыть иконки» уже предложен. Если его убрали, обратно не вставляем.</summary>
    public bool DesktopIconsWheelSeeded { get; set; }

    /// <summary>Сектор «Полка» уже предложен. Если его убрали, обратно не вставляем.</summary>
    public bool ShelfWheelSeeded { get; set; }

    /// <summary>Сектор «Мастерская» уже предложен. Если его убрали, обратно не вставляем.</summary>
    public bool WorkshopWheelSeeded { get; set; }

    /// <summary>Мастер: применять цепочку эффектов (иначе только усиление/монитор).</summary>
    public bool VoiceRecEnhance { get; set; } = true;

    /// <summary>Копировать путь к файлу в буфер после сохранения.</summary>
    public bool VoiceRecCopyPath { get; set; } = true;

    /// <summary>Папка для записей. Пусто = рабочий стол.</summary>
    public string VoiceRecFolder { get; set; } = "";

    /// <summary>Максимальная длина одной записи, минуты.</summary>
    public int VoiceRecMaxMinutes { get; set; } = 30;

    /// <summary>Входное усиление, дБ (−12…+18).</summary>
    public float VoiceRecInputGainDb { get; set; }

    public bool VoiceRecHighPass { get; set; } = true;
    /// <summary>Частота среза гула, Гц (40…160).</summary>
    public float VoiceRecHighPassHz { get; set; } = 75;

    public bool VoiceRecGate { get; set; } = true;
    /// <summary>Порог шумового гейта, дБ (−70…−30).</summary>
    public float VoiceRecGateDb { get; set; } = -52;

    public bool VoiceRecCompress { get; set; } = true;
    /// <summary>Сила компрессии 0…100.</summary>
    public float VoiceRecCompressStrength { get; set; } = 55;

    public bool VoiceRecPresence { get; set; } = true;
    /// <summary>Ясность речи, дБ (0…8).</summary>
    public float VoiceRecPresenceDb { get; set; } = 2.8f;

    public bool VoiceRecWarmth { get; set; } = true;
    /// <summary>Теплота низа, дБ (0…6).</summary>
    public float VoiceRecWarmthDb { get; set; } = 1.8f;

    public bool VoiceRecDeEsser { get; set; } = true;
    public bool VoiceRecLimiter { get; set; } = true;
    /// <summary>Автоуровень во время записи.</summary>
    public bool VoiceRecAgc { get; set; }

    public bool VoiceRecNormalize { get; set; } = true;
    public bool VoiceRecTrimSilence { get; set; } = true;
    /// <summary>Фейд на краях, мс (0…50).</summary>
    public int VoiceRecFadeMs { get; set; } = 12;

    /// <summary>Громкость «слушать себя», % (10…100).</summary>
    public int VoiceRecMonitorVolume { get; set; } = 55;

    /// <summary>Не писать первые мс после старта (клик UI).</summary>
    public int VoiceRecPreRollMs { get; set; } = 220;

    /// <summary>Разрядность WAV: 16 или 24 (совместимость; смотри VoiceRecFormat).</summary>
    public int VoiceRecBitDepth { get; set; } = 16;

    /// <summary>Формат файла: wav16, wav24, mp3, m4a, flac, ogg.</summary>
    public string VoiceRecFormat { get; set; } = "mp3";

    /// <summary>Тон изменителя голоса. 0 — свой.</summary>
    public int VoiceShiftPreset { get; set; }

    /// <summary>Имя своего файла onnx, если выбран такой голос.</summary>
    public string VoiceShiftRvc { get; set; } = "";

    /// <summary>Сдвиг тона в полутонах, от -15 до 15.</summary>
    public float VoiceShiftSemi { get; set; }

    /// <summary>Громкость изменённого голоса, 1 — как вошло.</summary>
    public float VoiceShiftGain { get; set; } = 1f;

    /// <summary>Слышать изменённый голос в наушниках, пока он уходит в кабель.</summary>
    public bool VoiceShiftHear { get; set; }

    /// <summary>Отдача включена и поднимается снова при старте Zwmenu.</summary>
    public bool VoiceShiftLiveOn { get; set; }

    /// <summary>Клавиша, которая включает и выключает отдачу. 0 — нет.</summary>
    public int VoiceShiftToggleKey { get; set; }

    /// <summary>Пользователь отложил предложение поставить кабель. При включении голоса спросим снова.</summary>
    public bool SetupCableLater { get; set; }

    /// <summary>Куда отдавать изменённый голос. Пусто — виртуальный кабель, если он есть.</summary>
    public string VoiceShiftOutId { get; set; } = "";

    /// <summary>Какой физический микрофон брать. Пусто — тот, что Windows считает обычным.</summary>
    public string VoiceShiftMicId { get; set; } = "";

    /// <summary>Усиление входа до обработки. 1 — как пришло с микрофона.</summary>
    public float VoiceShiftBoost { get; set; } = 1f;

    /// <summary>Порог тишины. 0.007 как раньше, 0 выключает приглушение шума.</summary>
    public float VoiceShiftGate { get; set; } = 0.007f;

    /// <summary>Срез низкого гула, Гц.</summary>
    public float VoiceShiftLowCut { get; set; } = 90f;

    /// <summary>Насколько выравнивать громкие всплески. 0.62 как раньше, 0 без выравнивания.</summary>
    public float VoiceShiftComp { get; set; } = 0.62f;

    /// <summary>Громкость «слышать себя», 0.72 как раньше. На кабель в звонок не влияет.</summary>
    public float VoiceShiftHearGain { get; set; } = 0.72f;

    /// <summary>Куда играть «слышать себя». Пусто — первые обычные колонки или наушники.</summary>
    public string VoiceShiftHearId { get; set; } = "";

    /// <summary>Запас буфера, мс. 0 — само (90, а для голоса из библиотеки 280).</summary>
    public int VoiceShiftLatencyMs { get; set; }

    /// <summary>Микрофон системы до переключения. Возвращаем его, когда отдача гаснет.</summary>
    public string VoiceShiftPrevMic { get; set; } = "";

    /// <summary>Микрофон связи до переключения.</summary>
    public string VoiceShiftPrevComm { get; set; } = "";

    /// <summary>Старый номер устройства вывода. Остался для старых настроек.</summary>
    public int VoiceShiftOutDevice { get; set; } = -1;

    /// <summary>Группа «Запуск» (частые программы) уже предложена.</summary>
    /// <summary>
    /// Боковая кнопка диктовки: 0 — не использовать, 1 — XButton1 (назад), 2 — XButton2 (вперёд).
    /// Пока зажата — слушает микрофон, отпустил — текст в активное окно.
    /// </summary>
    public int DictationSideButton { get; set; } = 1;

    /// <summary>
    /// Клавиша удержания для диктовки (VK). 0 — только мышь. Зажал — говори, отпустил — вставка.
    /// Можно вместе с боковой кнопкой.
    /// </summary>
    public int DictationHotkey { get; set; }

    /// <summary>Индекс микрофона WaveIn. -1 — устройство по умолчанию (0).</summary>
    public int DictationMicDevice { get; set; } = -1;

    /// <summary>Плашка «Слушаю…» у курсора во время диктовки.</summary>
    public bool DictationShowHud { get; set; } = true;

    /// <summary>После распознавания сразу Ctrl+V в активное окно. Иначе только буфер.</summary>
    public bool DictationAutoPaste { get; set; } = true;

    /// <summary>Устарело: тосты диктовки убраны. Поле оставлено для совместимости json.</summary>
    public bool DictationToast { get; set; }

    /// <summary>Первая буква заглавная.</summary>
    public bool DictationCapitalize { get; set; } = true;

    /// <summary>Пробел в конце текста — чтобы следующая фраза не слипалась.</summary>
    public bool DictationTrailingSpace { get; set; } = true;

    /// <summary>Фразы вроде «открой телеграм» выполнять как команду, а не вставлять текстом.</summary>
    public bool DictationVoiceCommands { get; set; } = true;

    /// <summary>
    /// Язык распознавания: 0 — суржик (RU+UK сразу, выбрать лучшее),
    /// 1 — только русский, 2 — только украинский.
    /// </summary>
    public int DictationLangMode { get; set; }

    /// <summary>Короткое нажатие без речи не показывает ошибку (мс). 0 — всегда сообщать.</summary>
    public int DictationQuietMs { get; set; } = 280;

    /// <summary>Буфер микрофона, мс. Больше — меньше CPU, чуть больше задержка.</summary>
    public int DictationBufferMs { get; set; } = 250;

    /// <summary>Как часто обновлять плашку partial, мс.</summary>
    public int DictationHudMs { get; set; } = 120;

    /// <summary>Пауза перед Ctrl+V после записи в буфер, мс.</summary>
    public int DictationPasteDelayMs { get; set; } = 50;

    /// <summary>Короткий звук при старте записи и при отпускании.</summary>
    public bool DictationSounds { get; set; } = true;

    /// <summary>Длина клипа, если его не остановить раньше: 5, 10 или 15 секунд.</summary>
    public int ClipSeconds { get; set; } = 10;

    /// <summary>Звук клипа: silent, system, mic или both.</summary>
    public string ClipAudio { get; set; } = "silent";

    public bool ShowNotifications { get; set; } = true;

    /// <summary>Следующий снимок открывает прошлую область, а не весь монитор.</summary>
    public bool RememberRegion { get; set; }

    public bool CaptureCursor { get; set; }

    /// <summary>png или jpeg.</summary>
    public string Format { get; set; } = "png";

    /// <summary>Качество JPEG, 40–100. PNG всегда без потерь.</summary>
    public int Quality { get; set; } = 92;

    public bool HasRegion { get; set; }

    public string? MonitorDevice { get; set; }

    public int RegionX { get; set; }

    public int RegionY { get; set; }

    public int RegionW { get; set; }

    public int RegionH { get; set; }
}

/// <summary>Настройки снимка экрана: %LOCALAPPDATA%\Zwmenu\screenshot.json.</summary>
public static class ScreenshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ScreenshotSettings _cache = new();
    private static bool _loaded;

    /// <summary>Пока true, глобальная клавиша снимка не перехватывается (назначение в настройках).</summary>
    public static bool SuspendHotkey { get; set; }

    /// <summary>Пока true, боковые кнопки не глотаются хуком — окно настроек может их назначить.</summary>
    public static bool SuspendDictationCapture { get; set; }

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu");

    public static string FilePath => Path.Combine(DirectoryPath, "screenshot.json");

    public static ScreenshotSettings Current
    {
        get
        {
            if (!_loaded)
                Reload();
            return _cache;
        }
    }

    public static ScreenshotSettings Reload()
    {
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath))
            {
                _cache = new ScreenshotSettings();
                return _cache;
            }

            string json = File.ReadAllText(FilePath);
            _cache = JsonSerializer.Deserialize<ScreenshotSettings>(json, Options) ?? new ScreenshotSettings();
            Normalize(_cache);
            return _cache;
        }
        catch
        {
            _cache = new ScreenshotSettings();
            return _cache;
        }
    }

    public static void Save(ScreenshotSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(DirectoryPath);
        string json = JsonSerializer.Serialize(settings, Options);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
        _cache = settings;
        _loaded = true;
    }

    public static void SaveRegion(string device, int x, int y, int w, int h)
    {
        var s = Current;
        s.HasRegion = w >= 8 && h >= 8;
        s.MonitorDevice = device;
        s.RegionX = Math.Max(0, x);
        s.RegionY = Math.Max(0, y);
        s.RegionW = Math.Max(0, w);
        s.RegionH = Math.Max(0, h);
        Save(s);
    }

    public static bool IsAllowedHotkey(int vk)
    {
        if (vk is < 0x08 or > 0xFE)
            return false;

        // Модификаторы, Esc и Win не назначаем: Esc отменяет выделение.
        return vk is not (0x10 or 0x11 or 0x12 or 0x1B or 0x5B or 0x5C
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5);
    }

    public static string HotkeyTitle(int vk)
    {
        if (vk is >= 0x70 and <= 0x87)
            return "F" + (vk - 0x6F);
        if (vk == 0x2C)
            return "Print Screen";
        if (vk == 0x2D)
            return "Insert";
        if (vk == 0x2E)
            return "Delete";
        if (vk == 0x13)
            return "Pause";
        if (vk == 0x91)
            return "Scroll Lock";
        if (vk == 0xA6)
            return "Назад (мышь/браузер)";
        if (vk == 0xA7)
            return "Вперёд (мышь/браузер)";
        if (vk is >= 0x30 and <= 0x39)
            return ((char)vk).ToString();
        if (vk is >= 0x41 and <= 0x5A)
            return ((char)vk).ToString();

        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key == Key.None ? "Клавиша " + vk : key.ToString();
    }

    private static void Normalize(ScreenshotSettings s)
    {
        if (!IsAllowedHotkey(s.Hotkey))
            s.Hotkey = 0x73;
        if (!IsAllowedHotkey(s.ClipHotkey) || s.ClipHotkey == s.Hotkey)
            s.ClipHotkey = s.Hotkey == 0x77 ? 0x78 : 0x77;
        if (!IsAllowedHotkey(s.TranslateHotkey)
            || s.TranslateHotkey == s.Hotkey
            || s.TranslateHotkey == s.ClipHotkey)
            s.TranslateHotkey = FreeHotkey(s.Hotkey, s.ClipHotkey);
        if (s.ClipSeconds is not (5 or 10 or 15))
            s.ClipSeconds = 10;
        s.ClipAudio = s.ClipAudio?.ToLowerInvariant() switch
        {
            "system" or "mic" or "both" => s.ClipAudio.ToLowerInvariant(),
            _ => "silent"
        };
        s.Format = string.Equals(s.Format, "jpeg", StringComparison.OrdinalIgnoreCase) ? "jpeg" : "png";
        s.Quality = Math.Clamp(s.Quality == 0 ? 92 : s.Quality, 40, 100);
        if (s.RegionW < 8 || s.RegionH < 8)
            s.HasRegion = false;
        if (s.RegionX < 0)
            s.RegionX = 0;
        if (s.RegionY < 0)
            s.RegionY = 0;
        if (s.DictationSideButton is not (0 or 1 or 2))
            s.DictationSideButton = 1;
        if (s.DictationHotkey != 0 && !IsAllowedHotkey(s.DictationHotkey))
            s.DictationHotkey = 0;
        // Не пересекаться со снимком / клипом / переводом.
        if (s.DictationHotkey != 0
            && (s.DictationHotkey == s.Hotkey
                || s.DictationHotkey == s.ClipHotkey
                || s.DictationHotkey == s.TranslateHotkey))
            s.DictationHotkey = 0;
        if (s.DictationMicDevice < -1)
            s.DictationMicDevice = -1;
        if (s.DictationLangMode is not (0 or 1 or 2))
            s.DictationLangMode = 0;
        s.DictationQuietMs = Math.Clamp(s.DictationQuietMs, 0, 1500);
        s.VoiceRecMaxMinutes = Math.Clamp(s.VoiceRecMaxMinutes, 1, 180);
        s.VoiceRecFolder ??= "";
        s.VoiceRecInputGainDb = Math.Clamp(s.VoiceRecInputGainDb, -12f, 18f);
        s.VoiceRecHighPassHz = Math.Clamp(s.VoiceRecHighPassHz, 40f, 160f);
        s.VoiceRecGateDb = Math.Clamp(s.VoiceRecGateDb, -70f, -30f);
        s.VoiceRecCompressStrength = Math.Clamp(s.VoiceRecCompressStrength, 0f, 100f);
        s.VoiceRecPresenceDb = Math.Clamp(s.VoiceRecPresenceDb, 0f, 8f);
        s.VoiceRecWarmthDb = Math.Clamp(s.VoiceRecWarmthDb, 0f, 6f);
        s.VoiceRecFadeMs = Math.Clamp(s.VoiceRecFadeMs, 0, 50);
        s.VoiceRecMonitorVolume = Math.Clamp(s.VoiceRecMonitorVolume, 10, 100);
        s.VoiceRecPreRollMs = Math.Clamp(s.VoiceRecPreRollMs, 0, 500);
        if (s.VoiceRecBitDepth is not (16 or 24))
            s.VoiceRecBitDepth = 16;
        s.VoiceRecFormat = NormalizeVoiceRecFormat(s.VoiceRecFormat, s.VoiceRecBitDepth);
        s.VoiceRecBitDepth = s.VoiceRecFormat is "wav24" ? 24 : 16;
        s.VoiceShiftSemi = Math.Clamp(s.VoiceShiftSemi, -15f, 15f);
        if (s.VoiceShiftGain < 0.4f || s.VoiceShiftGain > 2.2f)
            s.VoiceShiftGain = 1f;
        if (s.VoiceShiftPreset < 0)
            s.VoiceShiftPreset = 0;
        if (s.VoiceShiftToggleKey != 0 && !IsAllowedHotkey(s.VoiceShiftToggleKey))
            s.VoiceShiftToggleKey = 0;
        s.VoiceShiftOutId ??= "";
        s.VoiceShiftMicId ??= "";
        if (s.VoiceShiftBoost < 0.4f || s.VoiceShiftBoost > 4f)
            s.VoiceShiftBoost = 1f;
        if (s.VoiceShiftGate < 0f || s.VoiceShiftGate > 0.04f)
            s.VoiceShiftGate = 0.007f;
        if (s.VoiceShiftLowCut < 40f || s.VoiceShiftLowCut > 220f)
            s.VoiceShiftLowCut = 90f;
        if (s.VoiceShiftComp < 0f || s.VoiceShiftComp > 1.2f)
            s.VoiceShiftComp = 0.62f;
        if (s.VoiceShiftHearGain < 0f || s.VoiceShiftHearGain > 1f)
            s.VoiceShiftHearGain = 0.72f;
        s.VoiceShiftHearId ??= "";
        if (s.VoiceShiftLatencyMs < 0 || s.VoiceShiftLatencyMs > 220)
            s.VoiceShiftLatencyMs = 0;
        else if (s.VoiceShiftLatencyMs is > 0 and < 40)
            s.VoiceShiftLatencyMs = 40;
        s.VoiceShiftPrevMic ??= "";
        s.VoiceShiftPrevComm ??= "";
        s.DictationBufferMs = Math.Clamp(s.DictationBufferMs, 100, 500);
        s.DictationHudMs = Math.Clamp(s.DictationHudMs, 50, 500);
        s.DictationPasteDelayMs = Math.Clamp(s.DictationPasteDelayMs, 0, 500);
    }

    /// <summary>Диктовка включена, если назначена боковая кнопка и/или клавиша удержания.</summary>
    public static bool DictationEnabled
    {
        get
        {
            var s = Current;
            return s.DictationSideButton is 1 or 2
                || (s.DictationHotkey != 0 && IsAllowedHotkey(s.DictationHotkey));
        }
    }

    public static string NormalizeVoiceRecFormat(string? format, int bitDepthFallback = 16)
    {
        string f = (format ?? "").Trim().ToLowerInvariant();
        return f switch
        {
            "wav" or "wav16" or "16" => "wav16",
            "wav24" or "24" => "wav24",
            "mp3" => "mp3",
            "m4a" or "aac" or "mp4" => "m4a",
            "flac" => "flac",
            "ogg" or "oga" or "vorbis" => "ogg",
            // Пустое значение из старых настроек: оставляем WAV как раньше.
            _ => bitDepthFallback >= 24 ? "wav24" : "wav16"
        };
    }

    public static string VoiceRecExtension(string format) =>
        NormalizeVoiceRecFormat(format) switch
        {
            "wav24" or "wav16" => ".wav",
            "m4a" => ".m4a",
            "flac" => ".flac",
            "ogg" => ".ogg",
            _ => ".mp3"
        };

    /// <summary>Реальный индекс WaveIn: -1 в настройках → 0.</summary>
    public static int ResolveMicDevice(int configured)
    {
        int n = 0;
        try { n = NAudio.Wave.WaveIn.DeviceCount; } catch { return 0; }
        if (n <= 0)
            return 0;
        int device = configured < 0 ? 0 : configured;
        return Math.Clamp(device, 0, n - 1);
    }

    /// <summary>F10, затем соседние F, лишь бы не снимок и не клип.</summary>
    private static int FreeHotkey(int shot, int clip)
    {
        int[] order = { 0x79, 0x7A, 0x7B, 0x78, 0x76, 0x75, 0x74, 0x72 };
        foreach (int key in order)
        {
            if (key != shot && key != clip)
                return key;
        }

        return 0x79;
    }
}
