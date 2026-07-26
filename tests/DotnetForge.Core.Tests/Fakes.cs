using DotnetForge.Core;

namespace DotnetForge.Core.Tests;

internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<string> Invocations { get; } = [];
    public Dictionary<string, CommandResult> Results { get; } = new(StringComparer.Ordinal);

    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool throwOnError = true)
    {
        var key = BuildKey(fileName, arguments);
        Invocations.Add(key);
        if (Results.TryGetValue(key, out var result))
        {
            if (throwOnError && result.ExitCode != 0)
            {
                throw new ForgeException(result.StandardError.Length == 0 ? key : result.StandardError);
            }

            return result;
        }

        return new CommandResult(0, string.Empty, string.Empty);
    }

    private static string BuildKey(string fileName, IReadOnlyList<string> arguments) => fileName + " " + string.Join(' ', arguments);
}
