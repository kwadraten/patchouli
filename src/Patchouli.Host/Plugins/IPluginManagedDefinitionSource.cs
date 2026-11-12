namespace Patchouli.Host.Plugins;

public interface IPluginManagedDefinitionSource
{
    IReadOnlyList<PluginDefinition> ManagedFileDefinitions();
}
