namespace Zwmenu.Services;

/// <summary>Снимок настроек обработки на момент старта записи.</summary>
internal sealed class VoiceRecFx
{
    public bool Master = true;
    public float InputGainDb;
    public bool HighPass = true;
    public float HighPassHz = 75f;
    public bool Gate = true;
    public float GateDb = -52f;
    public bool Compress = true;
    public float CompressStrength = 55f; // 0..100
    public bool Presence = true;
    public float PresenceDb = 2.8f;
    public bool Warmth = true;
    public float WarmthDb = 1.8f;
    public bool DeEsser = true;
    public bool Limiter = true;
    public bool Agc;
    public bool Normalize = true;
    public bool TrimSilence = true;
    public int FadeMs = 12;
    public float MonitorVolume = 0.55f;
    public int PreRollMs = 220;
    public int BitDepth = 16;
    /// <summary>wav16 / wav24 / mp3 / m4a / flac / ogg.</summary>
    public string Format = "mp3";

    public static VoiceRecFx FromSettings(ScreenshotSettings s)
    {
        string format = ScreenshotStore.NormalizeVoiceRecFormat(s.VoiceRecFormat, s.VoiceRecBitDepth);
        int bits = format is "wav24" ? 24 : 16;
        if (!s.VoiceRecEnhance)
        {
            return new VoiceRecFx
            {
                Master = false,
                InputGainDb = s.VoiceRecInputGainDb,
                MonitorVolume = s.VoiceRecMonitorVolume / 100f,
                PreRollMs = s.VoiceRecPreRollMs,
                BitDepth = bits,
                Format = format,
                Normalize = false,
                TrimSilence = false,
                FadeMs = 0
            };
        }

        return new VoiceRecFx
        {
            Master = true,
            InputGainDb = s.VoiceRecInputGainDb,
            HighPass = s.VoiceRecHighPass,
            HighPassHz = s.VoiceRecHighPassHz,
            Gate = s.VoiceRecGate,
            GateDb = s.VoiceRecGateDb,
            Compress = s.VoiceRecCompress,
            CompressStrength = s.VoiceRecCompressStrength,
            Presence = s.VoiceRecPresence,
            PresenceDb = s.VoiceRecPresenceDb,
            Warmth = s.VoiceRecWarmth,
            WarmthDb = s.VoiceRecWarmthDb,
            DeEsser = s.VoiceRecDeEsser,
            Limiter = s.VoiceRecLimiter,
            Agc = s.VoiceRecAgc,
            Normalize = s.VoiceRecNormalize,
            TrimSilence = s.VoiceRecTrimSilence,
            FadeMs = s.VoiceRecFadeMs,
            MonitorVolume = s.VoiceRecMonitorVolume / 100f,
            PreRollMs = s.VoiceRecPreRollMs,
            BitDepth = bits,
            Format = format
        };
    }
}

/// <summary>
/// Цепочка обработки голоса по включённым эффектам. Float -1…1, 48 kHz mono.
/// </summary>
internal sealed class VoiceRecDsp
{
    private readonly VoiceRecFx _fx;
    private readonly float _sampleRate;
    private readonly float _inputGain;
    private readonly Biquad? _highPass;
    private readonly Biquad? _warmth;
    private readonly Biquad? _presence;
    private readonly Biquad? _deEsser;
    private readonly NoiseGate? _gate;
    private readonly Compressor? _comp;
    private readonly Limiter? _lim;
    private readonly Agc? _agc;

