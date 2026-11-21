using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Zwmenu.Services;

/// <summary>
/// Живой нейроголос. Считает кусками по 160 мс и успевает за речью на этом процессоре.
/// В кабель идёт другой тембр, не фильтр тона.
/// </summary>
sealed class MeanEngine : IDisposable
{
    const int Need = 16;
    const int Chunk = 12;
    const int Block = 4;
    const int Window = 19;
    const int Stride = 16;
    const int WavOverlap = 160;

    readonly InferenceSession _fbank;
    readonly InferenceSession _asr;
    readonly InferenceSession _gtm;
    readonly InferenceSession _dit;
    readonly InferenceSession _vocos;
    float[] _kMem = Array.Empty<float>();
    float[] _vMem = Array.Empty<float>();
    float[] _spk = Array.Empty<float>();
    readonly int _steps;
    readonly Random _rng = new(1);

    float[] _samples = Array.Empty<float>();
    float[,]? _frames;
    float[,]? _bnKeep;
    long _asrOffset = 8;
    DenseTensor<float> _att = new(new[] { 6, 4, 8, 128 });
    DenseTensor<float> _cnn = new(new[] { 6, 1, 256, 8 });
    DenseTensor<float>[] _kv = EmptyKv();
    int _vcOffset;
    float[,,]? _noise;
    float[,,]? _vocCache;
    float[]? _lastWav;
    float[,]? _bn;

