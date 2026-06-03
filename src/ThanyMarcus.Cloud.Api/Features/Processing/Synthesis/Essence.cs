using System.Text.Json.Serialization;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public sealed record ProseEssence(
    [property: JsonPropertyName("title")]     string Title,
    [property: JsonPropertyName("tags")]      IReadOnlyList<string> Tags,
    [property: JsonPropertyName("wikilinks")] IReadOnlyList<string> Wikilinks,
    [property: JsonPropertyName("sentences")] IReadOnlyList<string> Sentences);

public sealed record BulletsEssence(
    [property: JsonPropertyName("title")]     string Title,
    [property: JsonPropertyName("tags")]      IReadOnlyList<string> Tags,
    [property: JsonPropertyName("wikilinks")] IReadOnlyList<string> Wikilinks,
    [property: JsonPropertyName("bullets")]   IReadOnlyList<string> Bullets);

public sealed record ChecklistEssence(
    [property: JsonPropertyName("title")]     string Title,
    [property: JsonPropertyName("tags")]      IReadOnlyList<string> Tags,
    [property: JsonPropertyName("wikilinks")] IReadOnlyList<string> Wikilinks,
    [property: JsonPropertyName("items")]     IReadOnlyList<ChecklistEntry> Items);

public sealed record ChecklistEntry(
    [property: JsonPropertyName("text")]    string Text,
    [property: JsonPropertyName("checked")] bool Checked);

public sealed record TableEssence(
    [property: JsonPropertyName("title")]     string Title,
    [property: JsonPropertyName("tags")]      IReadOnlyList<string> Tags,
    [property: JsonPropertyName("wikilinks")] IReadOnlyList<string> Wikilinks,
    [property: JsonPropertyName("columns")]   IReadOnlyList<string> Columns,
    [property: JsonPropertyName("rows")]      IReadOnlyList<TableRow> Rows);

public sealed record TableRow(
    [property: JsonPropertyName("cells")] IReadOnlyList<string> Cells);
