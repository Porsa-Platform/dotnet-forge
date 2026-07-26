using DotnetForge.Core;

namespace DotnetForge.Core.Tests;

public sealed class RunOrchestratorTests
{
    [Fact]
    public async Task Dry_run_generates_commands_from_reusable_core_library()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "swarmforge"));
        var registry = new DefaultPackRegistry();
        var store = new JsonProjectConfigurationStore(registry);
        await store.SaveAsync(root, new ProjectConfiguration { EnabledPacks = [PackIds.FourPack], DefaultPack = PackIds.FourPack });
        var orchestrator = new RunOrchestrator(
            new EffectiveConfigurationService(store, registry),
            new PackMaterializer(registry),
            new WorkspacePreparationService(new FakeCommandRunner()),
            new RuntimeStateStore(),
            new AgentLaunchCommandBuilder(),
            new FakeCommandRunner());

        var summary = await orchestrator.RunAsync(new LaunchOptions(root, null, "opencode", Attach: false, DryRun: true));

        Assert.Equal(PackIds.FourPack, summary.EffectiveConfiguration.SelectedPack.Id);
        Assert.Contains(summary.ExecutedCommands, static command => command.Contains("opencode", StringComparison.Ordinal));
        Assert.Contains(summary.RuntimeRoles, static role => role.Role == "architect" && role.ReceiveMode == ReceiveMode.Batch);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dotnet-forge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
