using System.Net.Http;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Zwmenu.Services;

/// <summary>
/// Свой голос из файла RVC onnx. Основа считается отдельно, микрофон не ждёт сеть.
/// </summary>
internal sealed class VoiceRvc
{
    public static VoiceRvc Shared { get; } = new();

    public event Action? Changed;

    public const string Mark = "rvc:";

    const int LiveRate = 48000;
    const int ModelRate = 40000;
    const int VecRate = 16000;
    const int PitchRate = 8000;
    const int Fresh = 16384;
    const int Window = Fresh * 2;
    const string VecUrl = "https://huggingface.co/ozada/onnx_rvc/resolve/main/vec-768-layer-12.onnx";

    readonly object _io = new();
    readonly object _boot = new();
    readonly float[] _acc = new float[Fresh];
    readonly float[] _prev = new float[Fresh];
    readonly float[] _slot = new float[Window];
    readonly float[] _infer = new float[Window];
    readonly float[] _seam = new float[480];
    readonly float[] _out = new float[LiveRate * 4];
    readonly ManualResetEventSlim _wake = new(false);

    int _accN;
    int _queued;
    int _outW;
    int _outR;
    int _outN;
    int _gen;
    int _run;
    int _seamN;
    bool _havePrev;
    Thread? _worker;
    InferenceSession? _vec;
    InferenceSession? _net;
    volatile string _file = "";
    volatile string _loaded = "";
    volatile string _status = "";

    public string Status => _status;

    public static bool IsMark(string model) =>
        model.StartsWith(Mark, StringComparison.Ordinal);

