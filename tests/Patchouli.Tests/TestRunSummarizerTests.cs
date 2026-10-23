using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace Patchouli.Tests;

public sealed class TestRunSummarizerTests
{
    [Theory]
    [InlineData("Completed", 2, "", false)]
    [InlineData("Completed", 3, "", true)]
    [InlineData("Aborted", 2, "", true)]
    [InlineData("Completed", 2, "The test run was aborted because the test host exited.", true)]
    [InlineData("Completed", 2, "Tests.Cancellation_timeout_terminates_child_process [Passed]", false)]
    public async Task Summary_distinguishes_skipped_results_and_test_names_from_incomplete_runs(
        string outcome, int total, string runInfo, bool incomplete)
    {
        string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            $"patchouli-summary-{Guid.NewGuid():N}")).FullName;
        try
        {
            string trx = Path.Combine(directory, "results.trx");
            XDocument document = new(new XElement("TestRun",
                new XElement("Results",
                    new XElement("UnitTestResult", new XAttribute("testName", "Tests.Passed"),
                        new XAttribute("outcome", "Passed")),
                    new XElement("UnitTestResult", new XAttribute("testName", "Tests.Skipped"),
                        new XAttribute("outcome", "NotExecuted"))),
                new XElement("ResultSummary", new XAttribute("outcome", outcome),
                    new XElement("Counters", new XAttribute("total", total),
                        new XAttribute("executed", 1), new XAttribute("notExecuted", 0)),
                    new XElement("Output", new XElement("StdOut",
                        "Tests.Cancellation_timeout_is_retried [Passed]")),
                    new XElement("RunInfos", new XElement("RunInfo", new XElement("Text", runInfo))))));
            await File.WriteAllTextAsync(trx, document.ToString());
            ProcessStartInfo start = new("python")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(TestPaths.FromRepositoryRoot("scripts", "summarize-test-run.py"));
            start.ArgumentList.Add("--trx");
            start.ArgumentList.Add(trx);
            using Process process = new() { StartInfo = start };
            Assert.True(process.Start());
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                string stderr = await errors;
                Assert.True(process.ExitCode == 0, stderr);
                using JsonDocument summary = JsonDocument.Parse(await output);
                Assert.Equal(incomplete ? "incomplete" : "ok", summary.RootElement.GetProperty("status").GetString());
                Assert.Equal(1, summary.RootElement.GetProperty("skipped").GetInt32());
                Assert.Equal(incomplete, summary.RootElement.GetProperty("incomplete_signals").GetArrayLength() > 0);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync();
                }

                await Task.WhenAll(output, errors);
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
