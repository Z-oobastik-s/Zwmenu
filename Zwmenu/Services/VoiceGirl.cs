using System.Net.Http;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Zwmenu.Services;

/// <summary>
/// Женский голос из открытой библиотеки hevoicechanger (GPL-3.0).
/// Считается на своём потоке и не трогает образы.
/// </summary>
internal sealed class VoiceGirl
{
    public static VoiceGirl Shared { get; } = new();

    private const int ModelRate = 16000;
    private const int History = 3200;
    private const int Chunk = 3200;
    private const int Span = History + Chunk;
    private const string Source = "https://github.com/ultrapg/hevoicechanger/raw/main/models/";

    private readonly object _io = new();
    private readonly object _boot = new();
    private readonly float[] _hist = new float[History];
    private readonly float[] _acc = new float[Chunk];
    private readonly float[] _slot0 = new float[Span];
    private readonly float[] _slot1 = new float[Span];
    private readonly float[] _infer = new float[Span];
    private readonly float[] _tensorBuf = new float[Span];
    private readonly float[] _tail = new float[Chunk];
    private readonly float[] _up = new float[Chunk * 3];
    private readonly float[] _out = new float[ModelRate * 4];
    private readonly ManualResetEventSlim _wake = new(false);

    private float _r0;
    private float _r1;
    private int _rn;
    private int _accN;
    private int _qW;
    private int _qR;
    private int _qN;
    private int _outW;
    private int _outR;
    private int _outN;
    private float _upPrev;
    private int _gen;
    private int _run;
    private Thread? _worker;
    private InferenceSession? _session;
    private string _inputName = "audio_in";
    private volatile string _file = "";
    private volatile string _loaded = "";
    private volatile string _status = "";

    public event Action? Changed;

    public string Status => _status;

    public void Select(string file)
    {
        if (string.IsNullOrEmpty(file))
        {
            Idle();
            return;
        }

        if (!Known(file) || _file == file)
            return;
        Interlocked.Increment(ref _gen);
        _file = file;
        lock (_io)
            ClearRings();
        EnsureThread();
        _wake.Set();
    }

    public void Idle()
    {
        if (string.IsNullOrEmpty(_file))
            return;
        Interlocked.Increment(ref _gen);
        _file = "";
        SetStatus("");
        lock (_io)
            ClearRings();
    }

    public void Stop()
    {
        Volatile.Write(ref _run, 0);
        _wake.Set();
    }

    /// <summary>Кладёт микрофон и забирает уже готовый кусок. Сеть и расчёт здесь не идут.</summary>
    public int Take(string file, float[] input, int count, float[] dst)
    {
        if (count <= 0)
            return 0;
        bool ready = _loaded == file && _file == file && _session != null;
        if (!ready)
        {
            Array.Copy(input, dst, count);
            return count;
        }

        lock (_io)
        {
            for (int i = 0; i < count; i++)
                Push16(input[i]);
            return Pull(dst, count);
        }
    }