    public VoiceRecDsp(VoiceRecFx fx, float sampleRate = 48000f)
    {
        _fx = fx;
        _sampleRate = sampleRate;
        _inputGain = DbToLin(fx.InputGainDb);

        if (fx.Master && fx.HighPass)
            _highPass = Biquad.HighPass(sampleRate, Math.Clamp(fx.HighPassHz, 40f, 200f), 0.707f);

        if (fx.Master && fx.Warmth && fx.WarmthDb > 0.05f)
            _warmth = Biquad.Peaking(sampleRate, 220f, 0.9f, Math.Clamp(fx.WarmthDb, 0f, 8f));

        if (fx.Master && fx.Gate)
        {
            float open = Math.Clamp(fx.GateDb, -70f, -25f);
            _gate = new NoiseGate(sampleRate, openDb: open, closeDb: open - 6f, attackMs: 5f, releaseMs: 120f);
        }

        if (fx.Master && fx.Compress)
        {
            float strength = Math.Clamp(fx.CompressStrength, 0f, 100f) / 100f;
            float ratio = 1.5f + strength * 4.5f;       // 1.5..6
            float thr = -18f - strength * 10f;          // -18..-28
            float makeup = strength * 5f;
            _comp = new Compressor(sampleRate, thr, ratio, attackMs: 8f, releaseMs: 90f, makeupDb: makeup);
        }

        if (fx.Master && fx.Presence && fx.PresenceDb > 0.05f)
            _presence = Biquad.Peaking(sampleRate, 3200f, 1.1f, Math.Clamp(fx.PresenceDb, 0f, 10f));

        if (fx.Master && fx.DeEsser)
            _deEsser = Biquad.Peaking(sampleRate, 7500f, 2.5f, -2.4f);

        if (fx.Master && fx.Limiter)
            _lim = new Limiter(sampleRate, ceilingDb: -0.7f, releaseMs: 40f);

        if (fx.Master && fx.Agc)
            _agc = new Agc(sampleRate, targetDb: -16f);
    }

    public float Process(float x)
    {
        x *= _inputGain;
        if (_highPass != null) x = _highPass.Process(x);
        if (_warmth != null) x = _warmth.Process(x);
        if (_gate != null) x = _gate.Process(x);
        if (_comp != null) x = _comp.Process(x);
        if (_agc != null) x = _agc.Process(x);
        if (_presence != null) x = _presence.Process(x);
        if (_deEsser != null) x = _deEsser.Process(x);
        if (_lim != null) x = _lim.Process(x);
        return x;
    }

    public void ProcessBuffer(float[] buffer, int count)
    {
        for (int i = 0; i < count; i++)
            buffer[i] = Process(buffer[i]);
    }

    public static float[] Finalize(float[] samples, float sampleRate, VoiceRecFx fx)
    {
        if (samples.Length == 0)
            return samples;

        float[] cut = samples;
        if (fx.TrimSilence)
        {
            const float silence = 0.0045f;
            int pad = (int)(sampleRate * 0.08f);
            int first = 0;
            while (first < samples.Length && Math.Abs(samples[first]) < silence)
                first++;
            int last = samples.Length - 1;
            while (last > first && Math.Abs(samples[last]) < silence)
                last--;
            int start = Math.Max(0, first - pad);
            int end = Math.Min(samples.Length, last + 1 + pad);
            int len = Math.Max(0, end - start);
            if (len == 0)
                return Array.Empty<float>();
            cut = new float[len];
            Array.Copy(samples, start, cut, 0, len);
        }

        if (fx.Normalize && cut.Length > 0)
        {
            float peak = 0.0001f;
            for (int i = 0; i < cut.Length; i++)
                peak = Math.Max(peak, Math.Abs(cut[i]));
            float gain = Math.Min(8f, 0.92f / peak);
            if (Math.Abs(gain - 1f) > 0.02f)
            {
                for (int i = 0; i < cut.Length; i++)
                    cut[i] = Math.Clamp(cut[i] * gain, -1f, 1f);
            }
        }

        int fade = Math.Clamp(fx.FadeMs, 0, 80);
        if (fade > 0 && cut.Length > 0)
        {
            int n = Math.Min((int)(sampleRate * fade / 1000f), cut.Length / 4);
            for (int i = 0; i < n; i++)
            {
                float w = i / (float)n;
                cut[i] *= w;
                cut[cut.Length - 1 - i] *= w;
            }
        }

        return cut;
    }

    private sealed class Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        public static Biquad HighPass(float fs, float freq, float q)
        {
            double w0 = 2 * Math.PI * freq / fs;
            double cos = Math.Cos(w0);
            double sin = Math.Sin(w0);
            double alpha = sin / (2 * q);
            double b0 = (1 + cos) / 2;
            double b1 = -(1 + cos);
            double b2 = (1 + cos) / 2;
            double a0 = 1 + alpha;
            double a1 = -2 * cos;
            double a2 = 1 - alpha;
            return From(b0, b1, b2, a0, a1, a2);
        }

