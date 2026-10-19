using System.Text;

namespace Patchouli.Core.Agents;

/// <summary>Expands catalog path templates without touching the real environment.</summary>
public static class AgentPathExpansion
{
    /// <summary>
    /// Expands <c>%NAME%</c> and <c>$NAME</c> variables and a leading <c>~</c> (or <c>~/</c>,
    /// <c>~\</c>), then returns a fully qualified path. Only well-known directory names resolve;
    /// anything else is left literal so the following probe fails visibly.
    /// </summary>
    public static string Expand(string? path, IAgentPlatformEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string expanded = path.Trim();
        if (expanded == "~")
        {
            expanded = environment.HomeDirectory;
        }
        else if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
                 expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(environment.HomeDirectory, expanded[2..]);
        }

        expanded = ExpandPercentVariables(expanded, environment);
        expanded = ExpandDollarVariables(expanded, environment);
        return Path.GetFullPath(expanded);
    }

    private static string ExpandPercentVariables(string value, IAgentPlatformEnvironment environment)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder builder = new(value.Length);
        int index = 0;
        while (index < value.Length)
        {
            int start = value.IndexOf('%', index);
            if (start < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, start - index);
            int end = value.IndexOf('%', start + 1);
            if (end < 0)
            {
                builder.Append(value, start, value.Length - start);
                break;
            }

            string name = value[(start + 1)..end];
            if (environment.TryResolveWellKnown(name, out string? replacement) && replacement is not null)
            {
                builder.Append(replacement);
            }
            else
            {
                builder.Append(value, start, end - start + 1);
            }

            index = end + 1;
        }

        return builder.ToString();
    }

    private static string ExpandDollarVariables(string value, IAgentPlatformEnvironment environment)
    {
        if (!value.Contains('$', StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder builder = new(value.Length);
        int index = 0;
        while (index < value.Length)
        {
            int start = value.IndexOf('$', index);
            if (start < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, start - index);
            int nameStart = start + 1;
            bool braced = nameStart < value.Length && value[nameStart] == '{';
            if (braced)
            {
                nameStart++;
            }

            int nameEnd = nameStart;
            while (nameEnd < value.Length && (char.IsAsciiLetterOrDigit(value[nameEnd]) || value[nameEnd] == '_'))
            {
                nameEnd++;
            }

            bool terminated = !braced || (nameEnd < value.Length && value[nameEnd] == '}');
            string name = nameEnd > nameStart ? value[nameStart..nameEnd] : string.Empty;
            if (terminated && name.Length > 0 &&
                environment.TryResolveWellKnown(name, out string? replacement) && replacement is not null)
            {
                builder.Append(replacement);
                index = braced ? nameEnd + 1 : nameEnd;
                continue;
            }

            builder.Append('$');
            index = start + 1;
        }

        return builder.ToString();
    }
}
