using Patchouli.Mcp;

namespace Patchouli.UI.Services;

internal static class DesktopUriActivation
{
    internal static bool IsValid(string? uri)
    {
        return uri is not null && uri.StartsWith("patchouli://", StringComparison.OrdinalIgnoreCase) &&
               !uri.Any(char.IsControl) && McpResourceUris.Parse(uri).IsSuccess;
    }

    internal static string? FromArguments(string[] args)
    {
        if (args.Contains("--open-uri", StringComparer.Ordinal))
        {
            if (args.Length != 2 || args[0] != "--open-uri" || !IsValid(args[1]))
            {
                throw new ArgumentException("--open-uri 需要一个有效的 patchouli:// 文献虚拟链接。");
            }

            return args[1];
        }

        string[] links = args.Where(arg => arg.StartsWith("patchouli:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (links.Length == 0)
        {
            return null;
        }

        if (links.Length != 1 || !IsValid(links[0]))
        {
            throw new ArgumentException("请提供一个有效的 patchouli:// 文献虚拟链接。");
        }

        return links[0];
    }
}
