using System.IO;
using System.Media;

namespace Zwmenu.Services;

/// <summary>
/// Тихие тона: свежий SoundPlayer + PlaySync под одним локом. Без WaveOut.
/// </summary>
internal static class DictationCue
{
    private static readonly object Gate = new();
    private static readonly byte[] StartWav = BuildTone(660, 55, 0.07);
    private static readonly byte[] StopWav = BuildTone(420, 65, 0.055);
    private static readonly byte[] ClearWav = BuildTone(300, 70, 0.06);

    public static void PlayStart()
    {
        if (!ScreenshotStore.Current.DictationSounds)
            return;
        Play(StartWav);
    }

    public static void PlayStop()
    {
        if (!ScreenshotStore.Current.DictationSounds)
            return;
        Play(StopWav);
    }

    public static void PlayClear()
    {
        if (!ScreenshotStore.Current.DictationSounds)
            return;
        Play(ClearWav);
    }

    private static void Play(byte[] wav)
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Не держим Gate на весь PlaySync дольше чем нужно — но без параллельных Play.
                lock (Gate)
                {
                    using var ms = new MemoryStream(wav, writable: false);
                    using var player = new SoundPlayer(ms);
                    player.PlaySync();
                }
            }
            catch
            {
                // звук необязателен
            }
        });
    }

    private static byte[] BuildTone(double freqHz, int ms, double peak)
    {
        const int rate = 22050;
        int n = Math.Max(1, rate * ms / 1000);
        int dataBytes = n * 2;
        var wav = new byte[44 + dataBytes];

        void W(int o, int v, int len)
        {
            for (int i = 0; i < len; i++)
                wav[o + i] = (byte)((v >> (8 * i)) & 0xFF);
        }

        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        W(4, 36 + dataBytes, 4);
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        W(16, 16, 4);
        W(20, 1, 2);
        W(22, 1, 2);
        W(24, rate, 4);
        W(28, rate * 2, 4);
        W(32, 2, 2);
        W(34, 16, 2);
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        W(40, dataBytes, 4);

        int fade = Math.Max(1, n / 8);
        for (int i = 0; i < n; i++)
        {
            double env = 1.0;
            if (i < fade) env = i / (double)fade;
            else if (i > n - fade) env = (n - i) / (double)fade;
            double sample = Math.Sin(2 * Math.PI * freqHz * i / rate) * peak * env;
            short s = (short)Math.Clamp((int)(sample * short.MaxValue), short.MinValue, short.MaxValue);
            wav[44 + i * 2] = (byte)(s & 0xFF);
            wav[44 + i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        return wav;
    }
}
