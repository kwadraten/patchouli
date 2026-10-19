namespace Patchouli.Terminal;

/// <summary>
/// Describes a process to host in an <see cref="EmbeddedTerminal"/>.
/// </summary>
public sealed class TerminalSessionOptions
{
    public const int DefaultInitialCols = 120;
    public const int DefaultInitialRows = 30;

    /// <summary>
    /// Gets the executable to launch. Specify an absolute path when the executable is not on <c>PATH</c>.
    /// </summary>
    public required string Executable { get; init; }

    /// <summary>
    /// Gets the arguments passed to <see cref="Executable"/>. Each entry is delivered as one
    /// argument, or joined into one command line when <see cref="VerbatimCommandLine"/> is set.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets the initial working directory. When <see langword="null"/>, the host process directory is used.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets extra environment variables. They are merged into the inherited environment rather than replacing it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Gets the requested starting column count. The terminal layout remains the resize authority.
    /// </summary>
    public int InitialCols { get; init; } = DefaultInitialCols;

    /// <summary>
    /// Gets the requested starting row count. The terminal layout remains the resize authority.
    /// </summary>
    public int InitialRows { get; init; } = DefaultInitialRows;

    /// <summary>
    /// Gets whether <see cref="Arguments"/> are joined into one unquoted command line instead of
    /// being passed as individually quoted arguments. This is required for processors such as
    /// <c>cmd.exe /c</c> that parse their own command line. Windows only; Unix always uses argv.
    /// </summary>
    public bool VerbatimCommandLine { get; init; }

    /// <summary>
    /// Creates options for the default interactive shell on the current operating system.
    /// </summary>
    public static TerminalSessionOptions ForDefaultShell(string? workingDirectory = null)
    {
        return DefaultShellResolver.CreateOptions(workingDirectory);
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable))
        {
            throw new ArgumentException("A terminal executable is required.", nameof(Executable));
        }

        if (InitialCols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialCols), InitialCols,
                "The initial column count must be positive.");
        }

        if (InitialRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialRows), InitialRows,
                "The initial row count must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && !Directory.Exists(WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The terminal working directory does not exist: {WorkingDirectory}");
        }
    }
}
