using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using NAudio.Wave;
using Vosk;
using Zwmenu.Overlay;
using Zwmenu.Workout;

namespace Zwmenu.Services;

/// <summary>
/// Диктовка по удержанию.
/// Жёсткие правила против 0xc0000005:
/// 1) Все операции с WaveIn/Vosk идут через _micMutex (нет параллельного Start/Stop/Dispose).
/// 2) AcceptWaveform только под _gate и только пока !_finishing.
/// 3) Пока _starting — EndHold не вызывает Finish (StartMic сам завершит).
/// 4) С хука мыши сюда только через Task.Run (см. LauncherService).
/// </summary>
public sealed class DictationService : IDisposable
{
    private const int SampleRate = 16000;
    private const int TripleTapWindowMs = 1100;
    private const int WaveDrainMs = 80;

    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _micMutex = new(1, 1);

    private WaveInEvent? _waveIn;
    private VoskRecognizer? _recognizerRu;
    private VoskRecognizer? _recognizerUk;
    private int _session;
    private int _micGen; // инвалидирует in-flight StartRecording
    private bool _holding;
    private bool _starting;
    private bool _finishing;
    private bool _disposed;
    private bool _skipNextUp;
    private long _recStarted;
    private long _lastHudTick;
    private int _hudMinIntervalMs = 120;
    private string _partialRu = "";
    private string _partialUk = "";
    private string _lastHud = "";
    private readonly StringBuilder _finalsRu = new();
    private readonly StringBuilder _finalsUk = new();
    private int _tapCount;
    private long _tapWindowStart;

