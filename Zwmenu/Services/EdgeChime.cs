using NAudio.Wave;

namespace Zwmenu.Services;

/// <summary>
/// Тихий переход между ноутбуками. Два коротких тона, без щелчка в начале.
/// Уход мягко поднимается, возвращение опускается.
/// </summary>
static class EdgeChime
{
    private const int Rate = 44100;
    private static readonly byte[] LeavePcm = Build(rise: true);
    private static readonly byte[] ArrivePcm = Build(rise: false);
    private static readonly object Gate = new();
    private static Player? _leave;
    private static Player? _arrive;
    private static long _last;

    public static void Leave() => Play(leave: true);

    public static void Arrive() => Play(leave: false);

    private static void Play(bool leave)
    {
        if (!UiSounds.Enabled || UiSounds.Suspended)
            return;
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _last) < 160)
            return;
        Volatile.Write(ref _last, now);
        _ = Task.Run(() =>
        {
            try
            {
                lock (Gate)
                {
                    if (leave)
                        (_leave ??= new Player(LeavePcm)).Replay();
                    else
                        (_arrive ??= new Player(ArrivePcm)).Replay();
                }
            }
            catch
            {
                // без звука переход всё равно работает
            }
        });
    }

    private static byte[] Build(bool rise)
    {
        int n = (int)(Rate * 0.36);
        var pcm = new byte[n * 2];
        double from = rise ? 392.00 : 523.25;
        double to = rise ? 523.25 : 392.00;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double attack = Math.Sin(Math.Min(1, t / 0.03) * Math.PI / 2);
            double env = attack * Math.Exp(-t * 5.6);
            double glide = t / 0.36;
            double f = from + (to - from) * glide * glide;
            double s = Math.Sin(2 * Math.PI * f * t);
            s += 0.14 * Math.Sin(2 * Math.PI * f * 2 * t + 0.35);
            s += 0.06 * Math.Sin(2 * Math.PI * f * 1.498 * t);
            double v = s * env * 0.15;
            short sample = (short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }

        return pcm;
    }

    private sealed class Player
    {
        private readonly WaveOutEvent _out;
        private readonly MemoryStream _stream;
        private readonly RawSourceWaveStream _raw;

        public Player(byte[] pcm)
        {
            _stream = new MemoryStream(pcm, writable: false);
            _raw = new RawSourceWaveStream(_stream, new WaveFormat(Rate, 16, 1));
            _out = new WaveOutEvent { DesiredLatency = 90, NumberOfBuffers = 3 };
            _out.Init(_raw);
        }

        public void Replay()
        {
            _out.Stop();
            _stream.Position = 0;
            _raw.Position = 0;
            _out.Play();
        }
    }
}
