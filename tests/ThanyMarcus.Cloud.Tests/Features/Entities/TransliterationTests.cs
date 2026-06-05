using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Entities;

namespace ThanyMarcus.Cloud.Tests.Features.Entities;

public sealed class TransliterationTests
{
    [Fact]
    public void Kyiv_produces_national_and_common_variants()
    {
        var variants = Transliteration.CyrillicToLatinVariants("Київ");
        variants.ShouldContain("Kyiv");
        variants.ShouldContain("Kiev");
    }

    [Fact]
    public void Bohdan_produces_h_and_g_variants()
    {
        var variants = Transliteration.CyrillicToLatinVariants("Богдан");
        variants.ShouldContain("Bohdan");
        variants.ShouldContain("Bogdan");
    }

    [Fact]
    public void National_form_handles_iotated_and_digraphs()
    {
        Transliteration.CyrillicToLatinVariants("Харків").ShouldContain("Kharkiv");
        Transliteration.CyrillicToLatinVariants("Щербань").ShouldContain("Shcherban");
        // Word-initial Я romanizes to "Ya", mid-word to "ia".
        Transliteration.CyrillicToLatinVariants("Ярема").ShouldContain("Yarema");
    }

    [Fact]
    public void Latin_input_yields_no_variants()
    {
        Transliteration.CyrillicToLatinVariants("Kyiv").ShouldBeEmpty();
        Transliteration.CyrillicToLatinVariants("Acme Corp").ShouldBeEmpty();
        Transliteration.CyrillicToLatinVariants("").ShouldBeEmpty();
    }

    [Fact]
    public void Result_is_stable_across_calls()
    {
        var first = Transliteration.CyrillicToLatinVariants("Богдан");
        var second = Transliteration.CyrillicToLatinVariants("Богдан");
        second.ShouldBe(first);
    }

    [Fact]
    public void Variants_never_echo_the_input()
    {
        Transliteration.CyrillicToLatinVariants("Київ").ShouldNotContain("Київ");
    }

    [Fact]
    public void Seeding_is_limited_to_person_and_place_kinds()
    {
        Transliteration.SeedAliasesFor(EntityKind.Person, "Богдан").ShouldNotBeEmpty();
        Transliteration.SeedAliasesFor(EntityKind.Place, "Київ").ShouldNotBeEmpty();
        Transliteration.SeedAliasesFor(EntityKind.Concept, "Київ").ShouldBeEmpty();
        Transliteration.SeedAliasesFor(EntityKind.Organization, "Київ").ShouldBeEmpty();
    }
}
