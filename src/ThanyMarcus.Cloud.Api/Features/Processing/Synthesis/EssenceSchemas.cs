using System.Text.Json.Nodes;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public static class EssenceSchemas
{
    public static JsonObject Build(EssenceForm form, EssenceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var properties = new JsonObject
        {
            ["title"] = StringSchema(budget.MaxTitleChars),
            ["tags"] = StringArray(budget.MaxTags, itemMaxChars: 40, minItems: 1),
            ["wikilinks"] = StringArray(budget.MaxWikilinks, itemMaxChars: 80, minItems: 0),
        };
        var required = new JsonArray("title", "tags", "wikilinks");

        switch (form)
        {
            case EssenceForm.Prose:
                properties["sentences"] = StringArray(budget.Units, budget.MaxSentenceChars, minItems: 1);
                required.Add("sentences");
                break;
            case EssenceForm.Bullets:
                properties["bullets"] = StringArray(budget.Units, budget.MaxBulletChars, minItems: 1);
                required.Add("bullets");
                break;
            case EssenceForm.Checklist:
                properties["items"] = ChecklistArray(budget.Units, budget.MaxChecklistItemChars);
                required.Add("items");
                break;
            case EssenceForm.Table:
                properties["columns"] = StringArray(budget.MaxColumns, itemMaxChars: 40, minItems: 1);
                properties["rows"] = TableRowArray(budget.Units, budget.MaxCellChars);
                required.Add("columns");
                required.Add("rows");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(form), form, "unknown form");
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject StringSchema(int maxChars) => new()
    {
        ["type"] = "string",
        ["maxLength"] = maxChars,
    };

    private static JsonObject StringArray(int maxItems, int itemMaxChars, int minItems) => new()
    {
        ["type"] = "array",
        ["items"] = StringSchema(itemMaxChars),
        ["minItems"] = minItems,
        ["maxItems"] = maxItems,
    };

    private static JsonObject ChecklistArray(int maxItems, int itemMaxChars) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["text"] = StringSchema(itemMaxChars),
                ["checked"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray("text", "checked"),
            ["additionalProperties"] = false,
        },
        ["minItems"] = 1,
        ["maxItems"] = maxItems,
    };

    private static JsonObject TableRowArray(int maxItems, int cellMaxChars) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["cells"] = StringArray(maxItems: 8, itemMaxChars: cellMaxChars, minItems: 1),
            },
            ["required"] = new JsonArray("cells"),
            ["additionalProperties"] = false,
        },
        ["minItems"] = 1,
        ["maxItems"] = maxItems,
    };
}