    public MeanEngine(string dir, float[] spk, int steps)
    {
        var opt = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 2
        };
        _fbank = new InferenceSession(Path.Combine(dir, "fbank.onnx"), opt);
        _asr = new InferenceSession(Path.Combine(dir, "asr.int8.onnx"), opt);
        _gtm = new InferenceSession(Path.Combine(dir, "gtm.onnx"), opt);
        _dit = new InferenceSession(Path.Combine(dir, "dit.int8.onnx"), opt);
        _vocos = new InferenceSession(Path.Combine(dir, "vocos.int8.onnx"), opt);
        _steps = steps;
        _spk = spk;
        ApplyTarget(spk);
    }

    public void SetTarget(float[] spk)
    {
        _spk = spk;
        ApplyTarget(spk);
        ResetState();
    }

    void ApplyTarget(float[] spk)
    {
        var feed = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("spks", Vec(spk, 1, 256)) };
        using var got = _gtm.Run(feed);
        _kMem = got[0].AsEnumerable<float>().ToArray();
        _vMem = got[1].AsEnumerable<float>().ToArray();
    }

    void ResetState()
    {
        _samples = Array.Empty<float>();
        _frames = null;
        _bnKeep = null;
        _asrOffset = 8;
        _att = new DenseTensor<float>(new[] { 6, 4, 8, 128 });
        _cnn = new DenseTensor<float>(new[] { 6, 1, 256, 8 });
        _kv = EmptyKv();
        _vcOffset = 0;
        _noise = null;
        _vocCache = null;
        _lastWav = null;
        _bn = null;
    }

    public float[] Push(float[] samples)
    {
        var bnNew = Encode(samples);
        if (bnNew == null)
            return Array.Empty<float>();
        _bn = _bn == null ? bnNew : CatRows(_bn, bnNew);
        var parts = new List<float[]>();
        for (int n = 0; n < 4 && _bn.GetLength(0) >= Need; n++)
        {
            var cond = SliceRows(_bn, 0, Need);
            _bn = SliceRows(_bn, Chunk, _bn.GetLength(0) - Chunk);
            parts.Add(Decode(Step(cond)));
        }

        if (parts.Count == 0)
            return Array.Empty<float>();
        int len = 0;
        foreach (var p in parts)
            len += p.Length;
        var all = new float[len];
        int at = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, all, at, p.Length);
            at += p.Length;
        }

        return all;
    }

    float[,]? Encode(float[] samples)
    {
        var padded = new float[_samples.Length + samples.Length];
        Array.Copy(_samples, padded, _samples.Length);
        Array.Copy(samples, 0, padded, _samples.Length, samples.Length);
        if (padded.Length < 400)
        {
            _samples = padded;
            return null;
        }

        int n = 1 + (padded.Length - 400) / 160;
        var frames = new DenseTensor<float>(new[] { n, 400 });
        for (int i = 0; i < n; i++)
            for (int j = 0; j < 400; j++)
                frames[i, j] = padded[i * 160 + j] * 32768f;
        float[,] fb;
        using (var run = _fbank.Run(new[] { NamedOnnxValue.CreateFromTensor("frames", frames) }))
            fb = To2(run[0].AsTensor<float>(), n, 80);
        _samples = padded[(160 * n)..];
        if (_frames != null)
            fb = CatRows(_frames, fb);
        var bns = new List<float[,]>();
        int iAt = 0;
        while (iAt + Window <= fb.GetLength(0))
        {
            var xs = new DenseTensor<float>(new[] { 1, Window, 80 });
            for (int r = 0; r < Window; r++)
                for (int c = 0; c < 80; c++)
                    xs[0, r, c] = fb[iAt + r, c];
            var feeds = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("xs", xs),
                NamedOnnxValue.CreateFromTensor("offset", Scalar(_asrOffset)),
                NamedOnnxValue.CreateFromTensor("required_cache_size", Scalar(8)),
                NamedOnnxValue.CreateFromTensor("att_cache", _att),
                NamedOnnxValue.CreateFromTensor("cnn_cache", _cnn)
            };
            using var run = _asr.Run(feeds);
            var bn = run[0].AsTensor<float>();
            int rows = bn.Dimensions[1];
            var piece = new float[rows, 256];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < 256; c++)
                    piece[r, c] = bn[0, r, c];
            bns.Add(piece);
            _att = (DenseTensor<float>)run[1].AsTensor<float>().Clone();
            _cnn = (DenseTensor<float>)run[2].AsTensor<float>().Clone();
            _asrOffset += 4;
            iAt += Stride;
        }

        _frames = SliceRows(fb, iAt, fb.GetLength(0) - iAt);
        if (bns.Count == 0)
            return null;
        var joined = bns[0];
        for (int k = 1; k < bns.Count; k++)
            joined = CatRows(joined, bns[k]);
        if (_bnKeep != null)
            joined = CatRows(_bnKeep, joined);
        int keep = joined.GetLength(0);
        _bnKeep = SliceRows(joined, keep - 1, 1);
        if (keep < 2)
            return null;
        var up = Interp(joined, 17);
        return SliceRows(up, 1, 16);
    }

    float[,,] Step(float[,] cond)
    {
        int rows = cond.GetLength(0);
        var x = new float[1, rows, 80];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < 80; c++)
                x[0, r, c] = NextNormal();
        if (_noise != null)
        {
            for (int r = 0; r < Block; r++)
                for (int c = 0; c < 80; c++)
                    x[0, r, c] = _noise[0, r, c];
        }

        _noise = new float[1, Block, 80];
        for (int r = 0; r < Block; r++)
            for (int c = 0; c < 80; c++)
                _noise[0, r, c] = x[0, rows - Block + r, c];
        if (_vcOffset >= 4000)
        {
            _vcOffset = 0;
            _kv = EmptyKv();
        }

        float dt = 1f / _steps;
        var condT = new DenseTensor<float>(new[] { 1, rows, 256 });
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < 256; c++)
                condT[0, r, c] = cond[r, c];
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? last = null;
        for (int s = 0; s < _steps; s++)
        {
            float tVal = 1f - s * dt;
            float rVal = Math.Max(0f, tVal - dt);
            var xt = new DenseTensor<float>(new[] { 1, rows, 80 });
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < 80; c++)
                    xt[0, r, c] = x[0, r, c];
            var feeds = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("x", xt),
                NamedOnnxValue.CreateFromTensor("t", new DenseTensor<float>(new[] { tVal }, new[] { 1 })),
                NamedOnnxValue.CreateFromTensor("r", new DenseTensor<float>(new[] { rVal }, new[] { 1 })),
                NamedOnnxValue.CreateFromTensor("cond", condT),
                NamedOnnxValue.CreateFromTensor("spks", Vec(_spk, 1, 256)),
                NamedOnnxValue.CreateFromTensor("k_mem", Vec(_kMem, 1, 32, 256)),
                NamedOnnxValue.CreateFromTensor("v_mem", Vec(_vMem, 1, 32, 256)),
                NamedOnnxValue.CreateFromTensor("offset", Scalar(_vcOffset))
            };
            for (int j = 0; j < 4; j++)
            {
                feeds.Add(NamedOnnxValue.CreateFromTensor("k_cache" + j, _kv[2 * j]));
                feeds.Add(NamedOnnxValue.CreateFromTensor("v_cache" + j, _kv[2 * j + 1]));
            }

            last?.Dispose();
            last = _dit.Run(feeds);
            var u = last[0].AsTensor<float>();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < 80; c++)
                    x[0, r, c] -= dt * u[0, r, c];
        }

        var next = new DenseTensor<float>[8];
        for (int k = 0; k < 8; k++)
            next[k] = (DenseTensor<float>)last![k + 1].AsTensor<float>().Clone();
        last!.Dispose();
        _kv = next;
        _vcOffset += Chunk;
        var mel = new float[1, 80, Chunk];
        for (int c = 0; c < 80; c++)
            for (int r = 0; r < Chunk; r++)
                mel[0, c, r] = x[0, r, c];
        return mel;
    }

    float[] Decode(float[,,] mel)
    {
        if (_vocCache != null)
            mel = CatTime(_vocCache, mel);
        int t = mel.GetLength(2);
        _vocCache = new float[1, 80, 2];
        for (int c = 0; c < 80; c++)
            for (int k = 0; k < 2; k++)
                _vocCache[0, c, k] = mel[0, c, t - 2 + k];
        var tensor = new DenseTensor<float>(new[] { 1, 80, t });
        for (int c = 0; c < 80; c++)
            for (int k = 0; k < t; k++)
                tensor[0, c, k] = (mel[0, c, k] + 1f) * 0.5f;
        float[] wav;
        using (var run = _vocos.Run(new[] { NamedOnnxValue.CreateFromTensor("mel", tensor) }))
            wav = run[0].AsEnumerable<float>().ToArray();
        float[] fresh;
        if (_lastWav != null)
        {
            var smooth = new float[WavOverlap];
            for (int i = 0; i < WavOverlap; i++)
            {
                float down = 1f - i / (float)(WavOverlap - 1);
                smooth[i] = _lastWav[i] * down + wav[i] * (1f - down);
            }

            int mid = wav.Length - WavOverlap - WavOverlap;
            fresh = new float[WavOverlap + Math.Max(0, mid)];
            Array.Copy(smooth, fresh, WavOverlap);
            if (mid > 0)
                Array.Copy(wav, WavOverlap, fresh, WavOverlap, mid);
        }
        else
        {
            fresh = wav[..^WavOverlap];
        }

        _lastWav = wav[^WavOverlap..];
        return fresh;
    }

    float NextNormal()
    {
        double u = 1.0 - _rng.NextDouble();
        double v = 1.0 - _rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Sin(2.0 * Math.PI * v));
    }

    static DenseTensor<float>[] EmptyKv()
    {
        var kv = new DenseTensor<float>[8];
        for (int i = 0; i < 8; i++)
            kv[i] = new DenseTensor<float>(new[] { 1, 2, 0, 64 });
        return kv;
    }

    static DenseTensor<long> Scalar(long value) => new(new[] { value }, Array.Empty<int>());

    static DenseTensor<float> Vec(float[] data, params int[] shape) => new(data, shape);

    static float[,] To2(Tensor<float> tensor, int rows, int cols)
    {
        var m = new float[rows, cols];
        int k = 0;
        var flat = tensor.ToArray();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                m[r, c] = flat[k++];
        return m;
    }

    static float[,] CatRows(float[,] a, float[,] b)
    {
        int cols = a.GetLength(1);
        var m = new float[a.GetLength(0) + b.GetLength(0), cols];
        for (int r = 0; r < a.GetLength(0); r++)
            for (int c = 0; c < cols; c++)
                m[r, c] = a[r, c];
        for (int r = 0; r < b.GetLength(0); r++)
            for (int c = 0; c < cols; c++)
                m[a.GetLength(0) + r, c] = b[r, c];
        return m;
    }

    static float[,] SliceRows(float[,] src, int at, int rows)
    {
        int cols = src.GetLength(1);
        var m = new float[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                m[r, c] = src[at + r, c];
        return m;
    }

    static float[,] Interp(float[,] src, int size)
    {
        int n = src.GetLength(0);
        int cols = src.GetLength(1);
        var dst = new float[size, cols];
        if (n == 1)
        {
            for (int r = 0; r < size; r++)
                for (int c = 0; c < cols; c++)
                    dst[r, c] = src[0, c];
            return dst;
        }

        for (int i = 0; i < size; i++)
        {
            float pos = i * ((n - 1f) / (size - 1f));
            int lo = Math.Min((int)MathF.Floor(pos), n - 2);
            float w = pos - lo;
            for (int c = 0; c < cols; c++)
                dst[i, c] = src[lo, c] * (1f - w) + src[lo + 1, c] * w;
        }

        return dst;
    }

    static float[,,] CatTime(float[,,] a, float[,,] b)
    {
        int ta = a.GetLength(2);
        int tb = b.GetLength(2);
        var m = new float[1, 80, ta + tb];
        for (int c = 0; c < 80; c++)
        {
            for (int t = 0; t < ta; t++)
                m[0, c, t] = a[0, c, t];
            for (int t = 0; t < tb; t++)
                m[0, c, ta + t] = b[0, c, t];
        }

        return m;
    }

    public void Dispose()
    {
        _fbank.Dispose();
        _asr.Dispose();
        _dit.Dispose();
        _vocos.Dispose();
        _gtm.Dispose();
    }
}

