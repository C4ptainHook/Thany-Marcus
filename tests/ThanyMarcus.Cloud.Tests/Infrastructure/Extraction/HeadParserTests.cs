using Shouldly;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Extraction;

public sealed class HeadParserTests
{
    [Fact]
    public void Extracts_title_tag()
    {
        var html = "<!doctype html><html><head><title>Hello World</title></head><body></body></html>";
        var head = HeadParser.Parse(html);
        head.Title.ShouldBe("Hello World");
    }

    [Fact]
    public void Og_title_wins_over_title_tag()
    {
        var html = """
            <html><head>
              <title>Fallback Title</title>
              <meta property="og:title" content="OG Title" />
              <meta property="og:description" content="A description" />
              <meta property="og:image" content="https://example.com/img.png" />
              <meta property="og:site_name" content="MySite" />
            </head></html>
            """;
        var head = HeadParser.Parse(html);
        head.Title.ShouldBe("OG Title");
        head.Description.ShouldBe("A description");
        head.ImageUrl.ShouldBe("https://example.com/img.png");
        head.SiteName.ShouldBe("MySite");
    }

    [Fact]
    public void Finds_oembed_link()
    {
        var html = """
            <html><head>
              <title>x</title>
              <link rel="alternate" type="application/json+oembed"
                    href="https://www.youtube.com/oembed?url=https%3A//www.youtube.com/watch%3Fv%3Dabc" />
            </head></html>
            """;
        var head = HeadParser.Parse(html);
        head.OEmbedHref.ShouldNotBeNull();
        head.OEmbedHref!.ShouldContain("youtube.com/oembed");
    }

    [Fact]
    public void Resolves_relative_oembed_href_against_base_uri()
    {
        var html = """
            <html><head>
              <title>x</title>
              <link rel="alternate" type="application/json+oembed" href="/oembed?id=1" />
            </head></html>
            """;
        var head = HeadParser.Parse(html, new Uri("https://example.com/path/page"));
        head.OEmbedHref.ShouldBe("https://example.com/oembed?id=1");
    }

    [Fact]
    public void Returns_null_fields_when_head_is_empty()
    {
        var html = "<html><head></head><body></body></html>";
        var head = HeadParser.Parse(html);
        head.Title.ShouldBeNull();
        head.Description.ShouldBeNull();
        head.OEmbedHref.ShouldBeNull();
    }

    [Fact]
    public void Decodes_html_entities_in_title()
    {
        var html = "<html><head><title>Hello &amp; world</title></head></html>";
        var head = HeadParser.Parse(html);
        head.Title.ShouldBe("Hello & world");
    }
}
