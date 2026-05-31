using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class WordListTests
{
    [Fact]
    public void Generate_produces_eight_words_that_round_trip()
    {
        var phrase = WordList.Generate();

        phrase.Split(' ').Length.ShouldBe(8);
        WordList.TryUnpack(phrase, out var bytes).ShouldBeTrue();
        bytes.Length.ShouldBe(11);
        WordList.Pack(bytes).ShouldBe(phrase);
    }

    [Fact]
    public void Pack_then_unpack_recovers_the_original_bytes()
    {
        var bytes = new byte[11];
        new Random(20260531).NextBytes(bytes);

        var phrase = WordList.Pack(bytes);
        WordList.TryUnpack(phrase, out var recovered).ShouldBeTrue();

        recovered.ShouldBe(bytes);
    }

    [Fact]
    public void All_zero_entropy_maps_to_first_word_repeated()
    {
        var phrase = WordList.Pack(new byte[11]);
        phrase.ShouldBe(string.Join(' ', Enumerable.Repeat("abandon", 8)));
    }

    [Fact]
    public void Generate_is_not_deterministic()
    {
        var a = WordList.Generate();
        var b = WordList.Generate();
        a.ShouldNotBe(b);
    }

    [Fact]
    public void TryUnpack_rejects_words_outside_the_list()
    {
        WordList.TryUnpack("notaword river amber cloud iron stone vivid coral", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryUnpack_rejects_the_wrong_word_count()
    {
        WordList.TryUnpack("abandon ability able", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryUnpack_tolerates_casing_and_extra_whitespace()
    {
        var bytes = new byte[11];
        new Random(7).NextBytes(bytes);
        var phrase = WordList.Pack(bytes);

        var messy = "   " + phrase.ToUpperInvariant().Replace(" ", "    ") + "  ";
        WordList.TryUnpack(messy, out var recovered).ShouldBeTrue();
        recovered.ShouldBe(bytes);
    }
}