/// <summary>Кладёт микрофон 48 кГц и отдаёт уже чужой голос той же длины.</summary>
internal sealed class VoiceMean
{
    const int RateIn = 48000;
    const int Rate = 16000;
    const int Hop = 2560;

    readonly object _io = new();
    readonly Action<string> _status;
    readonly float[] _acc = new float[Hop];
    readonly float[] _out = new float[RateIn * 6];
    readonly ManualResetEventSlim _wake = new(false);
    float[] _wait = new float[Hop];
    int _accN;
    int _rn;
    float _r0;
    float _r1;
    int _has;
    int _outW;
    int _outR;
    int _outN;
    int _run = 1;
    float _held;
    string _file = "";
    string _loaded = "";
    Thread? _worker;
    MeanEngine? _engine;

    public VoiceMean(Action<string> status) => _status = status;

    public void Use(string file)
    {
        lock (_io)
        {
            if (_file == file && _engine != null)
                return;
            _file = file;
            _accN = 0;
            _rn = 0;
            _has = 0;
            _outW = _outR = _outN = 0;
            _held = 0;
        }

        Ensure();
        _wake.Set();
    }

    public void Clear()
    {
        lock (_io)
        {
            _file = "";
            _has = 0;
            _accN = 0;
            _outN = 0;
        }
    }

