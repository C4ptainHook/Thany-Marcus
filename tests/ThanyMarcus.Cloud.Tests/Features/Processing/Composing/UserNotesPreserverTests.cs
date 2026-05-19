using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Composing;

public sealed class UserNotesPreserverTests
{
    [Fact]
    public void Null_previous_body_returns_empty_placeholder()
    {
        UserNotesPreserver.Extract(null).ShouldBe(UserNotesPreserver.EmptyPlaceholder);
    }

    [Fact]
    public void Empty_previous_body_returns_empty_placeholder()
    {
        UserNotesPreserver.Extract("").ShouldBe(UserNotesPreserver.EmptyPlaceholder);
    }

    [Fact]
    public void Preserves_user_notes_block_between_markers()
    {
        const string body = "---\nid: x\n---\n\n## User Notes\n\nmy thoughts\n\n## System Output\n\nOLD";
        UserNotesPreserver.Extract(body).ShouldBe("my thoughts");
    }

    [Fact]
    public void Missing_system_output_marker_returns_placeholder()
    {
        const string body = "## User Notes\n\nmy thoughts\n\n## NOT SYSTEM OUTPUT";
        UserNotesPreserver.Extract(body).ShouldBe(UserNotesPreserver.EmptyPlaceholder);
    }

    [Fact]
    public void Whitespace_only_notes_returns_placeholder()
    {
        const string body = "## User Notes\n\n   \n\n## System Output\n\nstuff";
        UserNotesPreserver.Extract(body).ShouldBe(UserNotesPreserver.EmptyPlaceholder);
    }

    [Fact]
    public void Multi_paragraph_notes_are_preserved()
    {
        const string body = "## User Notes\n\nfirst para\n\nsecond para\n\n## System Output\n\nstuff";
        UserNotesPreserver.Extract(body).ShouldBe("first para\n\nsecond para");
    }
}
