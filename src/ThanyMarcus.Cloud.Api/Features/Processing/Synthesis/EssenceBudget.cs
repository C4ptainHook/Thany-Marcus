namespace ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;

public sealed record EssenceBudget(int Units)
{
    public int MaxTags => 6;
    public int MaxWikilinks => 12;
    public int MaxColumns => 4;
    public int MaxTitleChars => 80;
    public int MaxSentenceChars => 280;
    public int MaxBulletChars => 200;
    public int MaxChecklistItemChars => 200;
    public int MaxCellChars => 120;

    public static int EstimateTokens(string? body, IReadOnlyList<SynthesisInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var chars = body?.Length ?? 0;
        foreach (var input in inputs)
        {
            if (input.Content is not null)
            {
                chars += input.Content.Length;
            }
            else
            {
                chars += (input.Title?.Length ?? 0) + (input.Description?.Length ?? 0);
            }
        }
        return chars / 4;
    }

    public static EssenceBudget For(int inputTokens)
    {
        var units = inputTokens switch
        {
            < 25  => 2,
            < 80  => 3,
            < 250 => 5,
            < 600 => 7,
            _     => 8,
        };
        return new EssenceBudget(units);
    }
}
