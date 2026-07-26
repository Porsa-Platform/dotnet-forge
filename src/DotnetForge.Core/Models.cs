using System.Security.Cryptography;
using System.Text;

namespace DotnetForge.Core;

public enum ReceiveMode
{
    Task,
    Batch,
}

public static class AgentBackends
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "codex",
        "claude",
        "opencode",
        "copilot",
        "grok",
    };

    public static string Normalize(string? backend)
    {
        var value = string.IsNullOrWhiteSpace(backend) ? "codex" : backend.Trim().ToLowerInvariant();
        if (!Supported.Contains(value))
        {
            throw new ForgeException($"Unsupported agent backend '{value}'. Supported backends: codex, claude, opencode, copilot, grok.", 2);
        }

        return value;
    }
}

public static class PackIds
{
    public const string TwoPack = "two-pack";
    public const string FourPack = "four-pack";
    public const string SixPack = "six-pack";
}

public sealed record RoleDefinition(
    string Name,
    string DefaultAgent,
    string Worktree,
    ReceiveMode ReceiveMode,
    IReadOnlyList<string>? ExtraArgs = null);

public sealed record PackDefinition(
    string Id,
    string DisplayName,
    string Description,
    string Flow,
    IReadOnlyList<RoleDefinition> Roles);

public sealed record ProjectConfiguration
{
    public string SchemaVersion { get; init; } = "1.0";
    public List<string> EnabledPacks { get; init; } = [PackIds.FourPack, PackIds.TwoPack, PackIds.SixPack];
    public string DefaultPack { get; init; } = PackIds.FourPack;
    public string AgentBackend { get; init; } = "codex";
    public string TerminalMode { get; init; } = "none";
    public bool PreventSleep { get; init; }
    public int AgentStartDelayMs { get; init; } = 1500;

    public ProjectConfiguration Normalize(IPackRegistry registry)
    {
        var enabled = (EnabledPacks ?? [])
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(registry.Contains)
            .ToList();

        if (enabled.Count == 0)
        {
            enabled.Add(PackIds.FourPack);
        }

        var defaultPack = !string.IsNullOrWhiteSpace(DefaultPack)
            ? DefaultPack.Trim().ToLowerInvariant()
            : enabled[0];

        if (!enabled.Contains(defaultPack, StringComparer.OrdinalIgnoreCase))
        {
            defaultPack = enabled[0];
        }

        var backend = AgentBackends.Normalize(AgentBackend);
        var terminal = string.IsNullOrWhiteSpace(TerminalMode) ? "none" : TerminalMode.Trim().ToLowerInvariant();

        return this with
        {
            EnabledPacks = enabled,
            DefaultPack = defaultPack,
            AgentBackend = backend,
            TerminalMode = terminal,
            AgentStartDelayMs = AgentStartDelayMs <= 0 ? 1500 : AgentStartDelayMs,
        };
    }
}

public sealed record EffectiveConfiguration(
    string WorkingDirectory,
    ProjectConfiguration ProjectConfiguration,
    PackDefinition SelectedPack,
    string AgentBackend,
    bool Attach,
    bool DryRun);

public sealed record LaunchOptions(
    string WorkingDirectory,
    string? Pack,
    string? AgentBackend,
    bool Attach,
    bool DryRun);

public sealed record RuntimeRole(
    string Role,
    string WorktreeName,
    string WorktreePath,
    string Session,
    string DisplayName,
    string Agent,
    ReceiveMode ReceiveMode);

public sealed record RunSummary(
    EffectiveConfiguration EffectiveConfiguration,
    ProjectPaths Paths,
    IReadOnlyList<RuntimeRole> RuntimeRoles,
    IReadOnlyList<string> ExecutedCommands);

public sealed record PackAsset(string RelativePath, string Content);

public sealed record HandoffMessage(IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string Render()
    {
        var builder = new StringBuilder();
        foreach (var header in Headers)
        {
            builder.Append(header.Key).Append(": ").Append(header.Value).AppendLine();
        }

        builder.AppendLine();
        builder.Append(Body);
        return builder.ToString();
    }
}

public sealed record HandoffSummary(string Path, string From, string Type, int Priority, string? TaskName, string Payload);
public sealed record HandoffSelection(ReceiveMode ReceiveMode, string Path, IReadOnlyList<HandoffSummary> Items)
{
    public bool IsEmpty => Items.Count == 0;
}

public sealed record HandoffQueueResult(string Path);
public sealed record HandoffCompletionResult(string CompletedPath, HandoffSelection? NextSelection);

public sealed class ForgeException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public sealed class ProjectPaths
{
    private ProjectPaths(string workingDirectory)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        SwarmforgeDirectory = Path.Combine(WorkingDirectory, "swarmforge");
        ConfigurationFile = Path.Combine(SwarmforgeDirectory, "dotnet-forge.json");
        StateDirectory = Path.Combine(WorkingDirectory, ".swarmforge");
        WorktreesDirectory = Path.Combine(WorkingDirectory, ".worktrees");
        PromptsDirectory = Path.Combine(StateDirectory, "prompts");
        RolesFile = Path.Combine(StateDirectory, "roles.tsv");
        SessionsFile = Path.Combine(StateDirectory, "sessions.tsv");
        StopFile = Path.Combine(StateDirectory, "daemon", "stop");
        DaemonLogFile = Path.Combine(StateDirectory, "daemon", "handoffd.log");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WorkingDirectory))).ToLowerInvariant()[..12];
        TmuxSocket = Path.Combine(Path.GetTempPath(), $"dotnet-forge-{hash}.sock");
    }

    public string WorkingDirectory { get; }
    public string SwarmforgeDirectory { get; }
    public string ConfigurationFile { get; }
    public string StateDirectory { get; }
    public string WorktreesDirectory { get; }
    public string PromptsDirectory { get; }
    public string RolesFile { get; }
    public string SessionsFile { get; }
    public string StopFile { get; }
    public string DaemonLogFile { get; }
    public string TmuxSocket { get; }

    public static ProjectPaths For(string workingDirectory) => new(workingDirectory);

    public string WorktreePath(string worktreeName) => worktreeName is "master" or "none"
        ? WorkingDirectory
        : Path.Combine(WorktreesDirectory, worktreeName);
}
