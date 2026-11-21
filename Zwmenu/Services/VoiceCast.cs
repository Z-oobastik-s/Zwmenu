using SoundTouch;

namespace Zwmenu.Services;

/// <summary>
/// Живой тембр для кабеля. Полная модель на этом процессоре не успевает за речью,
/// поэтому телеграм получает сдвиг тона и горла сразу, без тишины и без сухого микрофона.
/// </summary>
internal sealed class VoiceCast
{
    const int Rate = 48000;

    readonly SoundTouchProcessor _touch = new();
    readonly VoiceFace _face = new();
    readonly float[] _pull = new float[8192];
    float _semi = 8f;
    float _formant = 3f;
    bool _open;

    public void Reset(string file)
    {
        Tune(file, out _semi, out _formant);
        if (_open)
        {
            _touch.Clear();
            _touch.PitchSemiTones = _semi;
        }

        _face.Clear();
    }

    public int Apply(float[] src, int count, float[] dst)
    {
        if (count <= 0)
            return 0;
        Open();
        if (Math.Abs(_touch.PitchSemiTones - _semi) > 0.02f)
            _touch.PitchSemiTones = _semi;
        _touch.PutSamples(new ReadOnlySpan<float>(src, 0, count), count);
        int got = 0;
        while (got < count)
        {
            int room = Math.Min(_pull.Length, count - got);
            int n = _touch.ReceiveSamples(new Span<float>(_pull, 0, room), room);
            if (n <= 0)
                break;
            Array.Copy(_pull, 0, dst, got, n);
            got += n;
        }

        if (got <= 0)
            return 0;
        got = _face.Process(dst, got, _formant);
        for (int i = 0; i < got; i++)
        {
            float x = dst[i];
            if (x > 0.97f)
                x = 0.97f;
            else if (x < -0.97f)
                x = -0.97f;
            dst[i] = x;
        }

        return got;
    }

    void Open()
    {
        if (_open)
            return;
        _touch.SampleRate = Rate;
        _touch.Channels = 1;
        _touch.Tempo = 1f;
        _touch.Rate = 1f;
        _touch.PitchSemiTones = _semi;
        _touch.SetSetting(SettingId.UseQuickSeek, 0);
        _touch.SetSetting(SettingId.UseAntiAliasFilter, 1);
        _touch.SetSetting(SettingId.AntiAliasFilterLength, 32);
        _touch.SetSetting(SettingId.SequenceDurationMs, 28);
        _touch.SetSetting(SettingId.SeekWindowDurationMs, 14);
        _touch.SetSetting(SettingId.OverlapDurationMs, 8);
        _open = true;
    }

    public static void Tune(string file, out float semi, out float formant)
    {
        switch (file)
        {
            case "butian.onnx":
                semi = 7.5f;
                formant = 3.6f;
                break;
            case "guaigui.onnx":
                semi = 9.2f;
                formant = 4.4f;
                break;
            case "keruan.onnx":
                semi = 4.2f;
                formant = 1.6f;
                break;
            case "kiki.onnx":
                semi = 11f;
                formant = 5.2f;
                break;
            case "yueyue.onnx":
                semi = 6f;
                formant = 2.8f;
                break;
            default:
                semi = 8f;
                formant = 3.2f;
                break;
        }
    }

    public static void SelfCheck()
    {
        var cast = new VoiceCast();
        cast.Reset("kiki.onnx");
        const int n = 48000;
        var src = new float[n];
        for (int i = 0; i < n; i++)
            src[i] = 0.35f * MathF.Sin(2f * MathF.PI * 120f * i / Rate);
        var whole = new float[n];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int total = 0;
        for (int at = 0; at < n; at += 480)
        {
            int chunk = Math.Min(480, n - at);
            var dst = new float[chunk];
            var piece = new float[chunk];
            Array.Copy(src, at, piece, 0, chunk);
            int got = cast.Apply(piece, chunk, dst);
            int room = Math.Min(got, n - total);
            if (room > 0)
                Array.Copy(dst, 0, whole, total, room);
            total += room;
        }

        if (clock.ElapsedMilliseconds > 1000)
            throw new InvalidOperationException("Живой голос не успевает");
        if (total < n / 2)
            throw new InvalidOperationException("Живой голос проглотил речь");
        int from = total / 3;
        int len = total - from;
        float energy = 0;
        for (int i = from; i < total; i++)
            energy += whole[i] * whole[i];
        if (energy / len < 0.004f)
            throw new InvalidOperationException("Живой голос молчит");
        float hz = Crossings(whole, from, len);
        if (hz < 170f || hz > 320f)
            throw new InvalidOperationException("Живой голос не сдвинул тон");
    }

    static float Crossings(float[] x, int at, int n)
    {
        int z = 0;
        for (int i = at + 1; i < at + n; i++)
        {
            if (x[i - 1] <= 0f && x[i] > 0f)
                z++;
        }

        return z * (float)Rate / n;
    }
}
