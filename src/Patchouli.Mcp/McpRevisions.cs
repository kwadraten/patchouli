using System;

namespace Patchouli.Mcp;

public static class McpRevisions
{
    public static string Item(DateTimeOffset updatedAt)
    {
        return $"item:{updatedAt.ToUniversalTime():O}";
    }

    public static string Style(string contentHash)
    {
        return $"style:{contentHash}";
    }
}