    public int Apply(float[] src, int count, float[] dst)
    {
        if (count <= 0)
            return 0;
        lock (_io)
        {
            for (int i = 0; i < count; i++)
                Down(src[i]);
            int got = Math.Min(count, _outN);
            for (int i = 0; i < got; i++)
            {
                dst[i] = _out[_outR];
                _held = dst[i];
                _outR++;
                if (_outR >= _out.Length)
                    _outR = 0;
            }

            _outN -= got;
            for (int i = got; i < count; i++)
            {
                _held *= 0.98f;
                dst[i] = _held;
            }
            for (int i = 0; i < count; i++)
            {
                if (dst[i] > 0.97f)
                    dst[i] = 0.97f;
                else if (dst[i] < -0.97f)
                    dst[i] = -0.97f;
            }

            return count;
        }
    }

    void Down(float x)
    {
        if (_rn == 0)
        {
            _r0 = x;
            _rn = 1;
            return;
        }

        if (_rn == 1)
        {
            _r1 = x;
            _rn = 2;
            return;
        }

        _acc[_accN++] = (_r0 + _r1 + x) * (1f / 3f);
        _rn = 0;
        if (_accN < Hop || _has != 0)
        {
            if (_accN >= Hop)
                _accN = 0;
            return;
        }

        Array.Copy(_acc, _wait, Hop);
        _accN = 0;
        _has = 1;
        _wake.Set();
    }

