namespace Patchouli.Infrastructure.Shell;

public static class ShellSandboxStatus
{
    public const string Starting = "starting";
    public const string Ready = "ready";
    public const string Stopping = "stopping";
    public const string Stopped = "stopped";
    public const string Faulted = "faulted";
    public const string ProtocolIncompatible = "protocol_incompatible";
}

public sealed class ShellResourceLimits
{
    public static readonly ShellResourceLimits Default = new();

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxTerminalOutputBytes { get; init; } = 1024 * 1024;
    public int MaxCommands { get; init; } = 2000;
    public int MaxLoopIterations { get; init; } = 5000;
    public int MaxFunctionDepth { get; init; } = 16;
    public int MaxStringBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxRpcFrameBytes { get; init; } = 8 * 1024 * 1024;
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed record ShellExecuteResult(string Text, int ExitCode, bool SessionReset);

public sealed record ShellRpcError(string Code, string Message);
