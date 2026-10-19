using FluentAssertions;
using Patchouli.Core.Agents;

namespace Patchouli.Tests;

/// <summary>
/// Issue #19 D: layered Agent detection. The tests drive every layer through an injected environment
/// and process runner, so no real CLI is executed and every candidate order is deterministic.
/// </summary>
public sealed class AgentDetectionServiceTests : IDisposable
{
    private readonly AgentTestDirectory _directory = new();
    private readonly FakeAgentPlatformEnvironment _environment = new();
    private readonly ScriptedProcessRunner _runner = new();

    public AgentDetectionServiceTests()
    {
        _environment.HomeDirectory = _directory.Combine("home");
        _environment.WellKnownDirectories["HOME"] = _environment.HomeDirectory;
        _environment.WellKnownDirectories["USERPROFILE"] = _environment.HomeDirectory;
        Directory.CreateDirectory(_environment.HomeDirectory);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact]
    public async Task A_valid_custom_path_wins_without_running_the_path_or_absolute_layers()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        AgentDetectionService service = CreateService();
        _runner.Handler = request => request.FileName == executable
            ? ScriptedProcessRunner.Output("1.19.3\n")
            : ScriptedProcessRunner.NotStarted("unexpected layer");

        AgentDetectionResult result = await service.DetectAsync(
            AgentTestCatalog.OpenCode, Settings("opencode", executable));

