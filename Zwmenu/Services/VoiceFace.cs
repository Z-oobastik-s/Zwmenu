namespace Zwmenu.Services;

/// <summary>Сдвиг горла отдельно от тона. Тон остаётся у SoundTouch.</summary>
internal sealed class VoiceFace
{
    private const int N = 512;
    private const int Hop = 256;
    private const int Lifter = 28;

    private readonly float[] _acc = new float[N];
    private readonly float[] _ola = new float[N];
    private readonly float[] _re = new float[N];
    private readonly float[] _im = new float[N];
    private readonly float[] _mag = new float[N];
    private readonly float[] _phaseRe = new float[N];
    private readonly float[] _phaseIm = new float[N];
    private readonly float[] _env = new float[N];
    private readonly float[] _env2 = new float[N];
    private readonly float[] _win = new float[N];
    private readonly float[] _pending = new float[4096];
    private int _accN;
    private int _pendW;
    private int _pendR;
    private int _pendN;

    public VoiceFace()
    {
        for (int i = 0; i < N; i++)
            _win[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / N);
    }

    public void Clear()
    {
        Array.Clear(_acc);
        Array.Clear(_ola);
        _accN = 0;
        _pendW = 0;
        _pendR = 0;
        _pendN = 0;
    }

    public int Process(float[] data, int count, float formantSemi)
    {
        if (count <= 0)
            return 0;
        float ratio = MathF.Pow(2f, Math.Clamp(formantSemi, -12f, 12f) / 12f);
        int read = 0;
        while (read < count)
        {
            int take = Math.Min(N - _accN, count - read);
            Array.Copy(data, read, _acc, _accN, take);
            _accN += take;
            read += take;
            if (_accN < N)
                break;
            Frame(ratio);
            Array.Copy(_acc, Hop, _acc, 0, Hop);
            _accN = Hop;
        }

        int give = Math.Min(count, _pendN);
        for (int i = 0; i < give; i++)
        {
            data[i] = _pending[_pendR];
            _pendR++;
            if (_pendR >= _pending.Length)
                _pendR = 0;
        }

        _pendN -= give;
        return give;
    }

    private void Frame(float ratio)
    {
        for (int i = 0; i < N; i++)
        {
            _re[i] = _acc[i] * _win[i];
            _im[i] = 0;
        }

        Fft(_re, _im, false);
        for (int i = 0; i < N; i++)
        {
            _phaseRe[i] = _re[i];
            _phaseIm[i] = _im[i];
            float m = MathF.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]);
            _mag[i] = m;
            _re[i] = MathF.Log(m + 1e-7f);
            _im[i] = 0;
        }

        Fft(_re, _im, true);
        for (int i = Lifter; i < N - Lifter; i++)
        {
            _re[i] = 0;
            _im[i] = 0;
        }

        for (int i = 0; i < N; i++)
            _im[i] = 0;
        Fft(_re, _im, false);
        for (int i = 0; i < N; i++)
            _env[i] = MathF.Exp(_re[i]);

        int half = N / 2;
        for (int k = 0; k <= half; k++)
        {
            float src = k / ratio;
            if (src > half)
                src = half;
            int i0 = (int)src;
            int i1 = i0 + 1;
            if (i1 > half)
                i1 = half;
            float f = src - i0;
            _env2[k] = _env[i0] * (1f - f) + _env[i1] * f;
        }

        for (int k = 1; k < half; k++)
            _env2[N - k] = _env2[k];
        _env2[half] = _env[half];

        float peak = 1e-6f;
        for (int k = 1; k < half; k++)
        {
            if (_mag[k] > peak)
                peak = _mag[k];
        }

        float floor = peak * 0.035f;
        for (int i = 0; i < N; i++)
        {
            float m = _mag[i];
            int bin = i <= half ? i : N - i;
            float hz = bin * (48000f / N);
            float gain = _env2[i] / (_env[i] + 1e-7f);
            if (m < floor || hz > 3200f)
                gain = Math.Min(gain, 1f);
            else if (gain > 1.7f)
                gain = 1.7f;
            else if (gain < 0.4f)
                gain = 0.4f;
            float shaped = m * gain;
            float pr = _phaseRe[i];
            float pi = _phaseIm[i];
            float pm = MathF.Sqrt(pr * pr + pi * pi) + 1e-8f;
            _re[i] = shaped * pr / pm;
            _im[i] = shaped * pi / pm;
        }

        Fft(_re, _im, true);
        for (int i = 0; i < N; i++)
            _ola[i] += _re[i];

        for (int i = 0; i < Hop; i++)
            Push(_ola[i]);
        Array.Copy(_ola, Hop, _ola, 0, Hop);
        Array.Clear(_ola, Hop, Hop);
    }

    private void Push(float x)
    {
        if (_pendN >= _pending.Length)
        {
            _pendR++;
            if (_pendR >= _pending.Length)
                _pendR = 0;
            _pendN--;
        }

        _pending[_pendW] = x;
        _pendW++;
        if (_pendW >= _pending.Length)
            _pendW = 0;
        _pendN++;
    }

    private static void Fft(float[] re, float[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = 2f * MathF.PI / len * (inverse ? 1f : -1f);
            float wlenRe = MathF.Cos(ang);
            float wlenIm = MathF.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float wRe = 1f;
                float wIm = 0f;
                int half = len >> 1;
                for (int j = 0; j < half; j++)
                {
                    int u = i + j;
                    int v = u + half;
                    float vr = re[v] * wRe - im[v] * wIm;
                    float vi = re[v] * wIm + im[v] * wRe;
                    re[v] = re[u] - vr;
                    im[v] = im[u] - vi;
                    re[u] += vr;
                    im[u] += vi;
                    float next = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe;
                    wRe = next;
                }
            }
        }

        if (!inverse)
            return;
        float inv = 1f / n;
        for (int i = 0; i < n; i++)
        {
            re[i] *= inv;
            im[i] *= inv;
        }
    }
}
