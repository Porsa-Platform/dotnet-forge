using System.Diagnostics;
using System.Text;

namespace DotnetForge.Core;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface ICommandRunner
{
    CommandResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool throwOnError = true);
}

public interface IAgentNotifier
{
    void Notify(string session, string message);
}

public sealed class NullAgentNotifier : IAgentNotifier
{
    public static readonly NullAgentNotifier Instance = new();
    public void Notify(string session, string message) { }
}

public sealed class ProcessAgentNotifier(IReadOnlyDictionary<string, StreamWriter> stdinWriters) : IAgentNotifier
{
    public void Notify(string session, string message)
    {
        if (stdinWriters.TryGetValue(session, out var writer))
        {
            try
            {
                writer.WriteLine(message);
                writer.Flush();
            }
            catch
            {
                // Process may have exited
            }
        }
    }
}

/// <summary>
/// Sends notifications to tmux sessions via `tmux send-keys`.
/// Works identically to upstream swarm-forge's handoffd.bb notify!().
/// On Windows, tmux is routed through Git Bash (bash.exe).
/// </summary>
public sealed class TmuxAgentNotifier : IAgentNotifier
{
    private readonly string _socket;
    private readonly ICommandRunner _runner;

    public TmuxAgentNotifier(string tmuxSocket, ICommandRunner runner)
    {
        _socket = OperatingSystem.IsWindows()
            ? "/" + char.ToLowerInvariant(tmuxSocket[0]) + tmuxSocket[2..].Replace('\\', '/')
            : tmuxSocket;
        _runner = runner;
    }

    public void Notify(string session, string message)
    {
        try
        {
            var tmuxArgs = $"-S \"{_socket}\" send-keys -t \"{session}\" -l \"{message.Replace("\"", "\\\"")}\"";
            RunTmuxCmd(tmuxArgs);
            Thread.Sleep(150);
            RunTmuxCmd($"-S \"{_socket}\" send-keys -t \"{session}\" C-m");
            Thread.Sleep(50);
            RunTmuxCmd($"-S \"{_socket}\" send-keys -t \"{session}\" C-j");
        }
        catch
        {
            // tmux session may have exited
        }
    }

    private void RunTmuxCmd(string tmuxArgs)
    {
        if (OperatingSystem.IsWindows())
            _runner.Run("bash", ["-c", $"tmux {tmuxArgs}"], throwOnError: false);
        else
            _runner.Run("sh", ["-lc", $"tmux {tmuxArgs}"], throwOnError: false);
    }
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public CommandResult Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool throwOnError = true)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.ASCII,
            StandardErrorEncoding = Encoding.ASCII,
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
}

public static class Shell
{
    public static string Quote(string value) => $"'{value.Replace("'", "'\''", StringComparison.Ordinal)}'";

    public static string JoinArguments(IEnumerable<string> values) => string.Join(' ', values.Select(Quote));
}
