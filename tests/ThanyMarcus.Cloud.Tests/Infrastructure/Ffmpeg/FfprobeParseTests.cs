using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Ffmpeg;

public sealed class FfprobeParseTests
{
    [Fact]
    public void Parses_duration_codecs_sample_rate()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 },
            { "codec_type": "audio", "codec_name": "aac",  "sample_rate": "48000", "channels": 2 }
          ],
          "format": { "duration": "12.34" }
        }
        """;
        var result = FfprobeRunner.Parse(json);

        result.DurationSeconds.ShouldBe(12.34, tolerance: 0.001);
        result.VideoCodec.ShouldBe("h264");
        result.AudioCodec.ShouldBe("aac");
        result.AudioSampleRate.ShouldBe(48000);
        result.AudioChannels.ShouldBe(2);
        result.VideoWidth.ShouldBe(1920);
        result.VideoHeight.ShouldBe(1080);
    }

    [Fact]
    public void Audio_only_returns_null_video_codec()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "audio", "codec_name": "pcm_s16le", "sample_rate": "16000", "channels": 1 }
          ],
          "format": { "duration": "5.0" }
        }
        """;
        var result = FfprobeRunner.Parse(json);

        result.VideoCodec.ShouldBeNull();
        result.AudioCodec.ShouldBe("pcm_s16le");
        result.AudioSampleRate.ShouldBe(16000);
        result.AudioChannels.ShouldBe(1);
    }

    [Fact]
    public void Missing_duration_returns_zero()
    {
        const string json = """
        { "streams": [], "format": {} }
        """;
        var result = FfprobeRunner.Parse(json);
        result.DurationSeconds.ShouldBe(0);
    }
}
