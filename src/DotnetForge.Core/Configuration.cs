using System.Text.Json;

namespace DotnetForge.Core;

public interface IProjectConfigurationStore
{
    Task<ProjectConfiguration> LoadAsync(string workingDirectory, CancellationToken cancellationToken = default);
    Task<ProjectConfiguration> SaveAsync(string workingDirectory, ProjectConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class JsonProjectConfigurationStore(IPackRegistry packRegistry) : IProjectConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ProjectConfiguration> LoadAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var paths = ProjectPaths.For(workingDirectory);
        if (!File.Exists(paths.ConfigurationFile))
        {
            return new ProjectConfiguration().Normalize(packRegistry);
        }

        await using var stream = File.OpenRead(paths.ConfigurationFile);
        var configuration = await JsonSerializer.DeserializeAsync<ProjectConfiguration>(stream, JsonOptions, cancellationToken)
            ?? new ProjectConfiguration();
        return configuration.Normalize(packRegistry);
    }

    public async Task<ProjectConfiguration> SaveAsync(string workingDirectory, ProjectConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var paths = ProjectPaths.For(workingDirectory);
        Directory.CreateDirectory(paths.DotnetForgeDirectory);
        var normalized = configuration.Normalize(packRegistry);
        await using var stream = File.Create(paths.ConfigurationFile);
        await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
        return normalized;
    }
}

public sealed class ConfigurationWorkflowService(IProjectConfigurationStore store, IPackRegistry packRegistry)
{
    public Task<ProjectConfiguration> LoadAsync(string workingDirectory, CancellationToken cancellationToken = default)
        => store.LoadAsync(workingDirectory, cancellationToken);

    public async Task<ProjectConfiguration> EnablePackAsync(string workingDirectory, string packId, CancellationToken cancellationToken = default)
    {
        EnsurePack(packId);
        var configuration = await store.LoadAsync(workingDirectory, cancellationToken);
        var enabled = configuration.EnabledPacks.ToHashSet(StringComparer.OrdinalIgnoreCase);
        enabled.Add(packId);
        var updated = configuration with
        {
            EnabledPacks = enabled.OrderBy(static x => x, StringComparer.Ordinal).ToList(),
            DefaultPack = enabled.Contains(configuration.DefaultPack, StringComparer.OrdinalIgnoreCase) ? configuration.DefaultPack : packId,
        };
        return await store.SaveAsync(workingDirectory, updated, cancellationToken);
    }

    public async Task<ProjectConfiguration> DisablePackAsync(string workingDirectory, string packId, CancellationToken cancellationToken = default)
    {
        EnsurePack(packId);
        var configuration = await store.LoadAsync(workingDirectory, cancellationToken);
        var enabled = configuration.EnabledPacks
            .Where(id => !string.Equals(id, packId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();

        if (enabled.Count == 0)
        {
            throw new ForgeException("At least one pack must remain enabled.", 2);
        }

        var updated = configuration with
        {
            EnabledPacks = enabled,
            DefaultPack = string.Equals(configuration.DefaultPack, packId, StringComparison.OrdinalIgnoreCase)
                ? enabled[0]
                : configuration.DefaultPack,
        };
        return await store.SaveAsync(workingDirectory, updated, cancellationToken);
    }

    private void EnsurePack(string packId)
    {
        if (!packRegistry.Contains(packId))
        {
            throw new ForgeException($"Unknown pack '{packId}'.", 2);
        }
    }
}

public sealed class EffectiveConfigurationService(IProjectConfigurationStore store, IPackRegistry packRegistry)
{
    public async Task<EffectiveConfiguration> ResolveAsync(LaunchOptions options, CancellationToken cancellationToken = default)
    {
        var configuration = await store.LoadAsync(options.WorkingDirectory, cancellationToken);
        var selectedPackId = string.IsNullOrWhiteSpace(options.Pack)
            ? configuration.DefaultPack
            : options.Pack.Trim().ToLowerInvariant();

        if (!configuration.EnabledPacks.Contains(selectedPackId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ForgeException($"Pack '{selectedPackId}' is not enabled for this project. Run 'dotnet-forge packs enable {selectedPackId}'.", 2);
        }

        var pack = packRegistry.Get(selectedPackId);
        var backend = AgentBackends.Normalize(string.IsNullOrWhiteSpace(options.AgentBackend)
            ? configuration.AgentBackend
            : options.AgentBackend);

        return new EffectiveConfiguration(
            Path.GetFullPath(options.WorkingDirectory),
            configuration,
            pack,
            backend,
            options.Attach,
            options.DryRun);
    }
}
