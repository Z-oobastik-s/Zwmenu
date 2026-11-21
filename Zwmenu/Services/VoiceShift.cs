using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using Zwmenu.Overlay;

namespace Zwmenu.Services;

/// <summary>Голос как у Clownfish: тон SoundTouch и окраска поверх.</summary>
public enum VoiceFx
{
    None,
    Male,
    Female,
    Baby,
    Helium,
    Robot,
    Cave,
    Radio,
    Alien,
    Clone,
    Mutate,
    MutateFast,
    MutateSlow,
    Atari
}

/// <summary>Горло, дыхание и рык. Тон задаётся отдельно, поэтому это не ползунок.</summary>
public readonly struct FaceTone
{
    public FaceTone(float formant, float breath, float sub, float growl, float shine = 0f)
    {
        Formant = formant;
        Breath = breath;
        Sub = sub;
        Growl = growl;
        Shine = shine;
    }

    public float Formant { get; }
    public float Breath { get; }
    public float Sub { get; }
    public float Growl { get; }
    public float Shine { get; }

    public bool On => Math.Abs(Formant) > 0.2f || Breath > 0.02f || Sub > 0.02f || Growl > 0.02f || Shine > 0.05f;
}

public sealed class VoiceColor
{
    public VoiceColor(string title, float semi, VoiceFx fx, FaceTone face = default, string model = "")
    {
        Title = title;
        Semi = semi;
        Fx = fx;
        Face = face;
        Model = model;
    }

    public string Title { get; }
    public float Semi { get; }
    public VoiceFx Fx { get; }
    public FaceTone Face { get; }
    public string Model { get; }

    static readonly VoiceColor[] Built =
    {
        new("Свой", 0f, VoiceFx.None),
        new("Мужской", -4f, VoiceFx.Male),
        new("Женский", 4.5f, VoiceFx.Female),
        new("Малыш", 8f, VoiceFx.Baby),
        new("Гелий", 12f, VoiceFx.Helium),
        new("Робот", 0f, VoiceFx.Robot),
        new("Пещера", -1.5f, VoiceFx.Cave),
        new("Рация", 1f, VoiceFx.Radio),
        new("Пришелец", 6f, VoiceFx.Alien),
        new("Двойник", 0f, VoiceFx.Clone),
        new("Мутация", 3f, VoiceFx.Mutate),
        new("Быстрая мутация", 5f, VoiceFx.MutateFast),
        new("Медленная мутация", -3f, VoiceFx.MutateSlow),
        new("Атари", 3f, VoiceFx.Atari),
        new("Баритон", -6f, VoiceFx.None, new FaceTone(-5f, 0f, 0.18f, 0.06f)),
        new("Сопрано", 5f, VoiceFx.None, new FaceTone(3.2f, 0.15f, 0f, 0f)),
        new("Диктор", -2f, VoiceFx.None, new FaceTone(-1.6f, 0f, 0.05f, 0.08f)),
        new("Старик", -3f, VoiceFx.None, new FaceTone(-2.2f, 0.22f, 0.08f, 0.04f)),
        new("Демон", -7f, VoiceFx.None, new FaceTone(-4.5f, 0f, 0.32f, 0.16f)),
        new("Шёпот", 1.5f, VoiceFx.None, new FaceTone(1.4f, 0.55f, 0f, 0f)),
        new("Злодей", -4f, VoiceFx.None, new FaceTone(2.4f, 0f, 0.12f, 0.1f)),
        new("Аниме", 6f, VoiceFx.None, new FaceTone(1.6f, 0.12f, 0f, 0f)),
        new("Орк", -8f, VoiceFx.None, new FaceTone(-5.5f, 0f, 0.34f, 0.18f)),
        new("Призрак", 2f, VoiceFx.None, new FaceTone(-2.8f, 0.35f, 0.1f, 0f)),
        new("Нежная", 3.2f, VoiceFx.None, new FaceTone(2.4f, 0.2f, 0f, 0f, 0.35f)),
        new("Ласка", 3.8f, VoiceFx.None, new FaceTone(2.8f, 0.26f, 0f, 0f, 0.25f)),
        new("Звонкая", 4.4f, VoiceFx.None, new FaceTone(3.1f, 0.08f, 0f, 0f, 0.8f)),
        new("Серебро", 4.8f, VoiceFx.None, new FaceTone(3.3f, 0.1f, 0f, 0f, 1f)),
        new("Бархат", 1.6f, VoiceFx.None, new FaceTone(1.7f, 0.12f, 0f, 0f, 0.2f)),
        new("Принцесса", 5.2f, VoiceFx.None, new FaceTone(3f, 0.16f, 0f, 0f, 0.7f)),
        new("Колокольчик", 5.8f, VoiceFx.None, new FaceTone(2.6f, 0.06f, 0f, 0f, 0.95f)),
        new("Мила", 0f, VoiceFx.None, default, "female_voice_hfg_new.onnx"),
        new("Светлая", 0f, VoiceFx.None, default, "female_voice_old_hq.onnx"),
        new("Мягкая", 0f, VoiceFx.None, default, "female_voice_old.onnx")
    };

    static VoiceColor[] _all = Built;

    public static IReadOnlyList<VoiceColor> All => _all;

    public static void Refresh()
    {
        var list = new List<VoiceColor>(Built);
        foreach (string name in VoiceRvc.Names())
            list.Add(new VoiceColor(VoiceRvc.TitleOf(name), 0f, VoiceFx.None, default, VoiceRvc.Mark + name));
        _all = list.ToArray();
    }
}

public sealed class VoiceShift
{
    public static VoiceShift Shared { get; } = new();

    private const int Rate = VoiceRecService.SampleRate;

    private readonly object _gate = new();
    private readonly SoundTouchProcessor _touch = new();
    private readonly VoiceFace _facePipe = new();
    private readonly float[] _mod = new float[8192];
    private readonly float[][] _cave =
    {
        new float[4096], new float[4096], new float[4096], new float[4096]
    };
    private readonly int[] _cavePos = new int[4];
    private readonly float[] _caveDamp = new float[4];
    private float[] _ready = new float[8192];
    private float[] _pull = new float[4096];
    private Biquad _hp;
    private Biquad _eq;
    private Biquad _bp;
    private int _preset;
    private float _semi;
    private float _gain = 1f;
    private float _boost = 1f;
    private float _gateLevel = 0.007f;
    private float _lowCut = 90f;
    private float _lowCutLive;
    private float _comp = 0.62f;
    private float _hear = 0.72f;
    private float _tilt;
    private float _tiltPrev;
    private float _env;
    private float _lfo;
    private float _ring;
    private int _modPos;
    private int _holdN;
    private float _held;
    private bool _useEq;
    private bool _open;
    private VoiceFx _fx;
    private bool _face;
    private float _formantSemi;
    private float _breath;
    private float _sub;
    private float _growl;
    private float _shine;
    private string _model = "";
    private float _breathLp;
    private int _epoch;
    private int _seenEpoch;

    public float[] Ready => _ready;

    private VoiceShift()
    {
        var s = ScreenshotStore.Current;
        _semi = Math.Clamp(s.VoiceShiftSemi, -15f, 15f);
        _gain = Math.Clamp(s.VoiceShiftGain, 0.4f, 2.2f);
        _boost = Math.Clamp(s.VoiceShiftBoost, 0.4f, 4f);
        _gateLevel = Math.Clamp(s.VoiceShiftGate, 0f, 0.04f);
        _lowCut = Math.Clamp(s.VoiceShiftLowCut, 40f, 220f);
        _comp = Math.Clamp(s.VoiceShiftComp, 0f, 1.2f);
        _hear = Math.Clamp(s.VoiceShiftHearGain, 0f, 1f);
        VoiceColor.Refresh();
        int id = s.VoiceShiftPreset;
        string saved = s.VoiceShiftRvc ?? "";
        if (saved.Length > 0)
        {
            for (int i = 0; i < VoiceColor.All.Count; i++)
            {
                if (VoiceColor.All[i].Model == VoiceRvc.Mark + saved)
                {
                    id = i;
                    break;
                }
            }
        }

        if (id < 0 || id >= VoiceColor.All.Count)
            id = 0;
        _preset = id;
        _fx = VoiceColor.All[id].Fx;
        LoadFace(id);
        ArmTone();
    }