    void Ensure()
    {
        if (_worker != null)
            return;
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Zwmenu.Mean",
            Priority = ThreadPriority.AboveNormal
        };
        _worker.Start();
    }

    void Loop()
    {
        while (Volatile.Read(ref _run) == 1)
        {
            _wake.Wait(400);
            _wake.Reset();
            if (Volatile.Read(ref _run) == 0)
                break;
            string file;
            float[]? chunk = null;
            lock (_io)
            {
                file = _file;
                if (_has != 0)
                {
                    chunk = new float[Hop];
                    Array.Copy(_wait, chunk, Hop);
                    _has = 0;
                }
            }

            if (file.Length == 0)
                continue;
            try
            {
                if (!Ready(file))
                    continue;
            }
            catch
            {
                _status("Голос не открылся.");
                Thread.Sleep(800);
                continue;
            }

            if (chunk == null || _engine == null)
                continue;
            float[] y;
            try
            {
                y = _engine.Push(chunk);
            }
            catch
            {
                _status("Голос споткнулся.");
                continue;
            }

            if (y.Length < 32)
                continue;
            var up = Up(y);
            lock (_io)
            {
                for (int i = 0; i < up.Length; i++)
                {
                    if (_outN >= _out.Length)
                    {
                        _outR++;
                        if (_outR >= _out.Length)
                            _outR = 0;
                        _outN--;
                    }

                    _out[_outW] = up[i];
                    _outW++;
                    if (_outW >= _out.Length)
                        _outW = 0;
                    _outN++;
                }
            }
        }
    }

    bool Ready(string file)
    {
        if (_engine != null && _loaded == file)
            return true;
        string dir = Folder();
        if (!File.Exists(Path.Combine(dir, "dit.int8.onnx")))
        {
            _status("Голос не найден.");
            return false;
        }

        var emb = LoadEmb(file);
        if (_engine == null)
        {
            _status("Готовлю голос.");
            _engine = new MeanEngine(dir, emb, 2);
        }
        else
            _engine.SetTarget(emb);
        _loaded = file;
        _status("");
        return true;
    }

    static float[] LoadEmb(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file);
        string path = Path.Combine(Folder(), stem + ".emb");
        if (!File.Exists(path))
            path = Path.Combine(Folder(), "default.emb");
        if (!File.Exists(path))
            return new float[256];
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 1024)
            return new float[256];
        var emb = new float[256];
        Buffer.BlockCopy(bytes, 0, emb, 0, 1024);
        return emb;
    }

    static float[] Up(float[] src)
    {
        var dst = new float[src.Length * 3];
        float prev = 0;
        int o = 0;
        for (int i = 0; i < src.Length; i++)
        {
            float s = src[i];
            dst[o++] = prev;
            dst[o++] = prev * 0.67f + s * 0.33f;
            dst[o++] = prev * 0.33f + s * 0.67f;
            prev = s;
        }

        return dst;
    }

    public static string Folder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "voices", "mean");

    public static void SelfCheck()
    {
        if (!File.Exists(Path.Combine(Folder(), "dit.int8.onnx")))
            return;
        var emb = LoadEmb("kiki.onnx");
        using var engine = new MeanEngine(Folder(), emb, 2);
        var src = new float[16000];
        for (int i = 0; i < src.Length; i++)
            src[i] = 0.22f * MathF.Sin(2f * MathF.PI * 140f * i / 16000f);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        float peak = 0;
        int got = 0;
        for (int at = 0; at + 2560 <= src.Length; at += 2560)
        {
            var chunk = new float[2560];
            Array.Copy(src, at, chunk, 0, 2560);
            foreach (float s in engine.Push(chunk))
            {
                got++;
                float a = Math.Abs(s);
                if (a > peak)
                    peak = a;
            }
        }

        clock.Stop();
        if (clock.ElapsedMilliseconds > 1600)
            throw new InvalidOperationException("Живой нейроголос не успевает");
        if (got < 8000 || peak < 0.02f)
            throw new InvalidOperationException("Живой нейроголос молчит");
    }
}
