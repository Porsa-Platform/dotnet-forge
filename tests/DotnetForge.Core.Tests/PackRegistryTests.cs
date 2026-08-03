using DotnetForge.Core;

namespace DotnetForge.Core.Tests;

public sealed class PackRegistryTests
{
    [Fact]
    public void Registry_contains_all_runtime_toggle_packs()
    {
        var registry = new DefaultPackRegistry();

        var packs = registry.GetAll().Select(static pack => pack.Id).ToArray();

        Assert.Equal([PackIds.FourPack, PackIds.SixPack, PackIds.TwoPack], packs.OrderBy(static x => x, StringComparer.Ordinal).ToArray());
        Assert.Contains(registry.GetSharedAssets(), static asset => asset.RelativePath.EndsWith("engineering.prompt", StringComparison.Ordinal));
        Assert.Contains(registry.GetPackAssets(PackIds.FourPack), static asset => asset.RelativePath.EndsWith("specifier.prompt", StringComparison.Ordinal));
    }
}