    public int Preset => _preset;

    public bool Library => _model.Length > 0;

    public bool Active =>
        _model.Length > 0 || Math.Abs(_semi) > 0.08f || _fx != VoiceFx.None || _face || Math.Abs(_gain - 1f) > 0.03f;

    public float Semi => _semi;

    public float Gain => _gain;

    public float Boost => _boost;

    public float GateLevel => _gateLevel;

    public float LowCut => _lowCut;

    public float Comp => _comp;

    public float HearGain => _hear;

    public string Title => VoiceColor.All[Math.Clamp(_preset, 0, VoiceColor.All.Count - 1)].Title;

    public event Action? Changed;

    public void ApplyPreset(int index)
    {
        index = Math.Clamp(index, 0, VoiceColor.All.Count - 1);
        Apply(index, VoiceColor.All[index].Semi, save: true, keepSemi: false);
    }

    public void SetSemi(float semi) => _semi = Math.Clamp(semi, -15f, 15f);

    public void SetGain(float gain) => _gain = Math.Clamp(gain, 0.4f, 2.2f);

    public void SetBoost(float boost) => _boost = Math.Clamp(boost, 0.4f, 4f);

    public void SetGateLevel(float gate) => _gateLevel = Math.Clamp(gate, 0f, 0.04f);

    public void SetLowCut(float hz) => _lowCut = Math.Clamp(hz, 40f, 220f);

    public void SetComp(float comp) => _comp = Math.Clamp(comp, 0f, 1.2f);

    public void SetHearGain(float hear) => _hear = Math.Clamp(hear, 0f, 1f);

    public void SaveTone()
    {
        var s = ScreenshotStore.Current;
        s.VoiceShiftSemi = Semi;
        s.VoiceShiftGain = Gain;
        ScreenshotStore.Save(s);
    }

    public void SaveMic()
    {
        var s = ScreenshotStore.Current;
        s.VoiceShiftBoost = _boost;
        s.VoiceShiftGate = _gateLevel;
        s.VoiceShiftLowCut = _lowCut;
        s.VoiceShiftComp = _comp;
        s.VoiceShiftHearGain = _hear;
        ScreenshotStore.Save(s);
    }

    public int Process(float[] input, int count) => Process(input, count, polish: false);

    /// <summary>polish: лёгкий гейт и компрессор даже на своём голосе. Для живого микрофона.</summary>
    public int Process(float[] input, int count, bool polish)
    {
        if (input == null || count <= 0)
            return 0;

        lock (_gate)
        {
            CatchUp();
            if (_model.Length > 0)
            {
                Ensure(count);
                if (Math.Abs(_boost - 1f) > 0.02f)
                {
                    for (int i = 0; i < count; i++)
                        input[i] *= _boost;
                }

                int gotGirl = VoiceRvc.IsMark(_model)
                    ? VoiceRvc.Shared.Take(_model, _semi, input, count, _ready)
                    : VoiceGirl.Shared.Take(_model, input, count, _ready);
                float girlGain = _gain;
                for (int i = 0; i < gotGirl; i++)
                {
                    float x = _ready[i] * girlGain;
                    if (x > 0.97f)
                        x = 0.97f;
                    else if (x < -0.97f)
                        x = -0.97f;
                    _ready[i] = x;
                }

                return gotGirl;
            }

            bool pitch = Math.Abs(_semi) > 0.08f;
            bool color = pitch || _fx != VoiceFx.None || _face;
            bool shaped = Math.Abs(_boost - 1f) > 0.02f
                || Math.Abs(_gateLevel - 0.007f) > 0.0004f
                || Math.Abs(_lowCut - 90f) > 1f
                || Math.Abs(_comp - 0.62f) > 0.02f;
            bool tone = color || polish || shaped || Math.Abs(_gain - 1f) > 0.03f;
            if (!tone)
            {
                Ensure(count);
                Array.Copy(input, _ready, count);
                return count;
            }

            for (int i = 0; i < count; i++)
                input[i] = Pre(input[i]);

            int got;
            if (!pitch)
            {
                Ensure(count);
                Array.Copy(input, _ready, count);
                got = count;
            }
            else
            {
                EnsureOpen();
                var span = new ReadOnlySpan<float>(input, 0, count);
                _touch.PutSamples(span, count);
                got = 0;
                while (true)
                {
                    var pull = new Span<float>(_pull);
                    int n = _touch.ReceiveSamples(pull, _pull.Length);
                    if (n <= 0)
                        break;
                    Ensure(got + n);
                    Array.Copy(_pull, 0, _ready, got, n);
                    got += n;
                    if (got > Rate)
                        break;
                }
            }

            if (_face && Math.Abs(_formantSemi) > 0.2f)
                got = _facePipe.Process(_ready, got, _formantSemi);
            for (int i = 0; i < got; i++)
                _ready[i] = Post(_ready[i]);
            return got;
        }
    }

    public static void SelfCheck()
    {
        var voice = new VoiceShift();
        var input = new float[12000];
        for (int i = 0; i < input.Length; i++)
            input[i] = 0.35f * MathF.Sin(2f * MathF.PI * 170f * i / Rate);

        foreach (int preset in new[] { 4, 5, 6, 7, 9, 13, 14, 19, 23, 24, 27 })
        {
            voice.Apply(preset, VoiceColor.All[preset].Semi, save: false, keepSemi: false);
            int got = 0;
            float peak = 0;
            var slice = new float[480];
            for (int off = 0; off + 480 <= input.Length; off += 480)
            {
                Array.Copy(input, off, slice, 0, 480);
                int n = voice.Process(slice, 480, polish: true);
                for (int i = 0; i < n; i++)
                {
                    float s = voice.Ready[i];
                    if (float.IsNaN(s) || float.IsInfinity(s))
                        throw new InvalidOperationException("Голос дал пустой отсчёт");
                    float a = Math.Abs(s);
                    if (a > peak)
                        peak = a;
                }

                got += n;
            }

            if (got < 4000 || peak < 0.02f)
                throw new InvalidOperationException("Голос «" + VoiceColor.All[preset].Title + "» пустой");
        }

        VoiceGirl.SelfCheck();
        VoiceRvc.SelfCheck();
    }

    private void Apply(int index, float semi, bool save, bool keepSemi)
    {
        _preset = index;
        _fx = VoiceColor.All[index].Fx;
        LoadFace(index);
        if (!keepSemi)
            _semi = semi;
        Interlocked.Increment(ref _epoch);
        Changed?.Invoke();
        if (!save)
            return;
        var s = ScreenshotStore.Current;
        s.VoiceShiftPreset = index;
        s.VoiceShiftSemi = _semi;
        string model = VoiceColor.All[index].Model;
        s.VoiceShiftRvc = VoiceRvc.IsMark(model) ? model[VoiceRvc.Mark.Length..] : "";
        ScreenshotStore.Save(s);
    }

    /// <summary>Только поток звука. Окно сюда не заходит, хук мыши не ждёт.</summary>
    private void CatchUp()
    {
        int epoch = Volatile.Read(ref _epoch);
        float semi = _semi;
        if (epoch != _seenEpoch)
        {
            _seenEpoch = epoch;
            _env = 0;
            _lfo = 0;
            _ring = 0;
            _holdN = 0;
            _held = 0;
            _tiltPrev = 0;
            _modPos = 0;
            Array.Clear(_mod);
            for (int i = 0; i < _cave.Length; i++)
            {
                Array.Clear(_cave[i]);
                _cavePos[i] = 0;
                _caveDamp[i] = 0;
            }

            _hp.Clear();
            _eq.Clear();
            _bp.Clear();
            _facePipe.Clear();
            _breathLp = 0;
            ArmTone();
            if (_open)
                TuneTouch();
            if (_open)
            {
                _touch.Clear();
                _touch.PitchSemiTones = semi;
            }
        }
        else if (_open && Math.Abs(_touch.PitchSemiTones - semi) > 0.02f)
            _touch.PitchSemiTones = semi;

        _tilt = _face
            ? Math.Clamp(-_formantSemi * 0.02f + _shine * 0.1f, -0.22f, 0.2f)
            : _fx switch
            {
                VoiceFx.Female or VoiceFx.Baby => -0.2f,
                VoiceFx.Male => 0.1f,
                VoiceFx.Helium => 0.16f,
                VoiceFx.Radio or VoiceFx.Robot or VoiceFx.Atari => 0f,
                _ => Math.Clamp(-semi * 0.028f, -0.38f, 0.38f)
            };
    }

