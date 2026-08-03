using System.Text;
using System.Text.RegularExpressions;

namespace DotnetForge.Core;

public sealed class ProjectLocator
{
    public string FindRoot(string workingDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "dotnet-forge")) || Directory.Exists(Path.Combine(directory.FullName, ".dotnet-forge")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new ForgeException("Unable to locate a dotnet-forge project root from the current directory.", 2);
    }
}

public sealed class HandoffFormatter
{
    public string FormatSelection(HandoffSelection? selection)
    {
        if (selection is null || selection.IsEmpty)
        {
            return "NO_TASK";
        }

        if (selection.ReceiveMode == ReceiveMode.Task)
        {
            var item = selection.Items[0];
            var lines = new List<string>
            {
                $"TASK: {item.Path}",
                $"FROM: {item.From}",
                $"TYPE: {item.Type}",
                $"PRIORITY: {item.Priority:00}",
            };

            if (!string.IsNullOrWhiteSpace(item.TaskName))
            {
                lines.Add($"TASK_NAME: {item.TaskName}");
            }

            lines.Add("PAYLOAD:");
            lines.Add(item.Payload);
            return string.Join(Environment.NewLine, lines);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"BATCH: {selection.Path}");
        builder.AppendLine($"COUNT: {selection.Items.Count}");
        builder.AppendLine($"PRIORITY: {selection.Items[0].Priority:00}");
        builder.AppendLine();

        for (var index = 0; index < selection.Items.Count; index++)
        {
            var item = selection.Items[index];
            builder.AppendLine($"BATCH_ITEM: {index + 1}");
            builder.AppendLine($"TASK: {item.Path}");
            builder.AppendLine($"FROM: {item.From}");
            builder.AppendLine($"TYPE: {item.Type}");
            builder.AppendLine($"PRIORITY: {item.Priority:00}");
            if (!string.IsNullOrWhiteSpace(item.TaskName))
            {
                builder.AppendLine($"TASK_NAME: {item.TaskName}");
            }
            builder.AppendLine("PAYLOAD:");
            builder.AppendLine(item.Payload);
            if (index < selection.Items.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed class HandoffService(ProjectLocator projectLocator, RuntimeStateStore runtimeStateStore, ICommandRunner commandRunner)
{
    private static readonly HashSet<string> ReservedHeaders =
    [
        "id", "from", "role", "recipient", "created_at", "enqueued_at", "dequeued_at", "completed_at"
    ];

    private const string WakeMessage = "You have new handoff mail. If idle, run ready_for_next.sh.";

    public HandoffQueueResult Queue(string draftFile, string workingDirectory, string? roleOverride = null)
    {
        var root = projectLocator.FindRoot(workingDirectory);
        var runtimeRoles = runtimeStateStore.LoadRoles(root);
        var role = ResolveRole(runtimeRoles, roleOverride);
        var draft = ParseDraft(File.ReadAllText(draftFile, Encoding.UTF8), root, role.Role);
        var handoffDirectory = Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs");
        var fileName = BuildFileName(draft.Headers["priority"], role.Role, draft.Headers["to"], handoffDirectory);
        var path = Path.Combine(handoffDirectory, "outbox", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, draft.Render(), Encoding.UTF8);
        return new HandoffQueueResult(path);
    }

    public void PumpOnce(string workingDirectory, IAgentNotifier notifier)
    {
        var root = projectLocator.FindRoot(workingDirectory);
        var runtimeRoles = runtimeStateStore.LoadRoles(root).ToDictionary(static role => role.Role, StringComparer.OrdinalIgnoreCase);
        var paths = ProjectPaths.For(root);

        foreach (var sender in runtimeRoles.Values)
        {
            var outbox = Path.Combine(sender.WorktreePath, ".dotnet-forge", "handoffs", "outbox");
            if (!Directory.Exists(outbox))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(outbox, "*.handoff").OrderBy(static x => x, StringComparer.Ordinal))
            {
                var message = ParseMessage(File.ReadAllText(file, Encoding.UTF8));
                if (!message.Headers.TryGetValue("to", out var to))
                {
                    MoveWithCollision(file, Path.Combine(sender.WorktreePath, ".dotnet-forge", "handoffs", "failed"));
                    continue;
                }

                foreach (var recipient in to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!runtimeRoles.TryGetValue(recipient, out var targetRole))
                    {
                        throw new ForgeException($"Unknown recipient '{recipient}'.", 2);
                    }

                    var targetPath = Path.Combine(targetRole.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "new", Path.GetFileName(file));
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    var delivered = new Dictionary<string, string>(message.Headers, StringComparer.OrdinalIgnoreCase)
                    {
                        ["recipient"] = recipient,
                        ["enqueued_at"] = DateTimeOffset.UtcNow.ToString("O"),
                    };
                    File.WriteAllText(targetPath, new HandoffMessage(delivered, message.Body).Render(), Encoding.UTF8);
                    notifier.Notify(targetRole.Session, WakeMessage);
                }

                MoveWithCollision(file, Path.Combine(sender.WorktreePath, ".dotnet-forge", "handoffs", "sent"));
            }
        }
    }

    public HandoffSelection ReadyForNext(string workingDirectory, string? roleOverride = null)
    {
        var root = projectLocator.FindRoot(workingDirectory);
        var runtimeRoles = runtimeStateStore.LoadRoles(root);
        var role = ResolveRole(runtimeRoles, roleOverride);
        return role.ReceiveMode == ReceiveMode.Batch ? ReadyBatch(role) : ReadyTask(role);
    }

    public HandoffCompletionResult DoneWithCurrent(string workingDirectory, string? roleOverride = null)
    {
        var root = projectLocator.FindRoot(workingDirectory);
        var runtimeRoles = runtimeStateStore.LoadRoles(root);
        var role = ResolveRole(runtimeRoles, roleOverride);
        return role.ReceiveMode == ReceiveMode.Batch ? CompleteBatch(role) : CompleteTask(role);
    }

    private HandoffMessage ParseDraft(string content, string root, string senderRole)
    {
        var message = ParseMessage(content);
        var errors = new List<string>();

        foreach (var header in message.Headers.Keys)
        {
            if (ReservedHeaders.Contains(header))
            {
                errors.Add($"Header '{header}' is reserved and must not be written by agents.");
            }
        }

        if (!message.Headers.TryGetValue("type", out var type) || (type is not "git_handoff" and not "note"))
        {
            errors.Add("Header 'type' must be either git_handoff or note.");
        }

        if (!message.Headers.TryGetValue("to", out var to) || string.IsNullOrWhiteSpace(to))
        {
            errors.Add("Header 'to' is required.");
        }

        if (!message.Headers.TryGetValue("priority", out var priority) || !Regex.IsMatch(priority, "^[0-9]{2}$"))
        {
            errors.Add("Header 'priority' must be a two-digit number.");
        }

        if (type == "git_handoff")
        {
            if (!message.Headers.TryGetValue("task", out var task) || string.IsNullOrWhiteSpace(task))
            {
                errors.Add("Header 'task' is required for git_handoff.");
            }

            if (!message.Headers.TryGetValue("commit", out var commit) || !Regex.IsMatch(commit, "^[0-9a-fA-F]{10}$"))
            {
                errors.Add("Header 'commit' must be a 10-character hexadecimal commit abbreviation.");
            }
            else
            {
                var result = commandRunner.Run("git", ["-C", root, "rev-parse", "--verify", $"{commit}^{{commit}}"], throwOnError: false);
                if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    errors.Add($"Commit '{commit}' could not be resolved.");
                }
                else
                {
                    message = new HandoffMessage(new Dictionary<string, string>(message.Headers, StringComparer.OrdinalIgnoreCase)
                    {
                        ["commit"] = result.StandardOutput[..10].ToLowerInvariant(),
                    }, message.Body);
                }
            }
        }

        if (type == "note" && (!message.Headers.TryGetValue("message", out var note) || string.IsNullOrWhiteSpace(note)))
        {
            errors.Add("Header 'message' is required for note.");
        }

        if (errors.Count > 0)
        {
            throw new ForgeException($"HANDOFF INVALID{Environment.NewLine}{Environment.NewLine}Errors:{Environment.NewLine}- {string.Join(Environment.NewLine + "- ", errors)}", 2);
        }

        var headers = new Dictionary<string, string>(message.Headers, StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}_{NextSequence(Path.Combine(root, ".dotnet-forge", "handoffs-sequence")):000000}_from_{senderRole}",
            ["from"] = senderRole,
            ["role"] = senderRole,
            ["created_at"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        var body = type == "git_handoff"
            ? $"Re-read your role and constitution.{Environment.NewLine}{Environment.NewLine}merge_and_process {senderRole} {headers["commit"]}"
            : $"Re-read your role and constitution.{Environment.NewLine}{Environment.NewLine}{headers["message"]}";

        return new HandoffMessage(headers, body);
    }

    private static HandoffMessage ParseMessage(string content)
    {
        var parts = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", 2, StringSplitOptions.None);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in parts[0].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = line.IndexOf(": ", StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            headers[line[..index]] = line[(index + 2)..];
        }

        return new HandoffMessage(headers, parts.Length > 1 ? parts[1] : string.Empty);
    }

    private static string BuildFileName(string priority, string senderRole, string recipients, string handoffDirectory)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var sequence = NextSequence(Path.Combine(handoffDirectory, "sequence"));
        var recipientSuffix = string.Join('_', recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return $"{priority}_{timestamp}_{sequence:000000}_from_{senderRole}_to_{recipientSuffix}.handoff";
    }

    private static int NextSequence(string sequenceFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sequenceFile)!);
        var lockFile = sequenceFile + ".lock";
        using var stream = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var useFileLock = !OperatingSystem.IsMacOS();
        if (useFileLock)
        {
            stream.Lock(0, 0);
        }