        public static Biquad Peaking(float fs, float freq, float q, float gainDb)
        {
            double a = Math.Pow(10, gainDb / 40.0);
            double w0 = 2 * Math.PI * freq / fs;
            double cos = Math.Cos(w0);
            double sin = Math.Sin(w0);
            double alpha = sin / (2 * q);
            double b0 = 1 + alpha * a;
            double b1 = -2 * cos;
            double b2 = 1 - alpha * a;
            double a0 = 1 + alpha / a;
            double a1 = -2 * cos;
            double a2 = 1 - alpha / a;
            return From(b0, b1, b2, a0, a1, a2);
        }

        private static Biquad From(double b0, double b1, double b2, double a0, double a1, double a2) =>
            new()
            {
                _b0 = (float)(b0 / a0),
                _b1 = (float)(b1 / a0),
                _b2 = (float)(b2 / a0),
                _a1 = (float)(a1 / a0),
                _a2 = (float)(a2 / a0)
            };

        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }

    private sealed class NoiseGate
    {
        private readonly float _open, _close, _atk, _rel;
        private float _env, _gain = 1f;
        private bool _openState;

        public NoiseGate(float fs, float openDb, float closeDb, float attackMs, float releaseMs)
        {
            _open = DbToLin(openDb);
            _close = DbToLin(closeDb);
            _atk = MathF.Exp(-1f / (fs * attackMs / 1000f));
            _rel = MathF.Exp(-1f / (fs * releaseMs / 1000f));
        }

        public float Process(float x)
        {
            float a = Math.Abs(x);
            _env = a > _env ? a : _env * _rel + a * (1 - _rel);
            if (_openState)
            {
                if (_env < _close) _openState = false;
            }
            else if (_env > _open)
                _openState = true;

            float target = _openState ? 1f : 0f;
            float coeff = target > _gain ? _atk : _rel;
            _gain = _gain * coeff + target * (1 - coeff);
            return x * _gain;
        }
    }

    private sealed class Compressor
    {
        private readonly float _thr, _ratio, _atk, _rel, _makeup;
        private float _env;

        public Compressor(float fs, float thresholdDb, float ratio, float attackMs, float releaseMs, float makeupDb)
        {
            _thr = DbToLin(thresholdDb);
            _ratio = ratio;
            _atk = MathF.Exp(-1f / (fs * attackMs / 1000f));
            _rel = MathF.Exp(-1f / (fs * releaseMs / 1000f));
            _makeup = DbToLin(makeupDb);
        }

        public float Process(float x)
        {
            float a = Math.Abs(x);
            _env = a > _env ? _env * _atk + a * (1 - _atk) : _env * _rel + a * (1 - _rel);
            float gain = 1f;
            if (_env > _thr && _env > 1e-8f)
            {
                float over = _env / _thr;
                float compressed = MathF.Pow(over, 1f / _ratio);
                gain = compressed / over;
            }

            return x * gain * _makeup;
        }
    }

    private sealed class Limiter
    {
        private readonly float _ceiling, _rel;
        private float _gain = 1f;

        public Limiter(float fs, float ceilingDb, float releaseMs)
        {
            _ceiling = DbToLin(ceilingDb);
            _rel = MathF.Exp(-1f / (fs * releaseMs / 1000f));
        }

        public float Process(float x)
        {
            float a = Math.Abs(x);
            float need = a > _ceiling ? _ceiling / a : 1f;
            _gain = need < _gain ? need : _gain * _rel + need * (1 - _rel);
            return x * _gain;
        }
    }

    private sealed class Agc
    {
        private readonly float _target, _atk, _rel;
        private float _gain = 1f;

        public Agc(float fs, float targetDb)
        {
            _target = DbToLin(targetDb);
            _atk = MathF.Exp(-1f / (fs * 0.35f));
            _rel = MathF.Exp(-1f / (fs * 1.2f));
        }

        public float Process(float x)
        {
            float a = Math.Abs(x);
            if (a < 1e-5f)
                return x;
            float need = Math.Clamp(_target / a, 0.25f, 6f);
            float c = need > _gain ? _atk : _rel;
            _gain = _gain * c + need * (1 - c);
            return x * _gain;
        }
    }

    private static float DbToLin(float db) => MathF.Pow(10f, db / 20f);
}