    private void EnsureOpen()
    {
        if (_open)
            return;
        _touch.SampleRate = Rate;
        _touch.Channels = 1;
        _touch.PitchSemiTones = _semi;
        _touch.Tempo = 1f;
        _touch.Rate = 1f;
        _touch.SetSetting(SettingId.UseQuickSeek, 1);
        _touch.SetSetting(SettingId.UseAntiAliasFilter, 1);
        _touch.SetSetting(SettingId.AntiAliasFilterLength, 16);
        _touch.SetSetting(SettingId.SequenceDurationMs, 16);
        _touch.SetSetting(SettingId.SeekWindowDurationMs, 10);
        _touch.SetSetting(SettingId.OverlapDurationMs, 6);
        _open = true;
        TuneTouch();
    }

    private void TuneTouch()
    {
        int quick = _face ? 0 : 1;
        int aa = _face ? 32 : 16;
        int seq = _face ? 28 : 16;
        int seek = _face ? 14 : 10;
        int overlap = _face ? 8 : 6;
        _touch.SetSetting(SettingId.UseQuickSeek, quick);
        _touch.SetSetting(SettingId.UseAntiAliasFilter, 1);
        _touch.SetSetting(SettingId.AntiAliasFilterLength, aa);
        _touch.SetSetting(SettingId.SequenceDurationMs, seq);
        _touch.SetSetting(SettingId.SeekWindowDurationMs, seek);
        _touch.SetSetting(SettingId.OverlapDurationMs, overlap);
    }

    private void LoadFace(int index)
    {
        FaceTone face = VoiceColor.All[index].Face;
        _formantSemi = face.Formant;
        _breath = face.Breath;
        _sub = face.Sub;
        _growl = face.Growl;
        _shine = face.Shine;
        _face = face.On;
        _model = VoiceColor.All[index].Model ?? "";
        if (VoiceRvc.IsMark(_model))
        {
            VoiceGirl.Shared.Idle();
            VoiceRvc.Shared.Select(_model);
        }
        else if (_model.Length > 0)
        {
            VoiceRvc.Shared.Idle();
            VoiceGirl.Shared.Select(_model);
        }
        else
        {
            VoiceGirl.Shared.Idle();
            VoiceRvc.Shared.Idle();
        }
    }

    private void Ensure(int need)
    {
        if (_ready.Length >= need)
            return;
        int size = _ready.Length;
        while (size < need)
            size *= 2;
        _ready = new float[size];
    }

    private void ArmTone()
    {
        _tilt = _fx switch
        {
            VoiceFx.Female or VoiceFx.Baby => -0.2f,
            VoiceFx.Male => 0.1f,
            VoiceFx.Helium => 0.16f,
            VoiceFx.Radio or VoiceFx.Robot or VoiceFx.Atari => 0f,
            _ => Math.Clamp(-_semi * 0.028f, -0.38f, 0.38f)
        };
        _useEq = _fx is VoiceFx.Male or VoiceFx.Female;
        if (_fx == VoiceFx.Male)
            Peak(ref _eq, 2500f, 0.9f, 4.5f);
        else if (_fx == VoiceFx.Female)
            Peak(ref _eq, 220f, 0.75f, 3.2f);
        if (_shine > 0.05f)
        {
            _useEq = true;
            Peak(ref _eq, 2700f, 0.9f, 1.6f + _shine * 1.5f);
        }
        if (_fx == VoiceFx.Radio)
            Band(ref _bp, 1750f, 0.65f);
        if (Math.Abs(_lowCut - _lowCutLive) > 0.5f)
        {
            HighPass(ref _hp, _lowCut, 0.707f);
            _lowCutLive = _lowCut;
        }
    }

    private float Pre(float x)
    {
        if (Math.Abs(_lowCut - _lowCutLive) > 0.5f)
        {
            HighPass(ref _hp, _lowCut, 0.707f);
            _lowCutLive = _lowCut;
        }

        x = _hp.Run(x);
        if (_boost != 1f)
            x *= _boost;
        float a = Math.Abs(x);
        _env += (a - _env) * (a > _env ? 0.22f : 0.004f);
        float gate = _gateLevel;
        if (gate > 0.0001f && _env < gate)
            x *= _env / gate;
        return x;
    }

    private float Post(float x)
    {
        float dry = x;
        x = _fx switch
        {
            VoiceFx.Robot => Robot(dry),
            VoiceFx.Cave => Cave(dry),
            VoiceFx.Radio => Radio(dry),
            VoiceFx.Alien => Alien(dry),
            VoiceFx.Clone => Clone(dry),
            VoiceFx.Mutate => Wobble(dry, 3.2f, 5.5f),
            VoiceFx.MutateFast => Wobble(dry, 8.5f, 3.2f),
            VoiceFx.MutateSlow => Wobble(dry, 0.7f, 8f),
            VoiceFx.Atari => Atari(dry),
            _ => dry
        };
        if (_face)
            x = Shade(x);

        x += _tilt * (dry - _tiltPrev);
        _tiltPrev = dry;
        if (_useEq)
            x = _eq.Run(x);

        float over = _env / 0.2f;
        float comp = over > 1f && _comp > 0.01f ? MathF.Pow(over, -_comp) : 1f;
        x *= comp * 1.35f * _gain;
        if (x > 1f)
            x = MathF.Tanh(x);
        else if (x < -1f)
            x = -MathF.Tanh(-x);
        return x;
    }

    private float Shade(float x)
    {
        if (_sub > 0.02f)
        {
            float mix = Math.Min(_sub, 0.34f);
            float body = x >= 0f ? x : x * (1f - mix);
            x = x * (1f - mix * 0.4f) + body * mix;
        }

        if (_breath > 0.02f)
        {
            _breathLp += 0.07f * (x - _breathLp);
            x -= _breathLp * Math.Min(_breath, 0.6f) * 0.7f;
        }

        if (_growl > 0.02f)
            x = MathF.Tanh(x * (1f + Math.Min(_growl, 0.2f) * 1.2f));

        return x;
    }

    private float Robot(float x)
    {
        _ring += 2f * MathF.PI * 48f / Rate;
        if (_ring > 2f * MathF.PI)
            _ring -= 2f * MathF.PI;
        float metal = Tap(x, 4.2f, 0f, 0f, write: true);
        return x * (0.22f + 0.78f * MathF.Sin(_ring)) * 0.72f + metal * 0.28f;
    }

    private float Radio(float x)
    {
        float b = _bp.Run(x);
        if (++_holdN >= 4)
        {
            _holdN = 0;
            _held = b;
        }

        return MathF.Tanh(_held * 2.5f);
    }

    private float Alien(float x)
    {
        _ring += 2f * MathF.PI * 7.5f / Rate;
        if (_ring > 2f * MathF.PI)
            _ring -= 2f * MathF.PI;
        float wet = Tap(x, 8f, 3.6f, 0f, write: true, rate: 5.5f);
        return wet * (0.78f + 0.22f * MathF.Sin(_ring));
    }

    private float Clone(float x)
    {
        float a = Tap(x, 19f, 2.4f, 0f, write: true, rate: 0.45f);
        float b = Tap(x, 36f, 1.7f, 2.1f, write: false);
        return x * 0.52f + a * 0.3f + b * 0.22f;
    }

    private float Wobble(float x, float hz, float depthMs)
    {
        float wet = Tap(x, 11f, depthMs, 0f, write: true, rate: hz);
        return wet * 0.78f + x * 0.22f;
    }

    private float Atari(float x)
    {
        if (++_holdN >= 4)
        {
            _holdN = 0;
            _held = MathF.Round(x * 11f) / 11f;
        }

        return _held;
    }

    private float Cave(float x)
    {
        float wet = 0;
        int[] delays = { 1488, 1776, 2112, 2544 };
        for (int i = 0; i < 4; i++)
            wet += Comb(x, _cave[i], ref _cavePos[i], ref _caveDamp[i], delays[i]);
        return x * 0.58f + wet * 0.16f;
    }

