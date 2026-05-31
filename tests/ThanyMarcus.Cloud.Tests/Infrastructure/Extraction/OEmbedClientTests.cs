using System.Text;
using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Extraction;

public sealed class OEmbedClientTests
{
    [Fact]
    public void Parses_youtube_shape()
    {
        var json = """
            {
              "title": "Some Video",
              "author_name": "Awesome Channel",
              "author_url": "https://www.youtube.com/channel/abc",
              "provider_name": "YouTube",
              "thumbnail_url": "https://i.ytimg.com/vi/abc/hqdefault.jpg",
              "type": "video",
              "html": "<iframe ...></iframe>"
            }
            """;
        var oe = OEmbedClient.Parse(Encoding.UTF8.GetBytes(json));
        oe.ShouldNotBeNull();
        oe!.Title.ShouldBe("Some Video");
        oe.AuthorName.ShouldBe("Awesome Channel");
        oe.ProviderName.ShouldBe("YouTube");
        oe.ThumbnailUrl.ShouldNotBeNullOrWhiteSpace();
        oe.Type.ShouldBe("video");
    }

    [Fact]
    public void Returns_null_on_invalid_json()
    {
        var oe = OEmbedClient.Parse(Encoding.UTF8.GetBytes("not json"));
        oe.ShouldBeNull();
    }

    [Fact]
    public void Returns_null_on_non_object_root()
    {
        var oe = OEmbedClient.Parse(Encoding.UTF8.GetBytes("[]"));
        oe.ShouldBeNull();
    }

    [Fact]
    public void Missing_fields_are_null()
    {
        var oe = OEmbedClient.Parse(Encoding.UTF8.GetBytes("{\"title\":\"only title\"}"));
        oe.ShouldNotBeNull();
        oe!.Title.ShouldBe("only title");
        oe.AuthorName.ShouldBeNull();
        oe.ProviderName.ShouldBeNull();
    }
}
