using System.Text;

namespace Patchouli.Host.Agent;

/// <summary>Routes script Console output by execution context while other host output keeps its original destination.</summary>
internal static class AgentFsiConsoleCapture
{
    private static readonly AsyncLocal<Capture?> Current = new();
    private static readonly object InstallGate = new();
    private static bool _installed;

    public static IDisposable Begin(TextWriter output, TextWriter error)
    {
        lock (InstallGate)
        {
            if (!_installed)
            {
                Console.SetOut(new RoutingWriter(Console.Out, false));
                Console.SetError(new RoutingWriter(Console.Error, true));
                _installed = true;
            }
        }

        Capture capture = new(output, error);
        Scope scope = new(capture, Current.Value);
        Current.Value = capture;
        return scope;
    }

    private sealed class Capture(TextWriter output, TextWriter error)
    {
        public object Gate { get; } = new();
        public bool Active { get; set; } = true;
        public TextWriter Output { get; } = output;
        public TextWriter Error { get; } = error;
    }

    private sealed class Scope(Capture capture, Capture? previous) : IDisposable
    {
        public void Dispose()
        {
            lock (capture.Gate)
            {
                capture.Active = false;
            }

            Current.Value = previous;
        }
    }

    private sealed class RoutingWriter(TextWriter fallback, bool isError) : TextWriter
    {
        public override Encoding Encoding => fallback.Encoding;

        public override void Write(char value)
        {
            Write(value.ToString());
        }

        public override void Write(string? value)
        {
            Capture? capture = Current.Value;
            if (capture is null)
            {
                fallback.Write(value);
                return;
            }

            lock (capture.Gate)
            {
                if (capture.Active)
                {
                    (isError ? capture.Error : capture.Output).Write(value);
                }
                else
                {
                    // Background work may inherit the context but outlive the interaction.
                    fallback.Write(value);
                }
            }
        }
    }
}
