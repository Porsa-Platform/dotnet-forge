using DotnetForge.Core;
using Spectre.Console;

namespace DotnetForge.Cli;

public sealed class ForgeCommands(
    IPackRegistry packRegistry,
    ConfigurationWorkflowService configurationWorkflowService,
    EffectiveConfigurationService effectiveConfigurationService,
    RunOrchestrator runOrchestrator,
    StopOrchestrator stopOrchestrator,
    HandoffService handoffService,
    HandoffFormatter handoffFormatter,
    HandoffDaemon handoffDaemon,
    IAnsiConsole console)
{
    [Command("packs list")]
    public async Task ListPacks(string workingDirectory = ".")
    {
        await ExecuteAsync(async () =>
        {
            var configuration = await configurationWorkflowService.LoadAsync(workingDirectory);
            var table = new Table().Border(TableBorder.Rounded).AddColumn("Pack").AddColumn("Enabled").AddColumn("Default").AddColumn("Flow").AddColumn("Description");
            foreach (var pack in packRegistry.GetAll())
            {
                table.AddRow(
                    pack.Id,
                    configuration.EnabledPacks.Contains(pack.Id, StringComparer.OrdinalIgnoreCase) ? "yes" : "no",
                    string.Equals(configuration.DefaultPack, pack.Id, StringComparison.OrdinalIgnoreCase) ? "yes" : "no",
                    pack.Flow,
                    pack.Description);
            }

            console.Write(table);
            return 0;
        });
    }

    [Command("packs enable")]
    public async Task EnablePack(string pack, string workingDirectory = ".")
    {
        await ExecuteAsync(async () =>
        {
            var configuration = await configurationWorkflowService.EnablePackAsync(workingDirectory, pack.Trim().ToLowerInvariant());
            console.MarkupLine($"[green]Enabled[/] [bold]{pack}[/]. Default pack: [bold]{configuration.DefaultPack}[/].");
            return 0;
        });
    }

    [Command("packs disable")]
    public async Task DisablePack(string pack, string workingDirectory = ".")
    {
        await ExecuteAsync(async () =>
        {
            var configuration = await configurationWorkflowService.DisablePackAsync(workingDirectory, pack.Trim().ToLowerInvariant());
            console.MarkupLine($"[yellow]Disabled[/] [bold]{pack}[/]. Default pack: [bold]{configuration.DefaultPack}[/].");
            return 0;
        });
    }

    [Command("config show")]
    public async Task ShowConfig(string workingDirectory = ".")
    {
        await ExecuteAsync(async () =>
        {
            var configuration = await configurationWorkflowService.LoadAsync(workingDirectory);
            var grid = new Grid();
            grid.AddColumn();
            grid.AddColumn();
            grid.AddRow("Working directory", Path.GetFullPath(workingDirectory));
            grid.AddRow("Enabled packs", string.Join(", ", configuration.EnabledPacks));
            grid.AddRow("Default pack", configuration.DefaultPack);
            grid.AddRow("Agent backend", configuration.AgentBackend);
            grid.AddRow("Terminal mode", configuration.TerminalMode);
            grid.AddRow("Prevent sleep", configuration.PreventSleep ? "yes" : "no");
            grid.AddRow("Agent start delay (ms)", configuration.AgentStartDelayMs.ToString());
            console.Write(new Panel(grid).Header("Effective project configuration"));
            return 0;
        });
    }

    [Command("run")]
    public async Task Run(string? pack = null, string workingDirectory = ".", string? agent = null, bool noAttach = false, bool dryRun = false)
    {
        await ExecuteAsync(async () =>
        {
            var summary = await runOrchestrator.RunAsync(new LaunchOptions(Path.GetFullPath(workingDirectory), pack, agent, !noAttach, dryRun));
            var config = summary.EffectiveConfiguration;
            var table = new Table().Border(TableBorder.Rounded).AddColumn("Role").AddColumn("Session").AddColumn("Worktree").AddColumn("Mode");
            foreach (var role in summary.RuntimeRoles)
            {
                table.AddRow(role.Role, role.Session, role.WorktreePath.Replace(config.WorkingDirectory, ".", StringComparison.Ordinal), role.ReceiveMode.ToString().ToLowerInvariant());
            }

            console.Write(new Panel($"Pack: [bold]{config.SelectedPack.Id}[/]\nAgent backend: [bold]{config.AgentBackend}[/]\nProject: [bold]{config.WorkingDirectory}[/]").Header(dryRun ? "Dry run" : "Run summary"));
            console.Write(table);
            if (dryRun)
            {
                console.Write(new Panel(string.Join(Environment.NewLine, summary.ExecutedCommands)).Header("Generated commands"));
            }
            else
            {
                console.MarkupLine("[green]dotnet-forge is ready.[/] Use [bold]close-swarm[/] or [bold]dotnet-forge stop[/] to stop it.");
            }

            return 0;
        });
    }

    [Command("stop")]
    public async Task Stop(string workingDirectory = ".")
    {
        await ExecuteAsync(() =>
        {
            stopOrchestrator.Stop(Path.GetFullPath(workingDirectory));
            console.MarkupLine("[yellow]Requested swarm shutdown.[/]");
            return Task.FromResult(0);
        });
    }

    [Command("handoff queue")]
    public async Task QueueHandoff(string draftFile, string workingDirectory = ".", string? role = null)
    {
        await ExecuteAsync(() =>
        {
            var result = handoffService.Queue(draftFile, Path.GetFullPath(workingDirectory), role);
            console.WriteLine($"HANDOFF QUEUED: {result.Path}");
            return Task.FromResult(0);
        });
    }

    [Command("handoff next")]
    public async Task ReadyForNext(string workingDirectory = ".", string? role = null)
    {
        await ExecuteAsync(() =>
        {
            var result = handoffService.ReadyForNext(Path.GetFullPath(workingDirectory), role);
            console.WriteLine(handoffFormatter.FormatSelection(result));
            return Task.FromResult(0);
        });
    }

    [Command("handoff done")]
    public async Task DoneWithCurrent(string workingDirectory = ".", string? role = null)
    {
        await ExecuteAsync(() =>
        {
            var result = handoffService.DoneWithCurrent(Path.GetFullPath(workingDirectory), role);
            console.WriteLine($"COMPLETED: {result.CompletedPath}");
            console.WriteLine(handoffFormatter.FormatSelection(result.NextSelection));
            return Task.FromResult(0);
        });
    }

    [Command("internal handoff-daemon")]
    public async Task RunHandoffDaemon(string workingDirectory = ".")
    {
        await ExecuteAsync(async () =>
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };
            await handoffDaemon.RunAsync(Path.GetFullPath(workingDirectory), cts.Token);
            return 0;
        });
    }

    private async Task ExecuteAsync(Func<Task<int>> action)
    {
        try
        {
            Environment.ExitCode = await action();
        }
        catch (ForgeException exception)
        {
            console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            Environment.ExitCode = exception.ExitCode;
        }
    }
}
