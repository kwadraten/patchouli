namespace Patchouli.Host.Composition;

public enum StartupStage
{
    ValidatingPaths,
    ComposingServices,
    ApplyingMigrations,
    PreparingSearchCache,
    AdoptingRootBindings,
    ReconcilingOcrRuns,
    StartingOcrQueue,
    ApplyingSyncedSettings
}
