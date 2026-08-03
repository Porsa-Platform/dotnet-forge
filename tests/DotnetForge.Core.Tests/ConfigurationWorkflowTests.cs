using DotnetForge.Core;

namespace DotnetForge.Core.Tests;

public sealed class ConfigurationWorkflowTests
{
    [Fact]
    public async Task Disable_pack_reassigns_default_when_needed()
    {
        var root = CreateTempDirectory();
        var registry = new DefaultPackRegistry();
        var store = new JsonProjectConfigurationStore(registry);
        var workflow = new ConfigurationWorkflowService(store, registry);

        await store.SaveAsync(root, new ProjectConfiguration
        {
            EnabledPacks = [PackIds.TwoPack, PackIds.FourPack],
            DefaultPack = PackIds.TwoPack,
        });

        var updated = await workflow.DisablePackAsync(root, PackIds.TwoPack);

        Assert.Equal(PackIds.FourPack, updated.DefaultPack);
        Assert.Equal([PackIds.FourPack], updated.EnabledPacks);
    }

    [Fact]
    public async Task Effective_configuration_requires_enabled_pack()
    {
        var root = CreateTempDirectory();
        var registry = new DefaultPackRegistry();
        var store = new JsonProjectConfigurationStore(registry);
        await store.SaveAsync(root, new ProjectConfiguration { EnabledPacks = [PackIds.FourPack], DefaultPack = PackIds.FourPack });
        var service = new EffectiveConfigurationService(store, registry);

        var exception = await Assert.ThrowsAsync<ForgeException>(() => service.ResolveAsync(new LaunchOptions(root, PackIds.TwoPack, null, true, true)));

        Assert.Equal(2, exception.ExitCode);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dotnet-forge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