    public static void SelfCheck()
    {
        string path = Path.Combine(Folder(), "female_voice_hfg_new.onnx");
        if (!File.Exists(path))
            return;
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 2
        };
        using var session = new InferenceSession(path, opts);
        string name = session.InputMetadata.Keys.First();
        var samples = new float[Span];
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)ModelRate;
            samples[i] = 0.2f * MathF.Sin(2f * MathF.PI * 120f * t);
        }

        var tensor = new DenseTensor<float>(samples, new[] { 1, 1, samples.Length });
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(name, tensor) });
        float[] data = results[0].AsEnumerable<float>().ToArray();
        if (data.Length < Chunk)
            throw new InvalidOperationException("Библиотека девушки вернула короткий кусок");
        float peak = 0;
        for (int i = data.Length - Chunk; i < data.Length; i++)
        {
            float s = data[i];
            if (float.IsNaN(s) || float.IsInfinity(s))
                throw new InvalidOperationException("Библиотека девушки дала пустой отсчёт");
            float a = Math.Abs(s);
            if (a > peak)
                peak = a;
        }

        if (peak < 0.004f)
            throw new InvalidOperationException("Библиотека девушки молчит");
    }

    private void Push16(float x)
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
        if (_accN >= Chunk)
            FlushChunk();
    }

    private void FlushChunk()
    {
        if (_qN < 2)
        {
            float[] slot = _qW == 0 ? _slot0 : _slot1;
            Array.Copy(_hist, 0, slot, 0, History);
            Array.Copy(_acc, 0, slot, History, Chunk);
            _qW ^= 1;
            _qN++;
            _wake.Set();
        }
        else
            Emit(_acc, Chunk, 1f);

        Array.Copy(_acc, _hist, Chunk);
        _accN = 0;
    }

    private int Pull(float[] dst, int count)
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
        return give;
    }

    private void Emit(float[] src, int n, float gain)
    {
        float prev = _upPrev;
        int o = 0;
        for (int i = 0; i < n; i++)
        {
            float s = src[i] * gain;
            if (s > 0.97f)
                s = 0.97f;
            else if (s < -0.97f)
                s = -0.97f;
            _up[o++] = prev;
            _up[o++] = prev * 0.67f + s * 0.33f;
            _up[o++] = prev * 0.33f + s * 0.67f;
            prev = s;
        }

        _upPrev = prev;
        for (int i = 0; i < o; i++)
        {
            if (_outN >= _out.Length)
            {
                _outR++;
                if (_outR >= _out.Length)
                    _outR = 0;
                _outN--;
            }

            _out[_outW] = _up[i];
            _outW++;
            if (_outW >= _out.Length)
                _outW = 0;
            _outN++;
        }
    }

    private void ClearRings()
    {
        _rn = 0;
        _accN = 0;
        _qW = 0;
        _qR = 0;
        _qN = 0;
        _outW = 0;
        _outR = 0;
        _outN = 0;
        _upPrev = 0;
        Array.Clear(_hist);
        Array.Clear(_acc);
    }

    private void EnsureThread()
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
                Name = "Zwmenu.Girl",
                Priority = ThreadPriority.BelowNormal
            };
            _worker.Start();
        }
    }

    private void Loop()
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

            if (!Steal())
                continue;
            if (gen != Volatile.Read(ref _gen))
                continue;
            try
            {
                Infer(gen);
            }
            catch
            {
                SetStatus("Голос споткнулся.");
            }
        }
    }

    private bool Steal()
    {
        lock (_io)
        {
            if (_qN <= 0)
                return false;
            Array.Copy(_qR == 0 ? _slot0 : _slot1, _infer, Span);
            _qR ^= 1;
            _qN--;
            return true;
        }
    }

    private void Infer(int gen)
    {
        var session = _session;
        if (session == null)
            return;
        var tensor = new DenseTensor<float>(_tensorBuf, new[] { 1, 1, Span });
        Array.Copy(_infer, _tensorBuf, Span);
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        if (gen != Volatile.Read(ref _gen))
            return;
        float[] data = results[0].AsEnumerable<float>().ToArray();
        if (data.Length == 0 || gen != Volatile.Read(ref _gen))
            return;
        int take = Math.Min(Chunk, data.Length);
        int start = data.Length - take;
        float energy = 0;
        for (int i = 0; i < take; i++)
        {
            float s = data[start + i];
            if (float.IsNaN(s) || float.IsInfinity(s))
                s = 0;
            _tail[i] = s;
            energy += s * s;
        }

        float rms = MathF.Sqrt(energy / take);
        float gain = rms < 0.012f ? 1f : Math.Clamp(0.12f / rms, 0.7f, 4f);
        lock (_io)
        {
            if (gen != Volatile.Read(ref _gen))
                return;
            Emit(_tail, take, gain);
        }
    }

    private bool EnsureLoaded(string file, int gen)
    {
        if (_session != null && _loaded == file)
            return true;
        if (!Known(file) || gen != Volatile.Read(ref _gen))
            return false;
        string path = Path.Combine(Folder(), file);
        if (!File.Exists(path) || new FileInfo(path).Length < 1_000_000)
        {
            SetStatus("Качаю голос.");
            if (!Download(file, path) || gen != Volatile.Read(ref _gen))
                return false;
        }

        if (gen != Volatile.Read(ref _gen))
            return false;
        SetStatus("Готовлю голос.");
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 2
        };
        var next = new InferenceSession(path, opts);
        if (gen != Volatile.Read(ref _gen))
        {
            next.Dispose();
            return false;
        }

        string input = next.InputMetadata.Keys.FirstOrDefault() ?? "audio_in";
        var previous = _session;
        _inputName = input;
        _session = next;
        _loaded = file;
        previous?.Dispose();
        SetStatus("");
        return true;
    }

    private static bool Download(string file, string path)
    {
        try
        {
            Directory.CreateDirectory(Folder());
            string part = path + ".part";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            using var req = new HttpRequestMessage(HttpMethod.Get, Source + file);
            req.Headers.UserAgent.ParseAdd("Zwmenu");
            using var resp = http.Send(req);
            resp.EnsureSuccessStatusCode();
            using (var fs = File.Create(part))
            using (var stream = resp.Content.ReadAsStream())
                stream.CopyTo(fs);
            if (new FileInfo(part).Length < 1_000_000)
            {
                TryDelete(part);
                Shared.SetStatus("Голос не скачался.");
                return false;
            }

            File.Move(part, path, true);
            return true;
        }
        catch
        {
            Shared.SetStatus("Голос не скачался.");
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* временный файл можно оставить */
        }
    }

    private void SetStatus(string text)
    {
        if (_status == text)
            return;
        _status = text;
        Changed?.Invoke();
    }

    private static bool Known(string file) =>
        file is "female_voice_hfg_new.onnx" or "female_voice_old_hq.onnx" or "female_voice_old.onnx";

    private static string Folder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zwmenu", "voices");
}