    public DictationService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // Модели и распознаватели заранее: иначе первые секунды удержания теряются на создании Vosk.
        _ = Task.Run(WarmRecognizers);
    }

    private void WarmRecognizers()
    {
        try
        {
            Model ru = VoskModelStore.PreloadRuAsync().GetAwaiter().GetResult();
            Model? uk = null;
            try { uk = VoskModelStore.PreloadUkAsync().GetAwaiter().GetResult(); }
            catch { /* без UK диктовка остаётся на русском */ }

            RunMicExclusive(() =>
            {
                if (_disposed)
                    return;
                EnsureLiveRecognizers(ScreenshotStore.Current.DictationLangMode, ru, uk);
            });
        }
        catch
        {
            // первый StartMic поднимет сам
        }
    }

    public void ApplySettings()
    {
        if (_disposed)
            return;
        if (ScreenshotStore.DictationEnabled)
            return;
        EndHold();
    }

    public void TriggerDown()
    {
        if (_disposed || !ScreenshotStore.DictationEnabled)
            return;

        try
        {
            if (NoteTripleTapClears())
            {
                lock (_gate) _skipNextUp = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        CancelActiveSession();
                        DictationCue.PlayClear();
                        _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                        {
                            try
                            {
                                SnippetStore.ClearActiveField();
                                DictationHud.ShowCleared();
                            }
                            catch { /* UI */ }
                        }));
                    }
                    catch { /* ignore */ }
                });
                return;
            }

            BeginHold();
        }
        catch
        {
            // never throw to caller/hook
        }
    }

    public void TriggerUp()
    {
        try
        {
            lock (_gate)
            {
                if (_skipNextUp)
                {
                    _skipNextUp = false;
                    return;
                }
            }

            EndHold();
        }
        catch
        {
            // ignore
        }
    }

    private bool NoteTripleTapClears()
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_tapCount == 0 || now - _tapWindowStart > TripleTapWindowMs)
            {
                _tapWindowStart = now;
                _tapCount = 1;
                return false;
            }

            _tapCount++;
            _tapWindowStart = now;
            if (_tapCount < 3)
                return false;

            _tapCount = 0;
            return true;
        }
    }

    private void ResetTaps()
    {
        lock (_gate)
        {
            _tapCount = 0;
            _tapWindowStart = 0;
        }
    }

    public void BeginHold()
    {
        if (_disposed || !ScreenshotStore.DictationEnabled)
            return;

        int session;
        lock (_gate)
        {
            if (_holding || _starting || _finishing)
                return;
            _holding = true;
            _starting = true;
            _session++;
            session = _session;
            _recStarted = 0;
            _partialRu = "";
            _partialUk = "";
            _lastHud = "";
            _lastHudTick = 0;
            _hudMinIntervalMs = Math.Clamp(ScreenshotStore.Current.DictationHudMs, 50, 500);
            _finalsRu.Clear();
            _finalsUk.Clear();
        }

        PostHudListening();
        _ = Task.Run(() =>
        {
            try { StartMic(session); }
            catch
            {
                lock (_gate)
                {
                    if (session == _session)
                    {
                        _holding = false;
                        _starting = false;
                        _finishing = false;
                    }
                }
            }
        });
    }

    public void EndHold()
    {
        if (_disposed)
            return;

        int session;
        bool shouldFinish;
        lock (_gate)
        {
            if (!_holding)
                return;
            _holding = false;
            session = _session;

            // Пока StartMic поднимает устройство — только он имеет право Finish/Dispose.
            // Иначе гонка: Finish Dispose'ит wave, а StartMic тут же StartRecording → 0xc0000005.
            if (_starting)
            {
                shouldFinish = false;
            }
            else
            {
                _finishing = true;
                shouldFinish = true;
            }
        }

        if (shouldFinish)
            _ = Task.Run(() =>
            {
                try { FinishAndPaste(session); }
                catch { /* ignore */ }
            });
    }

    private void CancelActiveSession()
    {
        int session;
        lock (_gate)
        {
            _holding = false;
            _starting = false;
            _finishing = false;
            _recStarted = 0;
            _session++;
            session = _session - 1;
            _micGen++;
            _finalsRu.Clear();
            _finalsUk.Clear();
            _partialRu = "";
            _partialUk = "";
        }

        RunMicExclusive(() => CleanupMicUnlocked(session));
    }

    private void StartMic(int session)
    {
        RunMicExclusive(() => StartMicUnlocked(session));
    }

    private void FinishAndPaste(int session)
    {
        RunMicExclusive(() => FinishAndPasteUnlocked(session));
    }

    private void RunMicExclusive(Action action)
    {
        try
        {
            _micMutex.Wait();
        }
        catch
        {
            return;
        }

        try
        {
            if (_disposed)
                return;
            action();
        }
        finally
        {
            try { _micMutex.Release(); } catch { /* ignore */ }
        }
    }

    private void StartMicUnlocked(int session)
    {
        WaveInEvent? wave = null;

        try
        {
            bool stillWanted;
            lock (_gate)
            {
                stillWanted = !_disposed && session == _session && _holding;
                if (!stillWanted)
                {
                    _starting = false;
                    if (session == _session)
                        _finishing = false;
                    return;
                }
            }

            var cfg = ScreenshotStore.Current;
            int lang = cfg.DictationLangMode; // 0 auto, 1 ru, 2 uk
            Model? modelRu = null;
            Model? modelUk = null;
            if (lang is 0 or 1)
                modelRu = VoskModelStore.PreloadRuAsync().GetAwaiter().GetResult();
            if (lang is 0 or 2)
            {
                modelUk = VoskModelStore.PreloadUkAsync().GetAwaiter().GetResult();
                // Авто без UK — хотя бы RU.
                if (lang == 0 && modelUk == null)
                    modelRu ??= VoskModelStore.PreloadRuAsync().GetAwaiter().GetResult();
                if (lang == 2 && modelUk == null)
                    modelRu = VoskModelStore.PreloadRuAsync().GetAwaiter().GetResult();
            }

            lock (_gate)
            {
                stillWanted = !_disposed && session == _session && _holding;
                if (!stillWanted)
                {
                    _starting = false;
                    return;
                }
            }

            int device = ScreenshotStore.ResolveMicDevice(cfg.DictationMicDevice);
            // Короткий буфер: Vosk чаще отдаёт финальные куски, меньше «каши» на длинном окне.
            int bufferMs = Math.Clamp(Math.Min(cfg.DictationBufferMs, 120), 60, 120);

            EnsureLiveRecognizers(lang, modelRu, modelUk);
            lock (_gate)
            {
                if (_recognizerRu == null && _recognizerUk == null)
                    throw new InvalidOperationException("Нет модели распознавания речи.");
            }

            wave = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(SampleRate, 1),
                BufferMilliseconds = bufferMs,
                NumberOfBuffers = 3
            };
            wave.DataAvailable += OnData;

            WaveInEvent? staleWave;
            int gen;
            lock (_gate)
            {
                if (_disposed || session != _session || !_holding)
                {
                    _starting = false;
                    staleWave = null;
                    gen = -1;
                }
                else
                {
                    staleWave = _waveIn;
                    _waveIn = wave;
                    _finalsRu.Clear();
                    _finalsUk.Clear();
                    _partialRu = "";
                    _partialUk = "";
                    _micGen++;
                    gen = _micGen;
                    wave = null;
                    _starting = false;
                }
            }

            StopAndDisposeWave(staleWave);

            if (gen < 0)
            {
                StopAndDisposeWave(wave);
                return;
            }

            // Отпустил, пока создавали — тихо закрываем без «ошибки».
            bool holdingNow;
            WaveInEvent? live;
            lock (_gate)
            {
                holdingNow = _holding && session == _session && _micGen == gen;
                live = holdingNow ? _waveIn : null;
                if (!holdingNow)
                    _finishing = true;
            }

            if (!holdingNow || live == null)
            {
                FinishAndPasteUnlocked(session);
                return;
            }

            try
            {
                live.StartRecording();
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (session == _session)
                    {
                        _holding = false;
                        _starting = false;
                        _finishing = false;
                    }
                }

                CleanupMicUnlocked(session);
                string msg = ex.Message;
                try
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal,
                        new Action(() => DictationHud.ShowError("Микрофон: " + msg)));
                }
                catch { /* ignore */ }
                return;
            }

            lock (_gate)
            {
                if (session != _session || _micGen != gen || !_holding || !ReferenceEquals(_waveIn, live))
                {
                    // Отменили уже после StartRecording — гасим.
                    _finishing = true;
                }
                else
                {
                    _recStarted = Environment.TickCount64;
                    DictationCue.PlayStart();
                    PostHudListening();
                    return;
                }
            }

            FinishAndPasteUnlocked(session);
        }
        catch (Exception ex)
        {
            StopAndDisposeWave(wave);
            lock (_gate)
            {
                if (session == _session)
                {
                    _holding = false;
                    _starting = false;
                    _finishing = false;
                }
            }

            CleanupMicUnlocked(session);
            string msg = ex.Message;
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => DictationHud.ShowError("Микрофон: " + msg)));
            }
            catch { /* ignore */ }
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || e.Buffer == null)
            return;

        string? hudText = null;
        try
        {
            lock (_gate)
            {
                if (_disposed || !_holding || _finishing
                    || (_recognizerRu == null && _recognizerUk == null))
                    return;

                int session = _session;
                int hudMs = _hudMinIntervalMs;
                BoostQuietPcm(e.Buffer, e.BytesRecorded);
                FeedRecognizer(e.Buffer, e.BytesRecorded, _recognizerRu, _finalsRu, ref _partialRu);
                FeedRecognizer(e.Buffer, e.BytesRecorded, _recognizerUk, _finalsUk, ref _partialUk);

                if (session != _session || !_holding || _finishing)
                    return;

                long now = Environment.TickCount64;
                if (now - _lastHudTick < hudMs)
                    return;

                hudText = BuildText_NoLock();
                if (hudText == _lastHud)
                    return;
                _lastHud = hudText;
                _lastHudTick = now;
            }
        }
        catch
        {
            return;
        }

        if (hudText != null && ScreenshotStore.Current.DictationShowHud)
        {
            string copy = hudText;
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => DictationHud.ShowPartial(copy)));
            }
            catch { /* ignore */ }
        }
    }

    private static void FeedRecognizer(
        byte[] buffer,
        int bytes,
        VoskRecognizer? recognizer,
        StringBuilder finals,
        ref string partial)
    {
        if (recognizer == null)
            return;

        if (recognizer.AcceptWaveform(buffer, bytes))
        {
            string piece = ReadHeard(recognizer.Result()).Text;
            if (piece.Length == 0)
                return;
            if (finals.Length > 0)
                finals.Append(' ');
            finals.Append(piece);
            partial = "";
        }
        else
        {
            string p = ReadText(recognizer.PartialResult(), "partial");
            if (p.Length > 220)
                p = p[^220..];
            partial = p;
        }
    }

    private void FinishAndPasteUnlocked(int session)
    {
        WaveInEvent? wave;
        VoskRecognizer? recRu;
        VoskRecognizer? recUk;
        long recStarted;
        string finalsRu;
        string finalsUk;

        lock (_gate)
        {
            if (session != _session)
                return;

            _finishing = true;
            _holding = false;
            _starting = false;
            wave = _waveIn;
            recRu = _recognizerRu;
            recUk = _recognizerUk;
            _waveIn = null;
            finalsRu = _finalsRu.ToString();
            finalsUk = _finalsUk.ToString();
            _finalsRu.Clear();
            _finalsUk.Clear();
            _partialRu = "";
            _partialUk = "";
            recStarted = _recStarted;
            _recStarted = 0;
            _micGen++;
        }

        StopAndDisposeWave(wave);

        Heard heardRu = CompleteHypothesis(recRu, finalsRu);
        Heard heardUk = CompleteHypothesis(recUk, finalsUk);
        try { recRu?.Reset(); } catch { /* ignore */ }
        try { recUk?.Reset(); } catch { /* ignore */ }

        string text = PickBestHypothesis(heardRu, heardUk).Text.Trim();
        lock (_gate)
        {
            if (session != _session)
                return;
            _finishing = false;
        }

        var cfg = ScreenshotStore.Current;
        string paste = FormatOutput(text, cfg);
        int quietMs = Math.Max(280, Math.Clamp(cfg.DictationQuietMs, 0, 1500));
        long heardMs = recStarted > 0 ? Environment.TickCount64 - recStarted : 0;
        bool quiet = paste.Length == 0 && (recStarted == 0 || heardMs < quietMs);
        bool autoPaste = cfg.DictationAutoPaste;
        int pasteDelay = Math.Clamp(cfg.DictationPasteDelayMs, 0, 500);
        bool voiceCmds = cfg.DictationVoiceCommands;

        if (!quiet)
        {
            DictationCue.PlayStop();
            ResetTaps();
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    if (quiet)
                    {
                        DictationHud.Hide();
                        return;
                    }

                    DictationHud.Hide();
                    if (paste.Length == 0)
                    {
                        DictationHud.ShowError("Ничего не расслышала");
                        return;
                    }

                    // Голосовая команда — пробуем RU, UK и «лучшую» (Vosk на суржике врёт по-разному).
                    if (voiceCmds
                        && VoiceCommandService.TryExecuteAny(out string cmdTitle, heardRu.Text, heardUk.Text, text))
                    {
                        DictationHud.ShowCommand(cmdTitle);
                        return;
                    }

                    SnippetStore.PutClipboard(paste, paste: autoPaste, pasteDelayMs: pasteDelay);
                }
                catch { /* UI */ }
            }));
        }
        catch { /* dispatcher */ }
    }

    private void CleanupMicUnlocked(int session)
    {
        _ = session;
        WaveInEvent? wave;
        VoskRecognizer? ru;
        VoskRecognizer? uk;
        lock (_gate)
        {
            wave = _waveIn;
            ru = _recognizerRu;
            uk = _recognizerUk;
            _waveIn = null;
            _finalsRu.Clear();
            _finalsUk.Clear();
            _partialRu = "";
            _partialUk = "";
            _starting = false;
            _finishing = false;
            _recStarted = 0;
        }

        StopAndDisposeWave(wave);
        try { ru?.Reset(); } catch { /* держим прогретыми */ }
        try { uk?.Reset(); } catch { /* ignore */ }
    }

    private void EnsureLiveRecognizers(int lang, Model? modelRu, Model? modelUk)
    {
        bool wantRu = lang is 0 or 1 && modelRu != null;
        bool wantUk = lang is 0 or 2 && modelUk != null;
        if (!wantRu && !wantUk && modelRu != null)
            wantRu = true;

        lock (_gate)
        {
            _recognizerRu = FitRecognizer(_recognizerRu, wantRu ? modelRu : null);
            _recognizerUk = FitRecognizer(_recognizerUk, wantUk ? modelUk : null);
        }
    }

    private static VoskRecognizer? FitRecognizer(VoskRecognizer? current, Model? model)
    {
        if (model == null)
        {
            DisposeRecognizer(current);
            return null;
        }

        if (current != null)
        {
            try { current.Reset(); } catch { /* ignore */ }
            return current;
        }

        var rec = new VoskRecognizer(model, SampleRate);
        rec.SetWords(true);
        rec.SetMaxAlternatives(0);
        return rec;
    }

    private readonly record struct Heard(string Text, float Conf);

    private static Heard CompleteHypothesis(VoskRecognizer? recognizer, string finalsSoFar)
    {
        finalsSoFar = finalsSoFar.Trim();
        if (recognizer == null)
            return new Heard(finalsSoFar, finalsSoFar.Length == 0 ? 0 : 0.45f);

        try
        {
            Heard last = ReadHeard(recognizer.FinalResult());
            if (last.Text.Length == 0)
                return new Heard(finalsSoFar, finalsSoFar.Length == 0 ? 0 : 0.45f);
            if (finalsSoFar.Length == 0)
                return last;
            return new Heard((finalsSoFar + " " + last.Text).Trim(), last.Conf);
        }
        catch
        {
            return new Heard(finalsSoFar, finalsSoFar.Length == 0 ? 0 : 0.4f);
        }
    }

    /// <summary>
    /// Русская гипотеза по умолчанию. Украинская побеждает только если Vosk в ней увереннее:
    /// раньше выигрывал более длинный мусор, и русская речь превращалась в кашу.
    /// </summary>
    private static Heard PickBestHypothesis(Heard ru, Heard uk)
    {
        if (ru.Text.Length == 0)
            return uk;
        if (uk.Text.Length == 0)
            return ru;
        if (uk.Conf >= ru.Conf + 0.1f && uk.Conf >= 0.4f)
            return uk;
        return ru;
    }

    private static string PickBestHypothesis(string ru, string uk) =>
        PickBestHypothesis(new Heard(ru, (float)ScoreText(ru)), new Heard(uk, (float)ScoreText(uk))).Text;

    private static double ScoreText(string text)
    {
        if (text.Length == 0)
            return 0;
        // Больше слов и букв = обычно лучше распознало (пустой мусор короткий).
        int letters = 0;
        int words = 1;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
                letters++;
            else if (char.IsWhiteSpace(c))
                words++;
        }

        return letters + words * 0.35;
    }

    private static string FormatOutput(string text, ScreenshotSettings cfg)
    {
        if (text.Length == 0)
            return text;
        if (cfg.DictationCapitalize)
        {
            var ti = CultureInfo.GetCultureInfo("ru-RU").TextInfo;
            text = ti.ToUpper(text[0]) + text[1..];
        }

        if (cfg.DictationTrailingSpace && !char.IsWhiteSpace(text[^1]))
            text += " ";
        return text;
    }

    private void PostHudListening()
    {
        if (!ScreenshotStore.Current.DictationShowHud)
            return;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(DictationHud.ShowListening));
        }
        catch { /* ignore */ }
    }

    private string BuildText_NoLock()
    {
        string ru = JoinParts(_finalsRu, _partialRu);
        string uk = JoinParts(_finalsUk, _partialUk);
        return PickBestHypothesis(ru, uk);
    }

    private static string JoinParts(StringBuilder finals, string partial)
    {
        if (finals.Length == 0)
            return partial;
        if (partial.Length == 0)
            return finals.ToString();
        return string.Concat(finals.ToString(), " ", partial);
    }

    private void StopAndDisposeWave(WaveInEvent? wave)
    {
        if (wave == null)
            return;
        try { wave.DataAvailable -= OnData; } catch { /* ignore */ }
        try { wave.StopRecording(); } catch { /* ignore */ }
        try { Thread.Sleep(WaveDrainMs); } catch { /* ignore */ }
        try { wave.Dispose(); } catch { /* ignore */ }
    }

    private static void DisposeRecognizer(VoskRecognizer? recognizer)
    {
        if (recognizer == null)
            return;
        try { recognizer.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>Тихий микрофон чуть поднимаем, иначе small-модель Vosk сыплет мусор.</summary>
    private static void BoostQuietPcm(byte[] buffer, int bytes)
    {
        int samples = bytes / 2;
        if (samples < 8)
            return;

        int peak = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
            int a = s == short.MinValue ? 32768 : Math.Abs(s);
            if (a > peak)
                peak = a;
        }

        if (peak < 350 || peak > 14000)
            return;

        float gain = Math.Min(3.2f, 10000f / peak);
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8));
            int v = (int)Math.Round(s * gain);
            if (v > short.MaxValue) v = short.MaxValue;
            if (v < short.MinValue) v = short.MinValue;
            buffer[i * 2] = (byte)(v & 0xff);
            buffer[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
    }

    /// <summary>Слова с низкой уверенностью выкидываем, остальное склеиваем.</summary>
    private static Heard ReadHeard(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string raw = root.TryGetProperty("text", out var textEl) ? (textEl.GetString() ?? "").Trim() : "";
            if (!root.TryGetProperty("result", out var words) || words.ValueKind != JsonValueKind.Array)
                return new Heard(raw, raw.Length == 0 ? 0 : 0.45f);

            var keep = new StringBuilder();
            float sum = 0;
            int n = 0;
            int kept = 0;
            foreach (var w in words.EnumerateArray())
            {
                string word = w.TryGetProperty("word", out var wordEl) ? (wordEl.GetString() ?? "") : "";
                float conf = 0.5f;
                if (w.TryGetProperty("conf", out var confEl) && confEl.TryGetSingle(out float c))
                    conf = c;
                if (word.Length == 0)
                    continue;
                n++;
                sum += conf;
                if (conf < 0.22f)
                    continue;
                if (keep.Length > 0)
                    keep.Append(' ');
                keep.Append(word);
                kept++;
            }

            float avg = n == 0 ? (raw.Length == 0 ? 0 : 0.4f) : sum / n;
            string joined = keep.ToString().Trim();
            if (joined.Length == 0)
                return new Heard(avg >= 0.2f ? raw : "", avg);
            return new Heard(joined, avg);
        }
        catch
        {
            return new Heard(ReadText(json), 0.4f);
        }
    }

    private static string ReadText(string json, string prop = "text")
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(prop, out var t))
                return (t.GetString() ?? "").Trim();
        }
        catch
        {
            // ignore
        }

        return "";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        int session;
        lock (_gate)
        {
            _holding = false;
            session = _session;
            _session++;
            _micGen++;
        }

        try
        {
            if (_micMutex.Wait(3000))
            {
                try
                {
                    CleanupMicUnlocked(session);
                    VoskRecognizer? ru;
                    VoskRecognizer? uk;
                    lock (_gate)
                    {
                        ru = _recognizerRu;
                        uk = _recognizerUk;
                        _recognizerRu = null;
                        _recognizerUk = null;
                    }

                    DisposeRecognizer(ru);
                    DisposeRecognizer(uk);
                }
                finally { _micMutex.Release(); }
            }
        }
        catch { /* ignore */ }

        try { _micMutex.Dispose(); } catch { /* ignore */ }

        try
        {
            if (_dispatcher.CheckAccess())
                DictationHud.Hide();
            else
                _dispatcher.BeginInvoke(new Action(DictationHud.Hide));
        }
        catch { /* shutdown */ }
    }
}