    private static float Comb(float x, float[] line, ref int pos, ref float damp, int delay)
    {
        int read = pos - delay;
        if (read < 0)
            read += line.Length;
        float d = line[read];
        damp = damp * 0.32f + d * 0.68f;
        line[pos] = x + damp * 0.44f;
        pos++;
        if (pos >= line.Length)
            pos = 0;
        return d;
    }

    private float Tap(float x, float baseMs, float depthMs, float phase, bool write, float rate = 0.35f)
    {
        if (write)
        {
            _mod[_modPos] = x;
            _lfo += 2f * MathF.PI * rate / Rate;
            if (_lfo > 2f * MathF.PI)
                _lfo -= 2f * MathF.PI;
        }

        float delay = (baseMs + depthMs * MathF.Sin(_lfo + phase)) * (Rate / 1000f);
        if (delay < 1f)
            delay = 1f;
        float read = _modPos - delay;
        while (read < 0)
            read += _mod.Length;
        int i0 = (int)read % _mod.Length;
        int i1 = i0 + 1;
        if (i1 >= _mod.Length)
            i1 = 0;
        float frac = read - (int)read;
        float y = _mod[i0] * (1f - frac) + _mod[i1] * frac;
        if (write)
        {
            _modPos++;
            if (_modPos >= _mod.Length)
                _modPos = 0;
        }

        return y;
    }

    private static void HighPass(ref Biquad f, float freq, float q) =>
        Arm(ref f, freq, q, kind: 0, gainDb: 0);

    private static void Band(ref Biquad f, float freq, float q) =>
        Arm(ref f, freq, q, kind: 1, gainDb: 0);

    private static void Peak(ref Biquad f, float freq, float q, float gainDb) =>
        Arm(ref f, freq, q, kind: 2, gainDb: gainDb);

    private static void Arm(ref Biquad f, float freq, float q, int kind, float gainDb)
    {
        float w0 = 2f * MathF.PI * freq / Rate;
        float sin = MathF.Sin(w0);
        float cos = MathF.Cos(w0);
        float alpha = sin / (2f * q);
        float b0, b1, b2, a0, a1, a2;
        if (kind == 0)
        {
            b0 = (1f + cos) / 2f;
            b1 = -(1f + cos);
            b2 = (1f + cos) / 2f;
            a0 = 1f + alpha;
            a1 = -2f * cos;
            a2 = 1f - alpha;
        }
        else if (kind == 1)
        {
            b0 = alpha;
            b1 = 0f;
            b2 = -alpha;
            a0 = 1f + alpha;
            a1 = -2f * cos;
            a2 = 1f - alpha;
        }
        else
        {
            float A = MathF.Pow(10f, gainDb / 40f);
            b0 = 1f + alpha * A;
            b1 = -2f * cos;
            b2 = 1f - alpha * A;
            a0 = 1f + alpha / A;
            a1 = -2f * cos;
            a2 = 1f - alpha / A;
        }

        f.B0 = b0 / a0;
        f.B1 = b1 / a0;
        f.B2 = b2 / a0;
        f.A1 = a1 / a0;
        f.A2 = a2 / a0;
        f.Clear();
    }

    private struct Biquad
    {
        public float B0, B1, B2, A1, A2, X1, X2, Y1, Y2;

        public float Run(float x)
        {
            float y = B0 * x + B1 * X1 + B2 * X2 - A1 * Y1 - A2 * Y2;
            X2 = X1;
            X1 = x;
            Y2 = Y1;
            Y1 = y;
            if (y > 4f)
                y = 4f;
            else if (y < -4f)
                y = -4f;
            return y;
        }

        public void Clear() => X1 = X2 = Y1 = Y2 = 0;
    }
}

/// <summary>Микрофон → голос → кабель. Кабель становится микрофоном системы.</summary>
public sealed class VoiceShiftLive
{
    public static VoiceShiftLive Shared { get; } = new();

    public readonly record struct AudioEnd(string Id, string Name, bool Cable);

    private const int Rate = VoiceRecService.SampleRate;

    private MMDeviceEnumerator? _enum;
    private MMDevice? _micDev;
    private MMDevice? _outDev;
    private MMDevice? _monDev;
    private WasapiCapture? _cap;
    private WasapiOut? _out;
    private WasapiOut? _mon;
    private BufferedWaveProvider? _play;
    private BufferedWaveProvider? _monPlay;
    private WaveFormat? _capFormat;
    private float[] _frames = new float[4096];
    private float[] _mono = new float[4096];
    private float[] _mix = new float[4096];
    private byte[] _pcm = new byte[8192];
    private byte[] _monPcm = new byte[8192];
    private double _phase;
    private static int _held;
    private readonly BlockingCollection<Action> _jobs = new();
    private Thread? _worker;
    private int _busy;
    private int _prepared;
    private AudioEnd[] _renders = Array.Empty<AudioEnd>();
    private AudioEnd[] _captures = Array.Empty<AudioEnd>();

    public static bool Arming { get; set; }
    public bool Running { get; private set; }
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public bool Prepared => Volatile.Read(ref _prepared) != 0;
    public bool UsingCable { get; private set; }
    public bool ClaimsMic { get; private set; }
    public string? DeviceName { get; private set; }
    public string Note { get; private set; } = "Подбираю микрофон и кабель…";

    /// <summary>Громкость входа, 0…1. Пишет звуковой поток, окно только читает.</summary>
    public float Peak { get; private set; }
    public event Action? Changed;

    public IReadOnlyList<AudioEnd> RenderList => _renders;
    public IReadOnlyList<AudioEnd> CaptureList => _captures;

    public static bool IsCable(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return name.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Clownfish", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCablePlay(string? name)
    {
        if (!IsCable(name) || string.IsNullOrEmpty(name))
            return false;
        if (name.Contains("16", StringComparison.Ordinal))
            return false;
        if (name.Contains("Output", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Выход", StringComparison.OrdinalIgnoreCase))
            return false;
        return name.Contains("Input", StringComparison.OrdinalIgnoreCase)
            || name.Contains(" In", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Вход", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureWorker()
    {
        if (_worker != null)
            return;
        lock (_jobs)
        {
            if (_worker != null)
                return;
            _worker = new Thread(Pump)
            {
                IsBackground = true,
                Name = "Zwmenu.Voice",
                Priority = ThreadPriority.AboveNormal
            };
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }
    }

    private void Pump()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try { job(); }
            catch { /* ошибка уже в Note */ }
        }
    }

    private void Post(Action job)
    {
        EnsureWorker();
        if (ReferenceEquals(Thread.CurrentThread, _worker))
            job();
        else
            _jobs.Add(job);
    }

    public void RequestStart(bool hear) => Post(() =>
    {
        Interlocked.Exchange(ref _busy, 1);
        try
        {
            if (Volatile.Read(ref _prepared) == 0)
                PrepareCore();
            string? error = Start(hear);
            if (error != null)
            {
                Note = error;
                if (error == SetupOffer.CableNote)
                    SetupOffer.ShowNow();
                else
                    Notify(error);
                Changed?.Invoke();
            }
        }
        finally { FinishJob(); }
    });

    public void RequestStop() => Post(() =>
    {
        Interlocked.Exchange(ref _busy, 1);
        try { Halt(user: true); }
        finally { FinishJob(); }
    });

    public void RequestPrepare() => Post(() =>
    {
        try
        {
            PrepareCore();
            Changed?.Invoke();
        }
        catch { /* окно покажет пустой список */ }
    });

    /// <summary>Список микрофонов и обычных динамиков. Перечисление только на потоке голоса.</summary>
    public void RequestDevices(Action<AudioEnd[], AudioEnd[]> done)
    {
        Post(() =>
        {
            AudioEnd[] mics;
            AudioEnd[] ears;
            try
            {
                var caps = List(DataFlow.Capture, cablesFirst: false);
                var renders = List(DataFlow.Render, cablesFirst: false);
                int micCount = 0;
                foreach (var item in caps)
                {
                    if (!item.Cable)
                        micCount++;
                }

                int earCount = 0;
                foreach (var item in renders)
                {
                    if (!item.Cable && !IsCable(item.Name))
                        earCount++;
                }

                mics = new AudioEnd[micCount];
                ears = new AudioEnd[earCount];
                int mi = 0;
                int ei = 0;
                foreach (var item in caps)
                {
                    if (!item.Cable)
                        mics[mi++] = item;
                }

                foreach (var item in renders)
                {
                    if (!item.Cable && !IsCable(item.Name))
                        ears[ei++] = item;
                }
            }
            catch
            {
                mics = Array.Empty<AudioEnd>();
                ears = Array.Empty<AudioEnd>();
            }

            var app = System.Windows.Application.Current;
            if (app == null)
                return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { done(mics, ears); }
                catch { /* окно уже закрыто */ }
            }));
        });
    }

