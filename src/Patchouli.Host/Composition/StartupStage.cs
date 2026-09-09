namespace Patchouli.Host.Composition;

public enum StartupStage
{
    ValidatingPaths,
    ComposingServices,
    ApplyingMigrations,
    AdoptingRootBindings,
    ReconcilingOcrRuns,
    StartingOcrQueue,
    ApplyingSyncedSettings
}
