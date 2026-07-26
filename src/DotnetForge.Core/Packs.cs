using System.Reflection;
using System.Text;

namespace DotnetForge.Core;

public interface IPackRegistry
{
    IReadOnlyCollection<PackDefinition> GetAll();
    PackDefinition Get(string id);
    bool Contains(string id);
    IReadOnlyList<PackAsset> GetSharedAssets();
    IReadOnlyList<PackAsset> GetPackAssets(string id);
}

public sealed class DefaultPackRegistry : IPackRegistry
{
    private const string ResourcePrefix = "DotnetForge.Asset|";
    private readonly IReadOnlyDictionary<string, PackDefinition> _packs;
    private readonly IReadOnlyList<PackAsset> _sharedAssets;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PackAsset>> _packAssets;

    public DefaultPackRegistry()
    {
        _packs = new Dictionary<string, PackDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [PackIds.TwoPack] = new(
                PackIds.TwoPack,
                "Two Pack",
                "Fast implementation and cleanup loop.",
                "coder -> cleaner -> coder",
                [
                    new RoleDefinition("coder", "codex", "master", ReceiveMode.Task),
                    new RoleDefinition("cleaner", "codex", "cleaner", ReceiveMode.Batch),
                ]),
            [PackIds.FourPack] = new(
                PackIds.FourPack,
                "Four Pack",
                "Compact spec-driven workflow.",
                "specifier -> coder -> refactorer -> architect -> specifier",
                [
                    new RoleDefinition("specifier", "codex", "master", ReceiveMode.Task),
                    new RoleDefinition("coder", "codex", "coder", ReceiveMode.Task),
                    new RoleDefinition("refactorer", "codex", "refactorer", ReceiveMode.Task),
                    new RoleDefinition("architect", "codex", "architect", ReceiveMode.Batch),
                ]),
            [PackIds.SixPack] = new(
                PackIds.SixPack,
                "Six Pack",
                "Full workflow with separated quality gates.",
                "specifier -> coder -> cleaner -> architect -> hardender -> QA",
                [
                    new RoleDefinition("specifier", "codex", "master", ReceiveMode.Task),
                    new RoleDefinition("coder", "codex", "coder", ReceiveMode.Task),
                    new RoleDefinition("cleaner", "codex", "cleaner", ReceiveMode.Batch),
                    new RoleDefinition("architect", "codex", "architect", ReceiveMode.Batch),
                    new RoleDefinition("hardender", "codex", "hardender", ReceiveMode.Batch),
                    new RoleDefinition("QA", "codex", "QA", ReceiveMode.Batch),
                ]),
        };

        (_sharedAssets, _packAssets) = LoadAssets();
    }

    public IReadOnlyCollection<PackDefinition> GetAll() => _packs.Values.OrderBy(static x => x.Id, StringComparer.Ordinal).ToArray();
    public PackDefinition Get(string id) => _packs.TryGetValue(id, out var pack)
        ? pack
        : throw new ForgeException($"Unknown pack '{id}'.", 2);
    public bool Contains(string id) => _packs.ContainsKey(id);
    public IReadOnlyList<PackAsset> GetSharedAssets() => _sharedAssets;
    public IReadOnlyList<PackAsset> GetPackAssets(string id) => _packAssets.TryGetValue(id, out var assets)
        ? assets
        : [];

    private static (IReadOnlyList<PackAsset>, IReadOnlyDictionary<string, IReadOnlyList<PackAsset>>) LoadAssets()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var allAssets = assembly
            .GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing resource {name}.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return new PackAsset(name[ResourcePrefix.Length..].Replace('\', '/'), reader.ReadToEnd());
            })
            .ToArray();

        var shared = allAssets
            .Where(static asset => asset.RelativePath.StartsWith("Shared/", StringComparison.Ordinal))
            .Select(static asset => asset with { RelativePath = asset.RelativePath["Shared/".Length..] })
            .OrderBy(static asset => asset.RelativePath, StringComparer.Ordinal)
            .ToArray();

        var packAssets = allAssets
            .Where(static asset => asset.RelativePath.StartsWith("Packs/", StringComparison.Ordinal))
            .GroupBy(static asset => asset.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[1], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<PackAsset>)group
                    .Select(asset => asset with { RelativePath = string.Join('/', asset.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(2)) })
                    .OrderBy(static asset => asset.RelativePath, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return (shared, packAssets);
    }
}
