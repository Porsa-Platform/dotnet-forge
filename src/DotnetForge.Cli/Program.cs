using ConsoleAppFramework;
using DotnetForge.Cli;
using DotnetForge.Core;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

var app = ConsoleApp.Create()
    .ConfigureServices(services =>
    {
        services.AddSingleton<IPackRegistry, DefaultPackRegistry>();
        services.AddSingleton<IProjectConfigurationStore, JsonProjectConfigurationStore>();
        services.AddSingleton<ConfigurationWorkflowService>();
        services.AddSingleton<EffectiveConfigurationService>();
        services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        services.AddSingleton<PackMaterializer>();
        services.AddSingleton<RuntimeStateStore>();
        services.AddSingleton<WorkspacePreparationService>();
        services.AddSingleton<AgentLaunchCommandBuilder>();
        services.AddSingleton<HandoffDaemon>();
        services.AddSingleton<RunOrchestrator>();
        services.AddSingleton<StopOrchestrator>();
        services.AddSingleton<ProjectLocator>();
        services.AddSingleton<HandoffFormatter>();
        services.AddSingleton<HandoffService>();
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);
    });

app.Add<ForgeCommands>();
app.Run(args);
