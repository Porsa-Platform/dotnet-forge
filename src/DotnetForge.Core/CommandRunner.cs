using System.Diagnostics;
using System.Text;

namespace DotnetForge.Core;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface ICommandRunner
{
    CommandResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool throwOnError = true);
    void StartDetached(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool throwOnError = true)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Unable to start process '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var result = new CommandResult(process.ExitCode, stdout.Trim(), stderr.Trim());
        if (throwOnError && result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"Command failed: {fileName} {string.Join(' ', arguments)}"
                : result.StandardError;
            throw new ForgeException(message);
        }

        return result;
    }

    public void StartDetached(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process.Start(startInfo);
    }
}

public static class Shell
{
    public static string Quote(string value) => $"'{value.Replace("'", "'\''", StringComparison.Ordinal)}'";

    public static string JoinArguments(IEnumerable<string> values) => string.Join(' ', values.Select(Quote));
}