    /// <summary>Снова поднять отдачу, если сменили микрофон, наушники или запас. Кабель остаётся выходом в звонок.</summary>
    public void RequestApplyRoute() => Post(() =>
    {
        if (!Running)
            return;
        bool hear = ScreenshotStore.Current.VoiceShiftHear;
        Interlocked.Exchange(ref _busy, 1);
        try
        {
            Halt(user: false);
            string? error = Start(hear);
            if (error != null)
            {
                Note = error;
                if (error == SetupOffer.CableNote)
                    SetupOffer.ShowNow();
                else
                    Notify(error);
                Changed?.Invoke();
            }
        }
        finally { FinishJob(); }
    });

    public static bool TryBareKey(int vk)
    {
        if (Arming || vk == 0)
            return false;
        var s = ScreenshotStore.Current;
        if (vk == s.Hotkey || vk == s.ClipHotkey || vk == s.TranslateHotkey || vk == s.DictationHotkey)
            return false;
        if (_held == vk)
            return true;
        if (s.VoiceShiftToggleKey != 0 && vk == s.VoiceShiftToggleKey)
        {
            _held = vk;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(Shared.ToggleSaved));
            return true;
        }

        return VoiceBoard.Shared.TryKey(vk, id => _held = id);
    }

    public static void ReleaseKey(int vk)
    {
        if (_held == vk)
            _held = 0;
    }

    public void ToggleSaved()
    {
        if (Running || Busy)
            RequestStop();
        else
            RequestStart(ScreenshotStore.Current.VoiceShiftHear);
    }

    /// <summary>Слышать себя в наушниках. startIfOff включает голос сразу, без окна настроек.</summary>
    public void SetHear(bool on, bool startIfOff)
    {
        var s = ScreenshotStore.Current;
        s.VoiceShiftHear = on;
        ScreenshotStore.Save(s);
        if (!Busy && (Running || (on && startIfOff)))
        {
            RequestStart(on);
            return;
        }

        Changed?.Invoke();
    }

    public static void SelfCheckPolicy()
    {
        MMDeviceEnumerator? en = null;
        try
        {
            en = new MMDeviceEnumerator();
            string real = "";
            string cable = "";
            try
            {
                var cur = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                real = cur.ID;
                cur.Dispose();
            }
            catch { return; }

            foreach (var item in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    if (IsCable(item.FriendlyName)
                        && item.FriendlyName.Contains("Output", StringComparison.OrdinalIgnoreCase)
                        && !item.FriendlyName.Contains("16", StringComparison.Ordinal))
                        cable = item.ID;
                }
                finally { try { item.Dispose(); } catch { /* ignore */ } }
            }

            if (cable.Length == 0 || cable == real)
                return;
            bool switched = false;
            try
            {
                if (!TrySetDefault(cable, cable))
                    throw new InvalidOperationException("Микрофон системы не переключается");
                switched = true;
                var check = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                bool stuck = check.ID != cable;
                check.Dispose();
                if (stuck)
                    throw new InvalidOperationException("Микрофон системы не переключается");
            }
            finally
            {
                if (switched)
                    TrySetDefault(real, real);
            }
        }
        finally
        {
            try { en?.Dispose(); } catch { /* ignore */ }
        }
    }

    public void Restore() => Post(() =>
    {
        try
        {
            PrepareCore();
            RecoverMic();
            var s = ScreenshotStore.Current;
            if (!s.VoiceShiftLiveOn)
            {
                Changed?.Invoke();
                return;
            }

            string? error = Start(s.VoiceShiftHear);
            if (error != null)
            {
                Note = error;
                if (error == SetupOffer.CableNote)
                    SetupOffer.ShowNow();
                else
                    Notify(error);
                Changed?.Invoke();
            }
        }
        catch { /* включение остаётся в окне голоса */ }
    });

    public void StopForExit()
    {
        VoiceGirl.Shared.Stop();
        VoiceRvc.Shared.Stop();
        if (_worker == null)
        {
            if (Running)
                Halt(user: true);
            else
                RecoverMic();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Post(() =>
        {
            try { Halt(user: true); }
            finally { done.Set(); }
        });
        done.Wait(1200);
    }

    private void PrepareCore()
    {
        WakeCable();
        _renders = List(DataFlow.Render, cablesFirst: true).ToArray();
        _captures = List(DataFlow.Capture, cablesFirst: false).ToArray();
        string cable = BestCableRender(_renders);
        string mic = DefaultCaptureId();
        if (string.IsNullOrEmpty(mic))
            mic = BestRealMic(_captures);

        var s = ScreenshotStore.Current;
        bool dirty = false;
        bool savedCable = false;
        foreach (var item in _renders)
        {
            if (item.Id == s.VoiceShiftOutId && IsCablePlay(item.Name))
                savedCable = true;
        }

        if (!savedCable && cable.Length > 0)
        {
            s.VoiceShiftOutId = cable;
            dirty = true;
        }

        bool savedMic = false;
        foreach (var item in _captures)
        {
            if (item.Id == s.VoiceShiftMicId && !item.Cable)
                savedMic = true;
        }

        if (!savedMic && mic.Length > 0)
        {
            s.VoiceShiftMicId = mic;
            dirty = true;
        }

        if (dirty)
            ScreenshotStore.Save(s);

        if (!Running)
        {
            Note = cable.Length > 0
                ? "Выключено. Микрофон Windows твой. Нажми включить, и голос подменится сам."
                : SetupOffer.CableNote;
        }

        Interlocked.Exchange(ref _prepared, 1);
    }

    private void FinishJob()
    {
        Interlocked.Exchange(ref _busy, 0);
        Changed?.Invoke();
    }

    private static string BestCableRender(IReadOnlyList<AudioEnd> renders)
    {
        string? any = null;
        foreach (var item in renders)
        {
            if (!IsCable(item.Name))
                continue;
            any ??= item.Id;
            if (IsCablePlay(item.Name))
                return item.Id;
        }

        return any ?? "";
    }

    private static string FirstSpeaker(IReadOnlyList<AudioEnd> renders, string skipId)
    {
        foreach (var item in renders)
        {
            if (item.Id == skipId || item.Cable || IsCable(item.Name))
                continue;
            return item.Id;
        }

        return "";
    }

    private static string BestRealMic(IReadOnlyList<AudioEnd> caps)
    {
        foreach (var item in caps)
        {
            if (!item.Cable)
                return item.Id;
        }

        return caps.Count > 0 ? caps[0].Id : "";
    }

    private static string NameIn(IReadOnlyList<AudioEnd> list, string id)
    {
        foreach (var item in list)
        {
            if (item.Id == id)
                return item.Name;
        }

        return "";
    }

    private static string DefaultCaptureId()
    {
        MMDeviceEnumerator? en = null;
        try
        {
            en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            string id = dev.ID;
            string name = dev.FriendlyName;
            dev.Dispose();
            return IsCable(name) ? "" : id;
        }
        catch { return ""; }
        finally
        {
            try { en?.Dispose(); } catch { /* ignore */ }
        }
    }

    public string? Start(bool hear)
    {
        if (Running)
            Halt(user: false);

        try
        {
            WakeCable();
            var renders = List(DataFlow.Render, cablesFirst: true);
            var caps = List(DataFlow.Capture, cablesFirst: false);
            string playId = BestCableRender(renders);
            string micId = "";
            string savedMic = ScreenshotStore.Current.VoiceShiftMicId ?? "";
            if (savedMic.Length > 0)
            {
                foreach (var item in caps)
                {
                    if (item.Id == savedMic && !item.Cable)
                    {
                        micId = savedMic;
                        break;
                    }
                }
            }

            if (micId.Length == 0)
                micId = DefaultCaptureId();
            if (string.IsNullOrEmpty(micId))
                micId = BestRealMic(caps);
            string playName = NameIn(renders, playId);
            string? recId = playId.Length > 0 ? CableCaptureId(playName) : null;
            if (playId.Length == 0 || string.IsNullOrEmpty(recId))
                return SetupOffer.CableNote;
            if (micId.Length == 0)
                return "Не вижу твой микрофон.";

            _enum = new MMDeviceEnumerator();
            _outDev = _enum.GetDevice(playId);
            _micDev = _enum.GetDevice(micId);
            if (_outDev.State != DeviceState.Active || _micDev.State != DeviceState.Active)
            {
                Halt(user: false);
                return "Кабель или микрофон сейчас выключены.";
            }

            UsingCable = true;
            _monDev = null;
            if (hear)
            {
                string want = ScreenshotStore.Current.VoiceShiftHearId ?? "";
                string speaker = "";
                if (want.Length > 0)
                {
                    foreach (var item in renders)
                    {
                        if (item.Id == want && !item.Cable && !IsCable(item.Name))
                        {
                            speaker = want;
                            break;
                        }
                    }
                }

                if (speaker.Length == 0)
                    speaker = FirstSpeaker(renders, playId);
                if (speaker.Length > 0)
                    _monDev = _enum.GetDevice(speaker);
            }

            ScreenshotStore.SuspendDictationCapture = true;
            VoiceRecService.Shared.YieldMicToShift();

            var speak = new WaveFormat(Rate, 16, 2);
            int asked = ScreenshotStore.Current.VoiceShiftLatencyMs;
            int holdMs = VoiceShift.Shared.Library ? 280 : 90;
            if (asked >= 40)
                holdMs = VoiceShift.Shared.Library ? Math.Max(280, asked) : asked;
            _play = new BufferedWaveProvider(speak)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(holdMs)
            };
            _out = OpenOut(_outDev, _play);
            if (_monDev != null)
            {
                _monPlay = new BufferedWaveProvider(speak)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromMilliseconds(holdMs)
                };
                try { _mon = OpenOut(_monDev, _monPlay); }
                catch
                {
                    _mon = null;
                    _monPlay = null;
                    try { _monDev.Dispose(); } catch { /* ignore */ }
                    _monDev = null;
                }
            }

            _cap = OpenCap(_micDev);
            _capFormat = _cap.WaveFormat;
            _phase = 0;
            _cap.DataAvailable += OnMic;
            _out.Play();
            _mon?.Play();
            _cap.StartRecording();

            DeviceName = _outDev.FriendlyName;
            ClaimsMic = Claim(recId);
            if (!ClaimsMic)
            {
                Halt(user: true);
                VoiceRecService.Shared.ShiftStopped();
                return "Не удалось подменить микрофон Windows. Голос выключен, ты себя не слышишь.";
            }

            var saved = ScreenshotStore.Current;
            saved.VoiceShiftLiveOn = true;
            saved.VoiceShiftHear = hear;
            saved.VoiceShiftOutId = playId;
            saved.VoiceShiftMicId = micId;
            ScreenshotStore.Save(saved);

            Running = true;
            Note = hear
                ? "Включено. Микрофон Windows сейчас этот голос, и ты себя слышишь. Выключишь, и снова будет твой микрофон."
                : "Включено. Микрофон Windows сейчас этот голос. В Telegram ничего не выбирай. Себя не слышно, пока не поставишь галочку. Выключишь, и снова будет твой микрофон.";
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            bool up = Running;
            Halt(user: false);
            if (!up)
                VoiceRecService.Shared.ShiftStopped();
            return "Не удалось включить голос: " + ex.Message;
        }
    }

    public void Stop() => RequestStop();

    public void Play(float[] data)
    {
        if (data == null || data.Length == 0)
            return;
        if (Running)
        {
            VoiceBoard.Shared.Queue(data);
            return;
        }

        var copy = data;
        Task.Run(() => Preview(copy));
    }

    public static void Notify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var app = System.Windows.Application.Current;
        if (app == null)
            return;
        void Go()
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            var area = screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 800, 600);
            ScreenshotToast.Show(text.Trim(), area, 4.2);
        }

        if (app.Dispatcher.CheckAccess())
            Go();
        else
            app.Dispatcher.BeginInvoke(Go);
    }

    private void Halt(bool user)
    {
        var cap = _cap;
        var output = _out;
        var monitor = _mon;
        _cap = null;
        _out = null;
        _mon = null;
        _play = null;
        _monPlay = null;
        bool was = Running;
        Running = false;
        DeviceName = null;
        UsingCable = false;
        ClaimsMic = false;
        Peak = 0;

        if (cap != null)
        {
            try { cap.DataAvailable -= OnMic; } catch { /* ignore */ }
            try { cap.StopRecording(); } catch { /* ignore */ }
            try { Thread.Sleep(30); } catch { /* ignore */ }
            try { cap.Dispose(); } catch { /* ignore */ }
        }

        try { output?.Stop(); } catch { /* ignore */ }
        try { output?.Dispose(); } catch { /* ignore */ }
        try { monitor?.Stop(); } catch { /* ignore */ }
        try { monitor?.Dispose(); } catch { /* ignore */ }
        try { _micDev?.Dispose(); } catch { /* ignore */ }
        try { _outDev?.Dispose(); } catch { /* ignore */ }
        try { _monDev?.Dispose(); } catch { /* ignore */ }
        _micDev = null;
        _outDev = null;
        _monDev = null;
        try { _enum?.Dispose(); } catch { /* ignore */ }
        _enum = null;

        RecoverMic();
        if (user)
        {
            var s = ScreenshotStore.Current;
            s.VoiceShiftLiveOn = false;
            ScreenshotStore.Save(s);
            Note = "Выключено. Микрофон Windows снова твой.";
        }

        if (was)
        {
            VoiceRecService.Shared.ShiftStopped();
            Changed?.Invoke();
        }
    }

    private void OnMic(object? sender, WaveInEventArgs e)
    {
        if (!Running || e.BytesRecorded <= 0 || e.Buffer == null || _capFormat == null)
            return;

        int frames = FramesOf(e.Buffer, e.BytesRecorded, _capFormat);
        if (frames <= 0)
            return;
        if (_frames.Length < frames)
            _frames = new float[frames];
        frames = ToMono(e.Buffer, e.BytesRecorded, _capFormat, _frames);
        int srcRate = _capFormat.SampleRate;
        int need = srcRate == Rate ? frames : frames + 8;
        if (_mono.Length < need)
            _mono = new float[need];
        int samples = Resample(_frames, frames, srcRate, _mono);
        if (samples < 0)
            return;

        float raw = 0;
        for (int i = 0; i < samples; i++)
        {
            float a = Math.Abs(_mono[i]);
            if (a > raw)
                raw = a;
        }

        int n = VoiceShift.Shared.Process(_mono, samples, polish: true);
        var ready = VoiceShift.Shared.Ready;
        if (n > 0)
        {
            if (_mix.Length < n)
                _mix = new float[n];
            if (_pcm.Length < n * 4)
                _pcm = new byte[n * 4];
            if (_monPcm.Length < n * 4)
                _monPcm = new byte[n * 4];
            VoiceBoard.Shared.Mix(ready, n, _mix);
            WriteStereo(_play, _mix, n, _pcm, 1f);
            if (_monPlay != null)
                WriteStereo(_monPlay, _mix, n, _monPcm, VoiceShift.Shared.HearGain);
            VoiceRecService.Shared.TakeShifted(_mix, n, raw);
            Peak = raw;
        }
        else
            VoiceRecService.Shared.TakeShifted(Array.Empty<float>(), 0, raw);
    }

    private static void Write(BufferedWaveProvider? play, float[] data, int n, byte[] pcm, float gain)
    {
        if (play == null || n <= 0)
            return;
        int bytes = n * 2;
        if (pcm.Length < bytes)
            return;
        for (int i = 0; i < n; i++)
        {
            float m = data[i] * gain;
            if (m > 1f) m = 1f;
            else if (m < -1f) m = -1f;
            short v = (short)Math.Round(m * short.MaxValue);
            pcm[i * 2] = (byte)(v & 0xff);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }

        try { play.AddSamples(pcm, 0, bytes); } catch { /* буфер переполнен */ }
    }

    private static void WriteStereo(BufferedWaveProvider? play, float[] data, int n, byte[] pcm, float gain)
    {
        if (play == null || n <= 0)
            return;
        int bytes = n * 4;
        if (pcm.Length < bytes)
            return;
        for (int i = 0; i < n; i++)
        {
            float m = data[i] * gain;
            if (m > 1f) m = 1f;
            else if (m < -1f) m = -1f;
            short v = (short)Math.Round(m * short.MaxValue);
            byte lo = (byte)(v & 0xff);
            byte hi = (byte)((v >> 8) & 0xff);
            int o = i * 4;
            pcm[o] = lo;
            pcm[o + 1] = hi;
            pcm[o + 2] = lo;
            pcm[o + 3] = hi;
        }

        try { play.AddSamples(pcm, 0, bytes); } catch { /* буфер переполнен */ }
    }

    private int Resample(float[] src, int frames, int srcRate, float[] dst)
    {
        if (frames <= 0)
            return 0;
        if (srcRate == Rate)
        {
            Array.Copy(src, dst, frames);
            _phase = 0;
            return frames;
        }

        double step = srcRate / (double)Rate;
        int produced = 0;
        double pos = _phase;
        while (pos < frames && produced < dst.Length)
        {
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, frames - 1);
            float frac = (float)(pos - i0);
            dst[produced++] = src[i0] * (1f - frac) + src[i1] * frac;
            pos += step;
        }

        _phase = pos - frames;
        if (_phase < 0)
            _phase = 0;
        return produced;
    }

    private static int FramesOf(byte[] src, int bytes, WaveFormat fmt)
    {
        int bps = fmt.BitsPerSample / 8;
        int ch = Math.Max(1, fmt.Channels);
        if (bps <= 0)
            return 0;
        return bytes / (bps * ch);
    }

    private static int ToMono(byte[] src, int bytes, WaveFormat fmt, float[] dst)
    {
        int ch = Math.Max(1, fmt.Channels);
        int bps = fmt.BitsPerSample / 8;
        int frames = bytes / (bps * ch);
        bool ieee = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
        for (int i = 0; i < frames; i++)
        {
            int off = i * bps * ch;
            float sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int o = off + c * bps;
                if (o + bps > bytes)
                    break;
                if (ieee && bps >= 4)
                    sum += BitConverter.ToSingle(src, o);
                else if (bps == 2)
                    sum += (short)(src[o] | (src[o + 1] << 8)) / 32768f;
                else if (bps == 3)
                {
                    int v = src[o] | (src[o + 1] << 8) | (src[o + 2] << 16);
                    if ((v & 0x800000) != 0)
                        v |= ~0xFFFFFF;
                    sum += v / 8388608f;
                }
                else if (bps == 4)
                    sum += BitConverter.ToInt32(src, o) / 2147483648f;
            }

            dst[i] = sum / ch;
        }

        return frames;
    }

    private void Preview(float[] data)
    {
        MMDeviceEnumerator? en = null;
        MMDevice? dev = null;
        WasapiOut? output = null;
        try
        {
            en = new MMDeviceEnumerator();
            try
            {
                dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (IsCable(dev.FriendlyName))
                {
                    dev.Dispose();
                    dev = null;
                    foreach (var item in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    {
                        if (dev == null && !IsCable(item.FriendlyName))
                            dev = item;
                        else
                            item.Dispose();
                    }
                }
            }
            catch { dev = null; }
            if (dev == null)
                return;
            var speak = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, Rate, 1, Rate * 2, 2, 16);
            var play = new BufferedWaveProvider(speak)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(Math.Min(16, data.Length / (double)Rate + 0.4))
            };
            var pcm = new byte[data.Length * 2];
            Write(play, data, data.Length, pcm, 0.85f);
            output = OpenOut(dev, play);
            output.Play();
            int guard = 0;
            while (play.BufferedDuration > TimeSpan.FromMilliseconds(60) && guard < 200)
            {
                Thread.Sleep(40);
                guard++;
            }
        }
        catch
        {
            Notify("Не удалось проиграть звук.");
        }
        finally
        {
            try { output?.Stop(); } catch { /* ignore */ }
            try { output?.Dispose(); } catch { /* ignore */ }
            try { dev?.Dispose(); } catch { /* ignore */ }
            try { en?.Dispose(); } catch { /* ignore */ }
        }
    }

    private bool Claim(string cableMicId)
    {
        string prevConsole = DefaultId(DataFlow.Capture, Role.Console);
        string prevComm = DefaultId(DataFlow.Capture, Role.Communications);
        if (IsCable(NameOf(prevConsole)))
            prevConsole = FirstId(DataFlow.Capture, wantCable: false) ?? prevConsole;
        if (IsCable(NameOf(prevComm)))
            prevComm = prevConsole;
        var s = ScreenshotStore.Current;
        s.VoiceShiftPrevMic = prevConsole ?? "";
        s.VoiceShiftPrevComm = prevComm ?? "";
        ScreenshotStore.Save(s);
        if (!TrySetDefault(cableMicId, cableMicId))
            return false;
        return DefaultId(DataFlow.Capture, Role.Console) == cableMicId
            && DefaultId(DataFlow.Capture, Role.Communications) == cableMicId;
    }

    private void RecoverMic()
    {
        var s = ScreenshotStore.Current;
        if (string.IsNullOrEmpty(s.VoiceShiftPrevMic) && string.IsNullOrEmpty(s.VoiceShiftPrevComm))
            return;
        TrySetDefault(s.VoiceShiftPrevMic, string.IsNullOrEmpty(s.VoiceShiftPrevComm) ? s.VoiceShiftPrevMic : s.VoiceShiftPrevComm);
        s.VoiceShiftPrevMic = "";
        s.VoiceShiftPrevComm = "";
        ScreenshotStore.Save(s);
    }

    private string? CableCaptureId(string? renderName)
    {
        bool wide = renderName != null && renderName.Contains("16", StringComparison.Ordinal);
        string? fallback = null;
        foreach (var item in List(DataFlow.Capture, cablesFirst: true))
        {
            if (!item.Cable)
                continue;
            bool output = item.Name.Contains("Output", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("Выход", StringComparison.OrdinalIgnoreCase);
            if (!output)
            {
                fallback ??= item.Id;
                continue;
            }

            bool ch16 = item.Name.Contains("16", StringComparison.Ordinal);
            if (ch16 == wide)
                return item.Id;
            fallback ??= item.Id;
        }

        return fallback;
    }

    private MMDevice? Pick(DataFlow flow, string id, bool preferCable, string? skipId = null)
    {
        if (_enum == null)
            return null;
        if (!string.IsNullOrEmpty(id) && id != skipId)
        {
            try
            {
                var chosen = _enum.GetDevice(id);
                if (chosen.State == DeviceState.Active)
                    return chosen;
                chosen.Dispose();
            }
            catch { /* id устарел */ }
        }

        string? want = FirstId(flow, preferCable, skipId);
        if (want == null && preferCable)
            want = FirstId(flow, wantCable: false, skipId);
        if (want == null)
            return null;
        try { return _enum.GetDevice(want); }
        catch { return null; }
    }

    private string? FirstId(DataFlow flow, bool wantCable, string? skipId = null)
    {
        foreach (var item in List(flow, cablesFirst: wantCable))
        {
            if (item.Id == skipId)
                continue;
            if (item.Cable == wantCable)
                return item.Id;
        }

        return null;
    }

    private string DefaultId(DataFlow flow, Role role)
    {
        if (_enum == null)
            return "";
        try
        {
            var dev = _enum.GetDefaultAudioEndpoint(flow, role);
            string id = dev.ID;
            dev.Dispose();
            return id;
        }
        catch { return ""; }
    }

    private string NameOf(string id)
    {
        if (_enum == null || string.IsNullOrEmpty(id))
            return "";
        try
        {
            var dev = _enum.GetDevice(id);
            string name = dev.FriendlyName;
            dev.Dispose();
            return name;
        }
        catch { return ""; }
    }

    private static bool TrySetDefault(string consoleId, string commId)
    {
        if (string.IsNullOrWhiteSpace(consoleId) && string.IsNullOrWhiteSpace(commId))
            return false;
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                bool ok = true;
                if (!string.IsNullOrWhiteSpace(consoleId))
                {
                    ok &= policy.SetDefaultEndpoint(consoleId, 0) == 0;
                    ok &= policy.SetDefaultEndpoint(consoleId, 1) == 0;
                }

                string comm = string.IsNullOrWhiteSpace(commId) ? consoleId : commId;
                if (!string.IsNullOrWhiteSpace(comm))
                    ok &= policy.SetDefaultEndpoint(comm, 2) == 0;
                return ok;
            }
            finally
            {
                try { Marshal.ReleaseComObject(policy); } catch { /* ignore */ }
            }
        }
        catch { return false; }
    }

    private static WasapiCapture OpenCap(MMDevice mic)
    {
        try { return new WasapiCapture(mic, true, 20); }
        catch { /* устройство не любит короткий буфер */ }
        try { return new WasapiCapture(mic, false, 40); }
        catch { /* ещё одна попытка */ }
        return new WasapiCapture(mic);
    }

    private static WasapiOut OpenOut(MMDevice dev, IWaveProvider play)
    {
        WasapiOut? output = null;
        try
        {
            output = new WasapiOut(dev, AudioClientShareMode.Shared, true, 30);
            output.Init(play);
            return output;
        }
        catch
        {
            try { output?.Dispose(); } catch { /* ignore */ }
            output = new WasapiOut(dev, AudioClientShareMode.Shared, false, 50);
            output.Init(play);
            return output;
        }
    }

    private static void WakeCable()
    {
        string id = "";
        foreach (var item in List(DataFlow.Render, cablesFirst: true, DeviceState.Active | DeviceState.Disabled))
        {
            if (!IsCablePlay(item.Name))
                continue;
            id = item.Id;
            break;
        }

        if (id.Length == 0)
            return;
        foreach (var item in List(DataFlow.Render, cablesFirst: true))
        {
            if (item.Id == id)
                return;
        }

        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try { policy.SetEndpointVisibility(id, true); }
            finally { try { Marshal.ReleaseComObject(policy); } catch { /* ignore */ } }
        }
        catch { /* кабель останется выключенным, старт это скажет */ }

        Thread.Sleep(200);
    }

    private static List<AudioEnd> List(DataFlow flow, bool cablesFirst) =>
        List(flow, cablesFirst, DeviceState.Active);

    private static List<AudioEnd> List(DataFlow flow, bool cablesFirst, DeviceState state)
    {
        var list = new List<AudioEnd>();
        MMDeviceEnumerator? en = null;
        try
        {
            en = new MMDeviceEnumerator();
            var col = en.EnumerateAudioEndPoints(flow, state);
            for (int i = 0; i < col.Count; i++)
            {
                MMDevice? dev = null;
                try
                {
                    dev = col[i];
                    string name = dev.FriendlyName;
                    list.Add(new AudioEnd(dev.ID, name, IsCable(name)));
                }
                catch { /* устройство без имени */ }
                finally
                {
                    try { dev?.Dispose(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* звука нет */ }
        finally
        {
            try { en?.Dispose(); } catch { /* ignore */ }
        }

        list.Sort((a, b) => cablesFirst
            ? b.Cable.CompareTo(a.Cable)
            : a.Cable.CompareTo(b.Cable));
        return list;
    }
}

public sealed class VoiceClip
{
    public string Path { get; set; } = "";
    public int Key { get; set; }
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>Короткие звуки в тот же микрофон, с клавишей.</summary>
public sealed class VoiceBoard
{
    public static VoiceBoard Shared { get; } = new();

    private readonly object _gate = new();
    private readonly List<VoiceClip> _clips = new();
    private float[] _play = Array.Empty<float>();
    private int _pos;
    private int _len;

    public event Action? Changed;

    private VoiceBoard() => Load();

    public IReadOnlyList<VoiceClip> Clips
    {
        get { lock (_gate) return _clips.Select(c => new VoiceClip { Path = c.Path, Key = c.Key }).ToList(); }
    }

    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        lock (_gate)
        {
            if (_clips.Count >= 8 || _clips.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)))
                return;
            _clips.Add(new VoiceClip { Path = path });
            Save();
        }

        Changed?.Invoke();
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            _clips.RemoveAll(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        Changed?.Invoke();
    }

    public void SetKey(string path, int vk)
    {
        lock (_gate)
        {
            foreach (var clip in _clips)
            {
                if (clip.Key == vk)
                    clip.Key = 0;
                if (string.Equals(clip.Path, path, StringComparison.OrdinalIgnoreCase))
                    clip.Key = vk;
            }

            Save();
        }

        Changed?.Invoke();
    }

    public bool TryKey(int vk, Action<int> hold)
    {
        VoiceClip? clip;
        lock (_gate)
            clip = _clips.FirstOrDefault(c => c.Key == vk);
        if (clip == null)
            return false;
        hold(vk);
        Play(clip.Path);
        return true;
    }

    public void Play(string path) => Task.Run(() =>
    {
        var data = Decode(path, out var error);
        if (data == null)
        {
            VoiceShiftLive.Notify(string.IsNullOrEmpty(error) ? "Не удалось прочитать звук." : error);
            return;
        }

        VoiceShiftLive.Shared.Play(data);
    });

    public void Queue(float[] data)
    {
        lock (_gate)
        {
            _play = data;
            _pos = 0;
            _len = data.Length;
        }
    }

    public void Mix(float[] src, int n, float[] dest)
    {
        lock (_gate)
        {
            for (int i = 0; i < n; i++)
            {
                float s = i < src.Length ? src[i] : 0f;
                if (_pos < _len)
                    s += _play[_pos++] * 0.82f;
                if (s > 1f) s = 1f;
                else if (s < -1f) s = -1f;
                dest[i] = s;
            }
        }
    }

    private static float[]? Decode(string path, out string? error)
    {
        error = null;
        try
        {
            using var reader = new AudioFileReader(path);
            ISampleProvider src = reader;
            if (reader.WaveFormat.Channels == 2)
                src = new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f };
            else if (reader.WaveFormat.Channels != 1)
            {
                error = "Нужен обычный звук, моно или стерео.";
                return null;
            }

            if (src.WaveFormat.SampleRate != VoiceRecService.SampleRate)
                src = new WdlResamplingSampleProvider(src, VoiceRecService.SampleRate);
            int cap = VoiceRecService.SampleRate * 15;
            var data = new float[cap];
            int got = 0;
            while (got < cap)
            {
                int n = src.Read(data, got, Math.Min(4096, cap - got));
                if (n <= 0)
                    break;
                got += n;
            }

            if (got < 8)
            {
                error = "Файл пустой.";
                return null;
            }

            if (got < data.Length)
                Array.Resize(ref data, got);
            int fade = Math.Min(384, data.Length / 4);
            for (int i = 0; i < fade; i++)
            {
                float g = i / (float)fade;
                data[i] *= g;
                data[data.Length - 1 - i] *= g;
            }

            return data;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            var list = JsonSerializer.Deserialize<List<VoiceClip>>(File.ReadAllText(FilePath));
            if (list == null)
                return;
            foreach (var clip in list)
            {
                if (!string.IsNullOrWhiteSpace(clip.Path))
                    _clips.Add(clip);
            }
        }
        catch { /* доска подтянется пустой */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_clips));
        }
        catch { /* звук всё равно играет в эту сессию */ }
    }

    private static string FilePath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "voice-board.json");
}

[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient
{
}

[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int Slot0();
    [PreserveSig] int Slot1();
    [PreserveSig] int Slot2();
    [PreserveSig] int Slot3();
    [PreserveSig] int Slot4();
    [PreserveSig] int Slot5();
    [PreserveSig] int Slot6();
    [PreserveSig] int Slot7();
    [PreserveSig] int Slot8();
    [PreserveSig] int Slot9();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
}
