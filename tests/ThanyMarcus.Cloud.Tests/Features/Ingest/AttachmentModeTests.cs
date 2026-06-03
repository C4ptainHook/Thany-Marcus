using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

public sealed class AttachmentModeTests
{
    [Theory]
    [InlineData("extract", true)]
    [InlineData("reference", true)]
    [InlineData("metadata", true)]
    [InlineData("bogus", false)]
    [InlineData("", false)]
    public void IsValid_recognises_the_three_modes(string mode, bool expected)
    {
        AttachmentMode.IsValid(mode).ShouldBe(expected);
    }

    [Fact]
    public void IsValidFor_metadata_only_with_url()
    {
        AttachmentMode.IsValidFor(AttachmentMode.Metadata, AttachmentKind.Url).ShouldBeTrue();
        AttachmentMode.IsValidFor(AttachmentMode.Metadata, AttachmentKind.Image).ShouldBeFalse();
        AttachmentMode.IsValidFor(AttachmentMode.Metadata, AttachmentKind.Voice).ShouldBeFalse();
        AttachmentMode.IsValidFor(AttachmentMode.Metadata, AttachmentKind.File).ShouldBeFalse();
    }

    [Theory]
    [InlineData(AttachmentKind.Url)]
    [InlineData(AttachmentKind.Image)]
    [InlineData(AttachmentKind.Voice)]
    [InlineData(AttachmentKind.File)]
    public void Extract_and_reference_valid_for_every_kind(string kind)
    {
        AttachmentMode.IsValidFor(AttachmentMode.Extract, kind).ShouldBeTrue();
        AttachmentMode.IsValidFor(AttachmentMode.Reference, kind).ShouldBeTrue();
    }
}
