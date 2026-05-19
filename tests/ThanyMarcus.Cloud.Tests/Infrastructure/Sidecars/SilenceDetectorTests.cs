using NAudio.Wave;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class SilenceDetectorTests
{
    [Fact]
    public void Silent_wav_returns_near_zero_rms()
    {
        using var wav = MakeWav(amplitude: 0);
        var rms = SilenceDetector.MaxRmsFromWav(wav, windowMs: 100);
        rms.ShouldBeLessThan(1e-6f);
    }

    [Fact]
    public void Loud_sine_returns_substantial_rms()
    {
        using var wav = MakeWav(amplitude: 0.5f);
        var rms = SilenceDetector.MaxRmsFromWav(wav, windowMs: 100);
        rms.ShouldBeGreaterThan(0.1f);
    }

    [Fact]
    public void Quiet_sine_under_threshold_returns_low_rms()
    {
        using var wav = MakeWav(amplitude: 0.001f);
        var rms = SilenceDetector.MaxRmsFromWav(wav, windowMs: 100);
        rms.ShouldBeLessThan(0.005f);
    }

    private static MemoryStream MakeWav(float amplitude, int sampleRate = 16000, double seconds = 1.0)
    {
        var format = new WaveFormat(sampleRate, 16, 1);
        var ms = new MemoryStream();
        using (var w = new WaveFileWriter(new IgnoreDisposeStream(ms), format))
        {
            var samples = (int)(seconds * sampleRate);
            var buffer = new short[samples];
            for (var i = 0; i < samples; i++)
            {
                var t = i / (double)sampleRate;
                var sample = amplitude * Math.Sin(2 * Math.PI * 440 * t);
                buffer[i] = (short)(sample * short.MaxValue);
            }
            w.WriteSamples(buffer, 0, buffer.Length);
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
