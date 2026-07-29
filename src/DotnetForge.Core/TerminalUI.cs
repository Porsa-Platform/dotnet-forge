using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace DotnetForge.Core;

public sealed class ForgeTui : IDisposable
{
    private const int RightPanelWidth = 44;
    private const int MaxTraceLines = 1000;
    private const int SummaryMaxLength = 60;
    private const int RenderIntervalMs = 500;

    private readonly Dictionary<string, SessionView> _sessions;
    private readonly ConcurrentQueue<TraceEntry> _traceLog;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _tuiCts;
    private Task _renderTask = Task.CompletedTask;
    private bool _disposed;
    private string _statusMessage = string.Empty;
    private string _packId = string.Empty;
    private string _workingDirectory = string.Empty;
    private volatile int _dirtyFlag;
    private Layout _cachedLayout;

    public ForgeTui(IReadOnlyList<RuntimeRole> roles, string? packId = null, string? workingDirectory = null)
    {
        _sessions = roles.ToDictionary(
            r => r.Role,
            r => new SessionView { DisplayName = r.DisplayName, Session = r.Session },
            StringComparer.OrdinalIgnoreCase);
        _traceLog = new ConcurrentQueue<TraceEntry>();
        _tuiCts = new CancellationTokenSource();
        _packId = packId ?? "forge";
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _cachedLayout = BuildLayout();
    }

    public void Start()
    {
        _renderTask = Task.Run(() => RenderLoop(_tuiCts.Token));
    }

    public void Trace(string role, string message)
    {
        if (_traceLog.Count > MaxTraceLines) _traceLog.TryDequeue(out _);
        _traceLog.Enqueue(new TraceEntry(role, message, DateTime.UtcNow));
        MarkDirty();
    }

    public void SetStatus(string role, string summary)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(role, out var session))
            {
                session.Summary = summary.Length > SummaryMaxLength
                    ? summary[..SummaryMaxLength] + "…"
                    : summary;
                session.LastActivity = DateTime.UtcNow;
            }
        }
        MarkDirty();
    }

    public void MarkNeedsAttention(string role, bool needsAttention = true)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(role, out var session))
                session.NeedsAttention = needsAttention;
        }
        MarkDirty();
    }

    public void SetStatusMessage(string message)
    {
        _statusMessage = message;
        MarkDirty();
    }

    private void MarkDirty() => _dirtyFlag = 1;

    private async Task RenderLoop(CancellationToken ct)
    {
        System.Console.Write("\u001b[?1049h");
        System.Console.CursorVisible = false;
        try
        {
            await AnsiConsole.Live(_cachedLayout)
                .AutoClear(true)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (_dirtyFlag == 1)
                        {
                            _dirtyFlag = 0;
                            _cachedLayout = BuildLayout();
                            ctx.UpdateTarget(_cachedLayout);
                            ctx.Refresh();
                        }
                        await Task.Delay(RenderIntervalMs, ct);
                    }
                });
        }
        catch (OperationCanceledException) { }
        finally
        {
            System.Console.CursorVisible = true;
            System.Console.Write("\u001b[?1049l");
        }
    }

    private Layout BuildLayout()
    {
        var tracePane = new Layout("TracePane");
        var bottomBar = new Layout("BottomBar").Size(3);
        var mainArea = new Layout("MainArea").SplitRows(tracePane, bottomBar);
        var sidebar = new Layout("Sidebar").Size(RightPanelWidth);
        var root = new Layout("Root").SplitColumns(mainArea, sidebar);

        tracePane.Update(new Panel(BuildTracePane())
        {
            Border = BoxBorder.None,
            Expand = true,
            Padding = new Padding(1, 0, 1, 0),
        });

        bottomBar.Update(BuildStatusBar());
        sidebar.Update(BuildSidebar());
        return root;
    }

    private Panel BuildStatusBar()
    {
        var msg = string.IsNullOrEmpty(_statusMessage)
            ? "[dim]Forge is running. Use 'dotnet-forge stop' to shut down.[/]"
            : Markup.Escape(_statusMessage);
        return new Panel(new Markup($"[bold magenta]░[/] {msg}"))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    private Panel BuildTracePane()
    {
        var sb = new StringBuilder();
        const int take = 50;
        var entries = _traceLog.Reverse().Take(take).Reverse().ToArray();

        if (entries.Length == 0)
        {
            sb.AppendLine("[dim]  Waiting for agent activity…[/]");
        }
        else
        {
            foreach (var entry in entries)
            {
                var roleDisplay = _sessions.GetValueOrDefault(entry.Role)?.DisplayName ?? entry.Role;
                var time = entry.Timestamp.ToString("HH:mm:ss");
                var color = entry.Role.ToLowerInvariant() switch
                {
                    "specifier" => "cyan",
                    "coder" => "green",
                    "refactorer" => "yellow",
                    "architect" => "blue",
                    "hardener" => "magenta",
                    "qa" => "red",
                    "cleaner" => "lime",
                    _ => "white"
                };
                sb.AppendLine($"[bold {color}]{Markup.Escape(roleDisplay),-10}[/] [dim]{time}[/] {Markup.Escape(entry.Message)}");
            }
        }

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Expand = true,
        };
    }

    private Panel BuildSidebar()
    {
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;

        sb.AppendLine($"[bold]Build · {Markup.Escape(_packId)}[/]");
        sb.AppendLine($"[dim]{Markup.Escape(_workingDirectory)}[/]");
        sb.AppendLine();

        lock (_lock)
        {
            foreach (var (role, session) in _sessions)
            {
                var indicator = session.NeedsAttention
                    ? "[bold yellow]●[/]"
                    : "  ";
                var name = $"[bold]{Markup.Escape(session.DisplayName)}[/]";

                var idleSeconds = session.LastActivity.HasValue
                    ? (now - session.LastActivity.Value).TotalSeconds
                    : 999;
                var activityDot = idleSeconds < 10 ? "[green]●[/]"
                    : idleSeconds < 60 ? "[yellow]●[/]"
                    : "[dim]●[/]";

                sb.AppendLine($"{indicator} {name} {activityDot}");

                var summary = string.IsNullOrEmpty(session.Summary)
                    ? "[dim]idle[/]"
                    : session.Summary.Length > SummaryMaxLength - 4
                        ? Markup.Escape(session.Summary[..(SummaryMaxLength - 4)] + "…")
                        : Markup.Escape(session.Summary);

                sb.AppendLine($"   {summary}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"[dim]Forge v1.0.0[/]");

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 1, 1, 0),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tuiCts.Cancel();
        try { _renderTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _tuiCts.Dispose();
    }

    private sealed class SessionView
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Session { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public bool NeedsAttention { get; set; }
        public DateTime? LastActivity { get; set; }
    }

    private sealed record TraceEntry(string Role, string Message, DateTime Timestamp);
}