    public static string TitleOf(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file);
        return stem.Replace('_', ' ');
    }

    public static IReadOnlyList<string> Names()
    {
        try
        {
            string dir = Folder();
            if (!Directory.Exists(dir))
                return Array.Empty<string>();
            var list = new List<string>();
            foreach (string path in Directory.EnumerateFiles(dir, "*.onnx"))
            {
                string name = Path.GetFileName(path);
                if (name.Equals("contentvec.onnx", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (new FileInfo(path).Length < 100_000)
                    continue;
                list.Add(name);
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder());
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Folder(),
                UseShellExecute = true
            });
        }
        catch
        {
            // папка остаётся на диске
        }
    }

    public static string Folder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "voices", "rvc");

    public void Select(string mark)
    {
        if (!IsMark(mark))
        {
            Idle();
            return;
        }

        string file = mark[Mark.Length..];
        if (_file == file)
            return;
        Interlocked.Increment(ref _gen);
        _file = file;
        lock (_io)
            ClearAudio();
        EnsureThread();
        _wake.Set();
    }

    public void Idle()
    {
        if (_file.Length == 0)
            return;
        Interlocked.Increment(ref _gen);
        _file = "";
        SetStatus("");
        lock (_io)
            ClearAudio();
    }

    public void Stop()
    {
        Volatile.Write(ref _run, 0);
        _wake.Set();
    }

    /// <summary>Кладёт микрофон и забирает готовый кусок. Расчёт идёт в стороне.</summary>
    public int Take(string mark, float semi, float[] input, int count, float[] dst)
    {
        if (count <= 0)
            return 0;
        string file = IsMark(mark) ? mark[Mark.Length..] : "";
        bool ready = file.Length > 0 && _loaded == file && _file == file && _net != null && _vec != null;
        if (!ready)
        {
            Array.Copy(input, dst, count);
            return count;
        }

        lock (_io)
        {
            int at = 0;
            while (at < count)
            {
                int room = Fresh - _accN;
                int n = Math.Min(room, count - at);
                Array.Copy(input, at, _acc, _accN, n);
                _accN += n;
                at += n;
                if (_accN >= Fresh)
                    QueueWindow(semi);
            }

            return Pull(dst, count);
        }
    }

    public static void SelfCheck()
    {
        var sine = new float[PitchRate];
        for (int i = 0; i < sine.Length; i++)
            sine[i] = MathF.Sin(2f * MathF.PI * 160f * i / PitchRate);
        float hz = PitchAt(sine, sine.Length, PitchRate / 2);
        if (hz < 140f || hz > 180f)
            throw new InvalidOperationException("Высота своего голоса промахнулась");

        var src = new float[480];
        for (int i = 0; i < src.Length; i++)
            src[i] = i / 480f;
        var dst = new float[400];
        int m = Resample(src, src.Length, LiveRate, ModelRate, dst);
        if (m != 400)
            throw new InvalidOperationException("Пересчёт своего голоса сбился");
    }

    void QueueWindow(float semi)
    {
        if (_queued > 0)
        {
            _accN = 0;
            return;
        }

        if (_havePrev)
            Array.Copy(_prev, _slot, Fresh);
        else
            Array.Clear(_slot, 0, Fresh);
        Array.Copy(_acc, 0, _slot, Fresh, Fresh);
        Array.Copy(_acc, _prev, Fresh);
        _havePrev = true;
        _accN = 0;
        _queued = 1;
        _slotSemi = semi;
        _wake.Set();
    }

    float _slotSemi;

    int Pull(float[] dst, int count)
    {
        int give = Math.Min(count, _outN);
        for (int i = 0; i < give; i++)
        {
            dst[i] = _out[_outR];
            _outR++;
            if (_outR >= _out.Length)
                _outR = 0;
        }

        _outN -= give;
        if (give < count)
            Array.Clear(dst, give, count - give);
        return count;
    }

    void ClearAudio()
    {
        _accN = 0;
        _queued = 0;
        _outW = 0;
        _outR = 0;
        _outN = 0;
        _seamN = 0;
        _havePrev = false;
        Array.Clear(_prev);
        Array.Clear(_seam);
    }

    void EnsureThread()
    {
        if (_worker != null)
            return;
        lock (_boot)
        {
            if (_worker != null)
                return;
            _run = 1;
            _worker = new Thread(Loop)
            {
                IsBackground = true,
                Name = "Zwmenu.Rvc",
                Priority = ThreadPriority.BelowNormal
            };
            _worker.Start();
        }
    }

    void Loop()
    {
        while (Volatile.Read(ref _run) == 1)
        {
            _wake.Wait(400);
            if (Volatile.Read(ref _run) == 0)
                break;
            string file = _file;
            if (file.Length == 0)
                continue;
            int gen = Volatile.Read(ref _gen);
            try
            {
                if (!EnsureLoaded(file, gen))
                    continue;
            }
            catch
            {
                SetStatus("Голос не открылся.");
                Thread.Sleep(1000);
                continue;
            }

            float semi;
            lock (_io)
            {
                if (_queued <= 0)
                    continue;
                Array.Copy(_slot, _infer, Window);
                semi = _slotSemi;
                _queued = 0;
            }

            if (gen != Volatile.Read(ref _gen))
                continue;
            try
            {
                Infer(gen, semi);
            }
            catch
            {
                SetStatus("Голос споткнулся.");
            }
        }
    }

    void Infer(int gen, float semi)
    {
        var vec = _vec;
        var net = _net;
        if (vec == null || net == null || _bind == null)
            return;
        var wav40 = new float[Window * ModelRate / LiveRate + 8];
        int n40 = Resample(_infer, Window, LiveRate, ModelRate, wav40);
        var wav16 = new float[n40 * VecRate / ModelRate + 8];
        int n16 = Resample(wav40, n40, ModelRate, VecRate, wav16);
        float[] phone = Features(vec, wav16, n16);
        if (phone.Length < 768)
            return;
        int featW = phone.Length % 768 == 0 ? 768 : 256;
        int frames = phone.Length / featW;
        var pitch = new long[frames];
        var pitchf = new float[frames];
        FillPitch(wav40, n40, frames, semi, pitch, pitchf);
        float[] audio = Speak(_bind, net, phone, frames, pitch, pitchf);
        if (audio.Length < 8 || gen != Volatile.Read(ref _gen))
            return;
        int half = Math.Max(1, audio.Length / 2);
        int start = audio.Length - half;
        var fresh = new float[Fresh];
        Stretch(audio, start, half, fresh, Fresh);
        Blend(fresh);
        lock (_io)
        {
            if (gen != Volatile.Read(ref _gen))
                return;
            for (int i = 0; i < fresh.Length; i++)
                PushOut(fresh[i]);
        }

        SetStatus("");
    }

    void Blend(float[] fresh)
    {
        int n = Math.Min(_seamN, Math.Min(fresh.Length, _seam.Length));
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 1f : i / (float)(n - 1);
            fresh[i] = _seam[i] * (1f - t) + fresh[i] * t;
        }

        int keep = Math.Min(_seam.Length, fresh.Length);
        Array.Copy(fresh, fresh.Length - keep, _seam, 0, keep);
        _seamN = keep;
    }

    void PushOut(float s)
    {
        if (float.IsNaN(s) || float.IsInfinity(s))
            s = 0;
        if (s > 0.97f)
            s = 0.97f;
        else if (s < -0.97f)
            s = -0.97f;
        if (_outN >= _out.Length)
        {
            _outR++;
            if (_outR >= _out.Length)
                _outR = 0;
            _outN--;
        }

        _out[_outW] = s;
        _outW++;
        if (_outW >= _out.Length)
            _outW = 0;
        _outN++;
    }

    bool EnsureLoaded(string file, int gen)
    {
        if (_net != null && _vec != null && _loaded == file)
            return true;
        string voice = Path.Combine(Folder(), file);
        if (!File.Exists(voice) || gen != Volatile.Read(ref _gen))
            return false;
        string vecPath = Path.Combine(Folder(), "contentvec.onnx");
        if (!File.Exists(vecPath) || new FileInfo(vecPath).Length < 20_000_000)
        {
            SetStatus("Качаю основу голоса.");
            if (!Download(vecPath) || gen != Volatile.Read(ref _gen))
                return false;
        }

        SetStatus("Готовлю голос.");
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 2
        };
        var vec = new InferenceSession(vecPath, opts);
        InferenceSession? net = null;
        try
        {
            net = new InferenceSession(voice, opts);
            var bind = Bind.Read(net);
            if (bind == null || gen != Volatile.Read(ref _gen))
            {
                SetStatus("Этот файл не подходит.");
                vec.Dispose();
                net.Dispose();
                return false;
            }

            var oldVec = _vec;
            var oldNet = _net;
            _bind = bind;
            _vec = vec;
            _net = net;
            _loaded = file;
            oldVec?.Dispose();
            oldNet?.Dispose();
            return true;
        }
        catch
        {
            vec.Dispose();
            net?.Dispose();
            SetStatus("Этот файл не подходит.");
            return false;
        }
    }

    Bind? _bind;

    static bool Download(string path)
    {
        try
        {
            Directory.CreateDirectory(Folder());
            string part = path + ".part";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, VecUrl);
            req.Headers.UserAgent.ParseAdd("Zwmenu");
            using var resp = http.Send(req);
            resp.EnsureSuccessStatusCode();
            using (var fs = File.Create(part))
            using (var stream = resp.Content.ReadAsStream())
                stream.CopyTo(fs);
            if (new FileInfo(part).Length < 20_000_000)
            {
                TryDelete(part);
                Shared.SetStatus("Основа голоса не скачалась.");
                return false;
            }

            File.Move(part, path, true);
            return true;
        }
        catch
        {
            Shared.SetStatus("Основа голоса не скачалась.");
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // временный файл можно оставить
        }
    }

    void SetStatus(string text)
    {
        if (_status == text)
            return;
        _status = text;
        Changed?.Invoke();
    }

    static float[] Features(InferenceSession vec, float[] wav, int n)
    {
        string audioName = "";
        string maskName = "";
        int rank = 2;
        foreach (var pair in vec.InputMetadata)
        {
            if (pair.Value.ElementType == typeof(float) && audioName.Length == 0)
            {
                audioName = pair.Key;
                rank = pair.Value.Dimensions.Length >= 3 ? 3 : 2;
            }
            else if (pair.Value.ElementType == typeof(long))
                maskName = pair.Key;
        }

        if (audioName.Length == 0)
            return Array.Empty<float>();
        var samples = new float[n];
        Array.Copy(wav, samples, n);
        var audio = rank == 3
            ? new DenseTensor<float>(samples, new[] { 1, 1, n })
            : new DenseTensor<float>(samples, new[] { 1, n });
        var feeds = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(audioName, audio) };
        if (maskName.Length > 0)
        {
            var mask = new DenseTensor<long>(new[] { 1, n });
            for (int i = 0; i < n; i++)
                mask[0, i] = 1;
            feeds.Add(NamedOnnxValue.CreateFromTensor(maskName, mask));
        }

        using var results = vec.Run(feeds);
        float[] data = results[0].AsEnumerable<float>().ToArray();
        var dims = results[0].AsTensor<float>().Dimensions;
        if (dims.Length == 3 && dims[1] == 768 && dims[2] != 768)
            data = Transpose(data, dims[1], dims[2]);
        return data;
    }

    static float[] ToChannels(float[] ntc, int time, int channels)
    {
        var nct = new float[time * channels];
        for (int t = 0; t < time; t++)
        {
            for (int c = 0; c < channels; c++)
                nct[c * time + t] = ntc[t * channels + c];
        }

        return nct;
    }

    static float[] Transpose(float[] nct, int channels, int time)
    {
        var ntc = new float[channels * time];
        for (int t = 0; t < time; t++)
        {
            for (int c = 0; c < channels; c++)
                ntc[t * channels + c] = nct[c * time + t];
        }

        return ntc;
    }

    static void FillPitch(float[] wav40, int n40, int frames, float semi, long[] pitch, float[] pitchf)
    {
        var low = new float[n40 * PitchRate / ModelRate + 8];
        int n8 = Resample(wav40, n40, ModelRate, PitchRate, low);
        double shift = Math.Pow(2, semi / 12.0);
        double melMin = 1127.0 * Math.Log(1.0 + 50.0 / 700.0);
        double melMax = 1127.0 * Math.Log(1.0 + 1100.0 / 700.0);
        for (int i = 0; i < frames; i++)
        {
            int center = (int)((i + 0.5) * n8 / frames);
            float hz = PitchAt(low, n8, center) * (float)shift;
            pitchf[i] = hz;
            if (hz <= 1f)
            {
                pitch[i] = 1;
                continue;
            }

            double mel = 1127.0 * Math.Log(1.0 + hz / 700.0);
            double scaled = (mel - melMin) * 254.0 / (melMax - melMin) + 1.0;
            pitch[i] = (long)Math.Clamp(Math.Round(scaled), 1, 255);
        }
    }

    static float PitchAt(float[] x, int n, int center)
    {
        const int win = 512;
        int from = Math.Clamp(center - win / 2, 0, Math.Max(0, n - win));
        int len = Math.Min(win, n - from);
        if (len < 64)
            return 0;
        int minT = Math.Max(2, PitchRate / 1100);
        int maxT = Math.Min(len / 2, PitchRate / 50);
        float acc = 0;
        int bestT = 0;
        float best = 1f;
        for (int tau = 1; tau <= maxT; tau++)
        {
            float d = 0;
            int lim = len - tau;
            for (int j = 0; j < lim; j++)
            {
                float diff = x[from + j] - x[from + j + tau];
                d += diff * diff;
            }

            acc += d;
            if (tau < minT || acc <= 1e-8f)
                continue;
            float cm = d * tau / acc;
            if (cm < 0.12f)
            {
                best = cm;
                bestT = tau;
                break;
            }

            if (cm < best)
            {
                best = cm;
                bestT = tau;
            }
        }

        if (bestT <= 0 || best > 0.3f)
            return 0;
        return PitchRate / (float)bestT;
    }

    static float[] Speak(Bind bind, InferenceSession net, float[] phone, int frames, long[] pitch, float[] pitchf)
    {
        var feeds = new List<NamedOnnxValue>();
        int feat = phone.Length / frames;
        if (bind.PhoneNct)
        {
            var laid = ToChannels(phone, frames, feat);
            feeds.Add(NamedOnnxValue.CreateFromTensor(bind.Phone, new DenseTensor<float>(laid, new[] { 1, feat, frames })));
        }
        else
            feeds.Add(NamedOnnxValue.CreateFromTensor(bind.Phone, new DenseTensor<float>(phone, new[] { 1, frames, feat })));

        if (bind.PitchLong)
            feeds.Add(NamedOnnxValue.CreateFromTensor(bind.Pitch, new DenseTensor<long>(pitch, new[] { 1, frames })));
        else
        {
            var narrow = new int[frames];
            for (int i = 0; i < frames; i++)
                narrow[i] = (int)pitch[i];
            feeds.Add(NamedOnnxValue.CreateFromTensor(bind.Pitch, new DenseTensor<int>(narrow, new[] { 1, frames })));
        }

        feeds.Add(NamedOnnxValue.CreateFromTensor(bind.PitchF, new DenseTensor<float>(pitchf, new[] { 1, frames })));
        if (bind.Length.Length > 0)
            feeds.Add(NamedOnnxValue.CreateFromTensor(bind.Length, new DenseTensor<long>(new long[] { frames }, new[] { 1 })));
        if (bind.Sid.Length > 0)
            feeds.Add(NamedOnnxValue.CreateFromTensor(bind.Sid, new DenseTensor<long>(new long[] { 0 }, new[] { 1 })));
        if (bind.Noise.Length > 0)
        {
            int channels = bind.NoiseChannels > 0 ? bind.NoiseChannels : 192;
            var noise = new float[channels * frames];
            uint state = 0x6d2b79f5;
            for (int i = 0; i < noise.Length; i++)
            {
                state = state * 1664525u + 1013904223u;
                noise[i] = ((state >> 8) & 255) / 255f * 0.1f - 0.05f;
            }

            feeds.Add(NamedOnnxValue.CreateFromTensor(bind.Noise, new DenseTensor<float>(noise, new[] { 1, channels, frames })));
        }

        using var results = net.Run(feeds);
        float[] data = results[0].AsEnumerable<float>().ToArray();
        float peak = 0;
        for (int i = 0; i < data.Length; i++)
        {
            float a = Math.Abs(data[i]);
            if (a > peak)
                peak = a;
        }

        if (peak > 4f)
        {
            for (int i = 0; i < data.Length; i++)
                data[i] /= 32768f;
        }

        return data;
    }

    static int Resample(float[] src, int n, int srcRate, int dstRate, float[] dst)
    {
        if (n <= 0)
            return 0;
        int m = (int)((long)n * dstRate / srcRate);
        if (m > dst.Length)
            m = dst.Length;
        double step = (double)srcRate / dstRate;
        for (int i = 0; i < m; i++)
        {
            double p = i * step;
            int i0 = (int)p;
            if (i0 >= n)
                i0 = n - 1;
            int i1 = i0 + 1 < n ? i0 + 1 : i0;
            float f = (float)(p - i0);
            dst[i] = src[i0] + (src[i1] - src[i0]) * f;
        }

        return m;
    }

    static void Stretch(float[] src, int start, int n, float[] dst, int m)
    {
        if (n <= 1 || m <= 0)
            return;
        for (int i = 0; i < m; i++)
        {
            double p = start + i * (n - 1.0) / Math.Max(1, m - 1);
            int i0 = (int)p;
            if (i0 >= src.Length)
                i0 = src.Length - 1;
            int i1 = i0 + 1 < src.Length ? i0 + 1 : i0;
            float f = (float)(p - i0);
            dst[i] = src[i0] + (src[i1] - src[i0]) * f;
        }
    }

    sealed class Bind
    {
        public string Phone = "";
        public bool PhoneNct;
        public string Pitch = "";
        public bool PitchLong = true;
        public string PitchF = "";
        public string Length = "";
        public string Sid = "";
        public string Noise = "";
        public int NoiseChannels;

        public static Bind? Read(InferenceSession net)
        {
            var bind = new Bind();
            int seen = 0;
            foreach (var pair in net.InputMetadata)
            {
                seen++;
                string key = pair.Key;
                string low = key.ToLowerInvariant();
                var meta = pair.Value;
                int[] dims = meta.Dimensions.ToArray();
                bool feat = false;
                foreach (int d in dims)
                {
                    if (d == 768 || d == 256)
                        feat = true;
                }

                if (low.Contains("rnd") || low.Contains("noise"))
                {
                    bind.Noise = key;
                    bind.NoiseChannels = dims.Length >= 2 && dims[1] > 1 ? dims[1] : 192;
                }
                else if (low.Contains("pitchf") || low.Contains("nsf"))
                    bind.PitchF = key;
                else if (low.Contains("pitch") || low == "f0")
                {
                    bind.Pitch = key;
                    bind.PitchLong = meta.ElementType == typeof(long);
                }
                else if (low.Contains("len"))
                    bind.Length = key;
                else if (low.Contains("phone") || low.Contains("feat") || feat)
                {
                    bind.Phone = key;
                    bind.PhoneNct = dims.Length >= 2 && (dims[1] == 768 || dims[1] == 256);
                }
                else if (low.Contains("sid") || low.Contains("spk") || low == "ds")
                    bind.Sid = key;
                else if (meta.ElementType == typeof(float) && bind.PitchF.Length == 0)
                    bind.PitchF = key;
                else if ((meta.ElementType == typeof(long) || meta.ElementType == typeof(int)) && dims.Length >= 2 && bind.Pitch.Length == 0)
                {
                    bind.Pitch = key;
                    bind.PitchLong = meta.ElementType == typeof(long);
                }
                else
                    return null;
            }

            if (seen == 0 || bind.Phone.Length == 0 || bind.Pitch.Length == 0 || bind.PitchF.Length == 0)
                return null;
            return bind;
        }
    }
}
