using NAudio.Wave;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

public static class SilenceDetector
{
    public static float MaxRmsFromWav(Stream wav, int windowMs)
    {
        ArgumentNullException.ThrowIfNull(wav);
        if (windowMs <= 0) windowMs = 100;

        using var reader = new WaveFileReader(wav);
        var format = reader.WaveFormat;
        var samplesPerWindow = Math.Max(1, format.SampleRate * windowMs / 1000) * format.Channels;
        var buffer = new float[samplesPerWindow];

        var provider = reader.ToSampleProvider();
        float maxRms = 0f;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            double sumSq = 0;
            for (var i = 0; i < read; i++)
            {
                double v = buffer[i];
                sumSq += v * v;
            }
            var rms = (float)Math.Sqrt(sumSq / read);
            if (rms > maxRms) maxRms = rms;
        }
        return maxRms;
    }
}
