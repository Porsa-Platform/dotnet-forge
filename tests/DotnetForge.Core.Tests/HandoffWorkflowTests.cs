using DotnetForge.Core;
using System.Text;

namespace DotnetForge.Core.Tests;

public sealed class HandoffWorkflowTests
{
    [Fact]
    public void Ready_and_done_flow_round_trips_task_handoffs()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "swarmforge"));
        var runtimeStateStore = new RuntimeStateStore();
        var effectiveConfiguration = new EffectiveConfiguration(
            root,
            new ProjectConfiguration(),
            new DefaultPackRegistry().Get(PackIds.TwoPack),
            "codex",
            Attach: false,
            DryRun: true);
        runtimeStateStore.Write(effectiveConfiguration);

        var coderInbox = Path.Combine(root, ".swarmforge", "handoffs", "inbox", "new");
        Directory.CreateDirectory(coderInbox);
        var handoffPath = Path.Combine(coderInbox, "00_20260726T000000Z_000001_from_cleaner_to_coder.handoff");
        File.WriteAllText(handoffPath, string.Join(Environment.NewLine,
            "from: cleaner",
            "to: coder",
            "priority: 00",
            "type: git_handoff",
            "task: implement-core",
            string.Empty,
            "Re-read your role and constitution.",
            string.Empty,
            "merge_and_process cleaner abcdef1234"), Encoding.UTF8);

        var service = new HandoffService(new ProjectLocator(), runtimeStateStore, new FakeCommandRunner());
        var selection = service.ReadyForNext(root, "coder");
        var completion = service.DoneWithCurrent(root, "coder");

        Assert.False(selection.IsEmpty);
        Assert.Equal("implement-core", selection.Items[0].TaskName);
        Assert.NotNull(completion.CompletedPath);
        Assert.Null(completion.NextSelection);
    }

    [Fact]
    public void Queue_validates_commit_resolution()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "swarmforge"));
        var runtimeStateStore = new RuntimeStateStore();
        runtimeStateStore.Write(new EffectiveConfiguration(
            root,
            new ProjectConfiguration(),
            new DefaultPackRegistry().Get(PackIds.TwoPack),
            "codex",
            Attach: false,
            DryRun: true));

        var draftPath = Path.Combine(root, "draft.txt");
        File.WriteAllText(draftPath, string.Join(Environment.NewLine,
            "type: git_handoff",
            "to: cleaner",
            "priority: 50",
            "task: test-task",
            "commit: abcdef1234"), Encoding.UTF8);

        var runner = new FakeCommandRunner();
        runner.Results["git -C " + root + " rev-parse --verify abcdef1234^{commit}"] = new CommandResult(0, "abcdef1234567890", string.Empty);
        var service = new HandoffService(new ProjectLocator(), runtimeStateStore, runner);

        var result = service.Queue(draftPath, root, "coder");

        Assert.True(File.Exists(result.Path));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dotnet-forge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
