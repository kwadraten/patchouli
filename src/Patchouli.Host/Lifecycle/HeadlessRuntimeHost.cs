using Patchouli.Core.Diagnostics;
using Patchouli.Core.Library;
using Patchouli.Core.Mcp;
using Patchouli.Core.Results;
using Patchouli.Host.Composition;
using Patchouli.Host.Mcp;
using Patchouli.Mcp;
using Patchouli.McpServer;
using System.Runtime.InteropServices;

namespace Patchouli.Host.Lifecycle;

/// <summary>Runs the UI executable as the persistent, UI-less owner of one Library.</summary>
public static class HeadlessRuntimeHost
{
    public static async Task<int> RunAsync(
        string databasePath,
        int? portOverride = null,
        CancellationToken cancellationToken = default)
    {
        await using RuntimeHostLease? lease = RuntimeHostCoordinator.TryAcquire(
            databasePath, RuntimeHostKind.Headless);
        if (lease is null)
        {
            Console.Error.WriteLine("[headless-host] the Library is already owned by another runtime host");
            return 8;
        }

        static void ReportUnexpected(Exception exception, string boundary, string? operation)
        {
            string context = operation is null ? boundary : $"{boundary}/{operation}";
            Console.Error.WriteLine(McpOutputSanitizer.Sanitize(
                $"Unexpected error in {context}:{Environment.NewLine}{exception}"));
        }

        UnexpectedExceptionReporter.Configure(ReportUnexpected);
        using PosixSignalRegistration? hangup = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);
        try
        {
            string settingsPath = new UI.PlatformAppPaths().Resolve().UserSettingsPath;
            HostServices services = await HostServices.CreateAsync(
                lease.DatabasePath,
                settingsPath: settingsPath,
                reportUnexpectedException: ReportUnexpected);

            Result<LibraryMetadata> library = await services.Library.GetCurrentLibraryAsync(cancellationToken);
            if (library.IsFailure)
            {
                Console.Error.WriteLine("[headless-host] the database does not contain an initialized Library");
                return 1;
            }

            McpServerSettings settings = services.Settings.Mcp;
            if (portOverride is not null)
            {
                settings = settings with { Port = portOverride.Value };
            }

            await using McpServerHost host = new(services, ReportUnexpected);
            await host.StartAsync(settings, cancellationToken);
            if (!host.IsRunning && portOverride is null)
            {
                settings = settings with { Port = RuntimeHostCoordinator.ReserveEphemeralLoopbackPort() };
                await host.StartAsync(settings, cancellationToken);
            }

            if (!host.IsRunning)
            {
                return 1;
            }

            await lease.PublishAsync(library.Value.LibraryId.ToString(), host.Endpoint, cancellationToken);
            Console.Error.WriteLine($"[headless-host] listening at {host.Endpoint}");

            using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                Task takeover = lease.WaitForTakeoverRequestAsync(shutdown.Token);
                Task stopped = host.WaitForShutdownAsync(shutdown.Token);
                Task completed = await Task.WhenAny(takeover, stopped);
                if (completed == takeover)
                {
                    await takeover;
                    Console.Error.WriteLine("[headless-host] desktop takeover accepted");
                }

                shutdown.Cancel();
                await host.StopAsync("Runtime host is shutting down.", CancellationToken.None);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                await host.StopAsync("Runtime host is shutting down.", CancellationToken.None);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            ReportUnexpected(exception, "headless-host", "run");
            return 1;
        }
    }
}