        result.Status.Should().Be(AgentDetectionStatus.Found);
        result.IsFound.Should().BeTrue();
        result.Version.Should().Be("1.19.3");
        result.ExecutablePath.Should().Be(executable);
        result.Selected!.Source.Should().Be(AgentDetectionSource.CustomPath);
        result.Candidates.Should().ContainSingle();
        _runner.Requests.Should().ContainSingle();
        _runner.Requests[0].FileName.Should().Be(executable);
        _runner.Requests[0].Arguments.Should().Equal("--version");
    }

    [Fact]
    public async Task A_missing_custom_path_reports_InvalidCustomPath_and_never_falls_back()
    {
        string missing = _directory.Combine("tools", "missing.exe");
        AgentDetectionService service = CreateService();

        AgentDetectionResult result = await service.DetectAsync(
            AgentTestCatalog.OpenCode, Settings("opencode", missing));

        result.Status.Should().Be(AgentDetectionStatus.InvalidCustomPath);
        result.IsFound.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.Candidates.Should().ContainSingle()
            .Which.Status.Should().Be(AgentCandidateStatus.Missing);
        result.Candidates[0].Source.Should().Be(AgentDetectionSource.CustomPath);
        _runner.Requests.Should().BeEmpty("an invalid custom path must not silently use another layer");
    }

    [Fact]
    public async Task A_custom_path_that_fails_to_start_reports_InvalidExecutable_without_fallback()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        AgentDetectionService service = CreateService();
        _runner.Handler = _ => ScriptedProcessRunner.NotStarted("cannot start");

        AgentDetectionResult result = await service.DetectAsync(
            AgentTestCatalog.OpenCode, Settings("opencode", executable));

        result.Status.Should().Be(AgentDetectionStatus.InvalidExecutable);
        result.Candidates.Should().ContainSingle()
            .Which.Status.Should().Be(AgentCandidateStatus.Invalid);
        _runner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_custom_path_below_the_minimum_version_reports_VersionTooOld()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        AgentCatalogEntry entry = AgentTestCatalog.Create(minimumVersion: "2.0.0");
        AgentDetectionService service = CreateService();
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.5.0");

        AgentDetectionResult result = await service.DetectAsync(entry, Settings(entry.Id, executable));

        result.Status.Should().Be(AgentDetectionStatus.VersionTooOld);
        result.Selected.Should().BeNull();
        result.ErrorMessage.Should().Contain("1.5.0").And.Contain("2.0.0");
        result.Candidates.Should().ContainSingle()
            .Which.Status.Should().Be(AgentCandidateStatus.VersionTooOld);
    }

    [Fact]
    public async Task Process_path_candidates_skip_a_missing_file_and_pick_the_first_valid_one()
    {
        string first = _directory.Combine("bin-one", "opencode.exe");
        string second = AddExistingFile("bin-two/opencode.exe");
        AgentDetectionService service = CreateService();
        _runner.Handler = request => request.FileName == "where.exe"
            ? ScriptedProcessRunner.Output($"\"{first}\"\r\n{second}\r\n")
            : request.FileName == second
                ? ScriptedProcessRunner.Output("1.19.3")
                : ScriptedProcessRunner.NotStarted("unexpected");

        AgentDetectionResult result = await service.DetectAsync(AgentTestCatalog.OpenCode);

        result.Status.Should().Be(AgentDetectionStatus.Found);
        result.Selected!.Source.Should().Be(AgentDetectionSource.ProcessPath);
        result.ExecutablePath.Should().Be(second);
        result.Candidates.Select(candidate => candidate.Status)
            .Should().Equal(AgentCandidateStatus.Missing, AgentCandidateStatus.Valid);
        _runner.Requests.Select(request => request.FileName)
            .Should().Equal("where.exe", second);
        _runner.Requests[0].Arguments.Should().Equal("opencode");
    }

    [Fact]
    public async Task Login_shell_resolution_runs_after_PATH_and_before_absolute_candidates()
    {
        _environment.Platform = AgentPlatform.Linux;
        string executable = AddExistingFile("tools/opencode");
        _environment.ExistingFiles.Add("/bin/bash");
        string absoluteCandidate = _directory.Combine("opt", "opencode");
        AgentCatalogEntry entry = AgentTestCatalog.Create(
            linuxPaths: [absoluteCandidate], commandName: "opencode");
        AgentDetectionService service = CreateService();
        _runner.Handler = request => request.FileName == "which"
            ? ScriptedProcessRunner.Output("")
            : request.FileName == "/bin/bash"
                ? ScriptedProcessRunner.Output(executable + "\n")
                : request.FileName == executable
                    ? ScriptedProcessRunner.Output("2.3.4")
                    : ScriptedProcessRunner.NotStarted("unexpected");

        AgentDetectionResult result = await service.DetectAsync(entry);

        result.Status.Should().Be(AgentDetectionStatus.Found);
        result.Selected!.Source.Should().Be(AgentDetectionSource.LoginShell);
        result.ExecutablePath.Should().Be(executable);
        _runner.Requests.Select(request => request.FileName)
            .Should().Equal("which", "/bin/bash", executable);
        _runner.Requests[1].Arguments.Should().Equal("-lc", "command -v 'opencode'");
        _runner.Requests.Should().NotContain(request => request.FileName == absoluteCandidate);
    }

    [Fact]
    public async Task Login_shell_layer_is_skipped_on_windows()
    {
        _environment.Platform = AgentPlatform.Windows;
        _environment.ExistingFiles.Add("/bin/bash");
        string executable = AddExistingFile("tools/opencode.exe");
        AgentDetectionService service = CreateService();
        _runner.Handler = request => request.FileName == "where.exe"
            ? ScriptedProcessRunner.Output(executable)
            : request.FileName == executable
                ? ScriptedProcessRunner.Output("1.0.0")
                : ScriptedProcessRunner.NotStarted("unexpected");

        AgentDetectionResult result = await service.DetectAsync(AgentTestCatalog.OpenCode);

        result.Selected!.Source.Should().Be(AgentDetectionSource.ProcessPath);
        _runner.Requests.Should().NotContain(request =>
            request.Arguments.Contains("-lc", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Absolute_catalog_candidates_are_probed_after_the_other_layers()
    {
        _environment.Platform = AgentPlatform.Windows;
        string missing = _directory.Combine("first", "opencode.exe");
        string executable = AddExistingFile("second/opencode.exe");
        AgentCatalogEntry entry = AgentTestCatalog.Create(windowsPaths: [missing, executable]);
        AgentDetectionService service = CreateService();
        _runner.Handler = request => request.FileName == "where.exe"
            ? ScriptedProcessRunner.Output("INFO: Could not find files.\r\n")
            : request.FileName == executable
                ? ScriptedProcessRunner.Output("1.0")
                : ScriptedProcessRunner.NotStarted("unexpected");

        AgentDetectionResult result = await service.DetectAsync(entry);

        result.Status.Should().Be(AgentDetectionStatus.Found);
        result.Selected!.Source.Should().Be(AgentDetectionSource.AbsolutePath);
        result.ExecutablePath.Should().Be(executable);
        result.Candidates.Select(candidate => candidate.Status)
            .Should().Equal(AgentCandidateStatus.Missing, AgentCandidateStatus.Valid);
        _runner.Requests.Select(request => request.FileName)
            .Should().Equal("where.exe", executable);
    }

    [Fact]
    public async Task An_ACP_only_entry_is_unsupported_and_never_probes()
    {
        AgentCatalogEntry entry = AgentTestCatalog.Create(
            kind: AgentLaunchKind.AcpOnly, windowsPaths: [AddExistingFile("acp.exe")]);
        AgentDetectionService service = CreateService();

        AgentDetectionResult result = await service.DetectAsync(entry);

        result.Status.Should().Be(AgentDetectionStatus.Unsupported);
        result.IsFound.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ACP");
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Version_probe_timeout_is_capped_and_non_positive_values_fall_back_to_five_seconds()
    {
        new AgentDetectionService(_runner, _environment)
                { VersionProbeTimeout = TimeSpan.FromSeconds(60) }
            .VersionProbeTimeout.Should().Be(AgentDetectionService.MaxVersionProbeTimeout);
        new AgentDetectionService(_runner, _environment)
                { VersionProbeTimeout = TimeSpan.FromSeconds(3) }
            .VersionProbeTimeout.Should().Be(TimeSpan.FromSeconds(3));
        new AgentDetectionService(_runner, _environment)
                { VersionProbeTimeout = TimeSpan.Zero }
            .VersionProbeTimeout.Should().Be(TimeSpan.FromSeconds(5));
        new AgentDetectionService(_runner, _environment)
                { VersionProbeTimeout = TimeSpan.FromSeconds(-1) }
            .VersionProbeTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_version_probe_passes_the_catalog_arguments_and_the_configured_timeout()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        AgentCatalogEntry entry = AgentTestCatalog.Create(versionArguments: ["--version", "--json"]);
        AgentDetectionService service = new(_runner, _environment)
        {
            VersionProbeTimeout = TimeSpan.FromSeconds(3)
        };
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.0");

        AgentCandidate candidate = await service.ProbeAsync(entry, executable);

        candidate.Status.Should().Be(AgentCandidateStatus.Valid);
        _runner.Requests.Should().ContainSingle();
        _runner.Requests[0].Arguments.Should().Equal("--version", "--json");
        _runner.Requests[0].Timeout.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Windows_batch_file_probes_are_wrapped_in_the_command_processor()
    {
        _environment.Platform = AgentPlatform.Windows;
        string executable = AddExistingFile("tools/opencode.cmd");
        AgentDetectionService service = CreateService();
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.0");

        AgentCandidate candidate = await service.ProbeAsync(AgentTestCatalog.OpenCode, executable);

        candidate.Status.Should().Be(AgentCandidateStatus.Valid);
        AgentProcessRequest request = _runner.Requests.Should().ContainSingle().Subject;
        request.FileName.Should().Be("cmd.exe");
        request.Arguments.Should().Equal("/d", "/s", "/c", "call", executable, "--version");
    }

    [Fact]
    public async Task A_non_zero_probe_exit_reports_the_first_stderr_line()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Failure(3, "\r\nboom: broken install\r\nsecond line");

        AgentCandidate candidate = await CreateService().ProbeAsync(AgentTestCatalog.OpenCode, executable);

        candidate.Status.Should().Be(AgentCandidateStatus.VersionProbeFailed);
        candidate.Detail.Should().Contain("3").And.Contain("boom: broken install");
    }

    [Fact]
    public async Task A_timed_out_probe_is_a_version_probe_failure()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.TimedOut("partial");

        AgentCandidate candidate = await CreateService().ProbeAsync(AgentTestCatalog.OpenCode, executable);

        candidate.Status.Should().Be(AgentCandidateStatus.VersionProbeFailed);
        candidate.Detail.Should().Contain("版本探测超时");
    }

    [Fact]
    public async Task Successful_probe_with_empty_version_output_is_still_valid()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.Output(" \r\n\t\r\n");

        AgentCandidate candidate = await CreateService().ProbeAsync(AgentTestCatalog.OpenCode, executable);

        candidate.Status.Should().Be(AgentCandidateStatus.Valid);
        candidate.Version.Should().BeNull();
        candidate.Detail.Should().Be("版本输出为空。");
    }

    [Fact]
    public async Task Version_output_can_fall_back_to_stderr()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        _runner.Handler = _ => ScriptedProcessRunner.OutputWithError("", "opencode 7.8.9");

        AgentCandidate candidate = await CreateService().ProbeAsync(AgentTestCatalog.OpenCode, executable);

        candidate.Status.Should().Be(AgentCandidateStatus.Valid);
        candidate.Version.Should().Be("opencode 7.8.9");
    }

    [Fact]
    public async Task A_probe_with_an_empty_path_reports_missing_without_starting_a_process()
    {
        AgentCandidate candidate = await CreateService().ProbeAsync(AgentTestCatalog.OpenCode, "  ");

        candidate.Status.Should().Be(AgentCandidateStatus.Missing);
        _runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Detection_is_cached_until_the_cache_is_invalidated()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        AgentIntegrationAppSettings settings = Settings("opencode", executable);
        AgentDetectionService service = CreateService();
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.0");

        AgentDetectionResult first = await service.DetectAsync(AgentTestCatalog.OpenCode, settings);
        AgentDetectionResult second = await service.DetectAsync(AgentTestCatalog.OpenCode, settings);

        second.Should().BeSameAs(first);
        _runner.CallCount.Should().Be(1);
        service.TryGetCached("opencode", executable, out AgentDetectionResult? cached).Should().BeTrue();
        cached.Should().BeSameAs(first);
        service.TryGetCached(" opencode ", executable, out _).Should().BeTrue();

        AgentDetectionResult refreshed = await service.DetectAsync(AgentTestCatalog.OpenCode, settings, true);
        _runner.CallCount.Should().Be(2);
        refreshed.Should().NotBeSameAs(first);

        service.InvalidateCache();
        service.TryGetCached("opencode", executable, out _).Should().BeFalse();
        await service.DetectAsync(AgentTestCatalog.OpenCode, settings);
        _runner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task The_cache_key_includes_the_custom_path()
    {
        string firstPath = AddExistingFile("tools/first/opencode.exe");
        string secondPath = AddExistingFile("tools/second/opencode.exe");
        AgentDetectionService service = CreateService();
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.0");

        await service.DetectAsync(AgentTestCatalog.OpenCode, Settings("opencode", firstPath));

        service.TryGetCached("opencode", secondPath, out _).Should().BeFalse();
        service.TryGetCached("opencode", firstPath, out _).Should().BeTrue();

        await service.DetectAsync(AgentTestCatalog.OpenCode, Settings("opencode", secondPath));

        _runner.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetCached_returns_false_for_a_blank_agent_id(string? agentId)
    {
        CreateService().TryGetCached(agentId!, null, out AgentDetectionResult? result).Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public async Task An_install_offer_lookup_failure_never_breaks_detection()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        FakeInstallOfferProvider offers = new() { Exception = new IOException("registry unavailable") };
        AgentDetectionService service = new(_runner, _environment, offers);
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.0");

        AgentDetectionResult result = await service.DetectAsync(
            AgentTestCatalog.OpenCode, Settings("opencode", executable));

        result.Status.Should().Be(AgentDetectionStatus.Found);
        result.InstallOffer.Should().BeNull();
        offers.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_registry_install_offer_is_attached_independently_of_detection()
    {
        string executable = AddExistingFile("tools/opencode.exe");
        AgentInstallOffer offer = CreateOffer();
        FakeInstallOfferProvider offers = new() { Offer = offer };
        AgentDetectionService service = new(_runner, _environment, offers);
        _runner.Handler = _ => ScriptedProcessRunner.Output("1.0");

        AgentDetectionResult result = await service.DetectAsync(
            AgentTestCatalog.OpenCode, Settings("opencode", executable));

        result.InstallOffer.Should().BeSameAs(offer);
        result.IsInstallable.Should().BeTrue();
    }

    [Fact]
    public async Task Detect_all_returns_every_catalog_entry_in_catalog_order()
    {
        AgentDetectionService service = CreateService();
        _runner.Handler = _ => ScriptedProcessRunner.NotStarted("nothing installed");

        IReadOnlyList<AgentDetectionResult> results = await service.DetectAllAsync();

        results.Should().HaveCount(AgentCatalog.All.Length);
        results.Select(result => result.Entry.Id)
            .Should().Equal(AgentCatalog.All.Select(entry => entry.Id));
        results.Should().OnlyContain(result => result.Status == AgentDetectionStatus.NotInstalled);
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, 0)]
    [InlineData("opencode 1.19.3", 1, 19, 3, 0)]
    [InlineData("v2", 2, 0, 0, 0)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData("10.0.0-beta", 10, 0, 0, 0)]
    public void Agent_version_parses_the_first_numeric_run(string text, int major, int minor, int build,
        int revision)
    {
        AgentVersion.TryParse(text, out Version version).Should().BeTrue();
        version.Should().Be(new Version(major, minor, build, revision));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no digits here")]
    public void Agent_version_rejects_output_without_a_number(string? text)
    {
        AgentVersion.TryParse(text, out _).Should().BeFalse();
    }

    private AgentDetectionService CreateService()
    {
        return new AgentDetectionService(_runner, _environment);
    }

    private string AddExistingFile(string relativePath)
    {
        string path = Path.GetFullPath(_directory.Combine(
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        _environment.ExistingFiles.Add(path);
        return path;
    }

    private static AgentIntegrationAppSettings Settings(string agentId, string path)
    {
        return AgentIntegrationAppSettings.Default()
            .WithCustomPath(agentId, path);
    }

    private static AgentInstallOffer CreateOffer()
    {
        return new AgentInstallOffer(
            "opencode",
            "OpenCode",
            "1.19.3",
            "windows-x86_64",
            "https://registry.example.test/opencode.zip",
            "00",
            "./opencode.exe",
            ["acp"],
            [],
            "https://github.com/anomalyco/opencode",
            "MIT",
            "https://opensource.org/license/mit");
    }
}
