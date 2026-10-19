namespace Patchouli.Tests;

/// <summary>
/// Marks a real-PTY smoke test. Iciclecreek's terminal leaves render-loop state tied to the headless
/// dispatcher that created it, so running these tests in the same process as the ordinary UI tests can
/// make a later headless session throw a cross-thread compositor exception. The tests pass in
/// isolation; when their isolation changes, they can be enabled explicitly with
/// <c>PATCHOULI_RUN_PTY_TESTS=1</c>.
/// </summary>
public sealed class TerminalPtyFactAttribute : FactAttribute
{
    private const string EnableVariable = "PATCHOULI_RUN_PTY_TESTS";

    public TerminalPtyFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {EnableVariable}=1 to run this real-PTY test in an isolated process.";
        }
    }
}