        try
        {
            var current = File.Exists(sequenceFile) && int.TryParse(File.ReadAllText(sequenceFile, Encoding.UTF8), out var parsed) ? parsed : 0;
            current++;
            File.WriteAllText(sequenceFile, current.ToString(), Encoding.UTF8);
            return current;
        }
        finally
        {
            if (useFileLock)
            {
                stream.Unlock(0, 0);
            }
        }
    }

    private static RuntimeRole ResolveRole(IReadOnlyList<RuntimeRole> runtimeRoles, string? roleOverride)
    {
        var roleName = string.IsNullOrWhiteSpace(roleOverride)
            ? Environment.GetEnvironmentVariable("DOTNET_FORGE_ROLE")
            : roleOverride;

        if (string.IsNullOrWhiteSpace(roleName))
        {
            throw new ForgeException("No role was supplied and DOTNET_FORGE_ROLE is not set.", 2);
        }

        return runtimeRoles.FirstOrDefault(role => string.Equals(role.Role, roleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ForgeException($"Unknown runtime role '{roleName}'.", 2);
    }

    private static HandoffSelection ReadyTask(RuntimeRole role)
    {
        var inProcess = Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "in_process");
        var existing = Directory.GetFileSystemEntries(inProcess);
        if (existing.Length > 1)
        {
            throw new ForgeException("AMBIGUOUS_TASK_STATE: multiple tasks are already in process.", 2);
        }

        if (existing.Length == 1)
        {
            if (Directory.Exists(existing[0]))
            {
                throw new ForgeException("TASK_IN_PROCESS_IS_BATCH: use ready_for_next.sh or done_with_current.sh.", 2);
            }

            return BuildTaskSelection(existing[0]);
        }

        var next = Directory.GetFiles(Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "new"), "*.handoff")
            .OrderBy(static x => x, StringComparer.Ordinal)
            .FirstOrDefault();
        if (next is null)
        {
            return new HandoffSelection(ReceiveMode.Task, inProcess, []);
        }

        var target = Path.Combine(inProcess, Path.GetFileName(next));
        File.Move(next, target);
        StampHeader(target, "dequeued_at", DateTimeOffset.UtcNow.ToString("O"));
        return BuildTaskSelection(target);
    }

    private static HandoffSelection ReadyBatch(RuntimeRole role)
    {
        var inProcess = Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "in_process");
        var existingDirectories = Directory.GetDirectories(inProcess);
        if (existingDirectories.Length > 1 || Directory.GetFiles(inProcess, "*.handoff").Length > 0)
        {
            throw new ForgeException("AMBIGUOUS_TASK_STATE: multiple tasks are already in process.", 2);
        }

        if (existingDirectories.Length == 1)
        {
            return BuildBatchSelection(existingDirectories[0]);
        }

        var newFiles = Directory.GetFiles(Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "new"), "*.handoff")
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToArray();
        if (newFiles.Length == 0)
        {
            return new HandoffSelection(ReceiveMode.Batch, inProcess, []);
        }

        var firstPriority = ParseMessage(File.ReadAllText(newFiles[0], Encoding.UTF8)).Headers["priority"];
        var batchSuffix = Guid.NewGuid().ToString("N")[..8];
        var batchDirectory = Path.Combine(inProcess, $"batch_{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}_{batchSuffix}");
        Directory.CreateDirectory(batchDirectory);
        foreach (var file in newFiles.Where(file => ParseMessage(File.ReadAllText(file, Encoding.UTF8)).Headers["priority"] == firstPriority))
        {
            var destination = Path.Combine(batchDirectory, Path.GetFileName(file));
            File.Move(file, destination);
            StampHeader(destination, "dequeued_at", DateTimeOffset.UtcNow.ToString("O"));
        }

        return BuildBatchSelection(batchDirectory);
    }

    private static HandoffCompletionResult CompleteTask(RuntimeRole role)
    {
        var inProcess = Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "in_process");
        var existing = Directory.GetFileSystemEntries(inProcess);
        if (existing.Length == 0)
        {
            throw new ForgeException("NO_CURRENT_TASK", 1);
        }

        if (existing.Length > 1)
        {
            throw new ForgeException("AMBIGUOUS_TASK_STATE: multiple tasks are in process.", 2);
        }

        if (Directory.Exists(existing[0]))
        {
            throw new ForgeException("CURRENT_WORK_IS_BATCH: use done_with_current.sh.", 2);
        }

        StampHeader(existing[0], "completed_at", DateTimeOffset.UtcNow.ToString("O"));
        var completedDirectory = Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "completed");
        Directory.CreateDirectory(completedDirectory);
        var completedPath = Path.Combine(completedDirectory, Path.GetFileName(existing[0]));
        if (File.Exists(completedPath))
        {
            File.Delete(completedPath);
        }
        File.Move(existing[0], completedPath);
        var nextSelection = ReadyTask(role);
        return new HandoffCompletionResult(completedPath, nextSelection.IsEmpty ? null : nextSelection);
    }

    private static HandoffCompletionResult CompleteBatch(RuntimeRole role)
    {
        var inProcess = Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "in_process");
        var directories = Directory.GetDirectories(inProcess);
        if (directories.Length == 0)
        {
            throw new ForgeException("NO_CURRENT_TASK", 1);
        }

        if (directories.Length > 1)
        {
            throw new ForgeException("AMBIGUOUS_TASK_STATE: multiple tasks are in process.", 2);
        }

        foreach (var file in Directory.GetFiles(directories[0], "*.handoff"))
        {
            StampHeader(file, "completed_at", DateTimeOffset.UtcNow.ToString("O"));
        }

        var completedDirectory = Path.Combine(role.WorktreePath, ".dotnet-forge", "handoffs", "inbox", "completed");
        Directory.CreateDirectory(completedDirectory);
        var destination = Path.Combine(completedDirectory, Path.GetFileName(directories[0]));
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, true);
        }
        Directory.Move(directories[0], destination);
        var nextSelection = ReadyBatch(role);
        return new HandoffCompletionResult(destination, nextSelection.IsEmpty ? null : nextSelection);
    }

    private static HandoffSelection BuildTaskSelection(string path)
    {
        var message = ParseMessage(File.ReadAllText(path, Encoding.UTF8));
        return new HandoffSelection(ReceiveMode.Task, path, [BuildSummary(path, message)]);
    }

    private static HandoffSelection BuildBatchSelection(string directory)
    {
        var items = Directory.GetFiles(directory, "*.handoff")
            .OrderBy(static x => x, StringComparer.Ordinal)
            .Select(path => BuildSummary(path, ParseMessage(File.ReadAllText(path, Encoding.UTF8))))
            .ToArray();
        return new HandoffSelection(ReceiveMode.Batch, directory, items);
    }

    private static HandoffSummary BuildSummary(string path, HandoffMessage message)
        => new(
            path,
            message.Headers.GetValueOrDefault("from") ?? string.Empty,
            message.Headers.GetValueOrDefault("type") ?? string.Empty,
            int.TryParse(message.Headers.GetValueOrDefault("priority"), out var priority) ? priority : 0,
            message.Headers.GetValueOrDefault("task"),
            message.Body);

    private static void StampHeader(string path, string key, string value)
    {
        var message = ParseMessage(File.ReadAllText(path, Encoding.UTF8));
        var updated = new Dictionary<string, string>(message.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [key] = value,
        };
        File.WriteAllText(path, new HandoffMessage(updated, message.Body).Render(), Encoding.UTF8);
    }

    private static void MoveWithCollision(string source, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        if (File.Exists(destination))
        {
            destination = Path.Combine(destinationDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}_{Path.GetFileName(source)}");
        }

        File.Move(source, destination);
    }
}

public sealed class HandoffDaemon(HandoffService handoffService)
{
    public async Task RunAsync(string workingDirectory, CancellationToken cancellationToken, IAgentNotifier? notifier = null)
    {
        notifier ??= NullAgentNotifier.Instance;
        var root = new ProjectLocator().FindRoot(workingDirectory);
        var stopFile = ProjectPaths.For(root).StopFile;
        Directory.CreateDirectory(Path.GetDirectoryName(stopFile)!);
        while (!cancellationToken.IsCancellationRequested && !File.Exists(stopFile))
        {
            handoffService.PumpOnce(root, notifier);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }
}
