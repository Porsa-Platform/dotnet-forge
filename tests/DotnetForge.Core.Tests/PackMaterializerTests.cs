using DotnetForge.Core;

namespace DotnetForge.Core.Tests;

public sealed class PackMaterializerTests
{
    [Fact]
    public void Materialize_writes_pack_specific_files_and_wrappers()
    {
        var root = CreateTempDirectory();
        var registry = new DefaultPackRegistry();
        var materializer = new PackMaterializer(registry);
        var configuration = new EffectiveConfiguration(
            root,
            new ProjectConfiguration(),
            registry.Get(PackIds.TwoPack),
            "opencode",
            Attach: false,
            DryRun: true);

        materializer.Materialize(configuration);

        Assert.True(File.Exists(Path.Combine(root, "swarmforge", "roles", "coder.prompt")));
        Assert.True(File.Exists(Path.Combine(root, "swarmforge", "scripts", "ready_for_next.sh")));
        var configText = File.ReadAllText(Path.Combine(root, "swarmforge", "swarmforge.conf"));
        Assert.Contains("window cleaner opencode cleaner batch", configText, StringComparison.Ordinal);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dotnet-forge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
