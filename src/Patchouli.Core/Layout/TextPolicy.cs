namespace Patchouli.Core.Layout;

public static class TextPolicy
{
    public const string Own = "own";
    public const string AggregateChildren = "aggregate_children";
    public const string None = "none";

    public static bool IsKnown(string policy)
    {
        return policy is Own or AggregateChildren or None;
    }
}
