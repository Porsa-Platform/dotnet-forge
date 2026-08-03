using System.Collections.Concurrent;
using System.Text;
using Spectre.Console;

namespace DotnetForge.Core;

/// <summary>
/// Interactive TUI dashboard for dotnet-forge.
/// Shows agent status sidebar, scrollable trace/peek log, and supports:
///   - ↑↓ / j,k: select an agent
///   - Enter: peek into the selected agent's tmux pane (live refresh)
///   - i: enter input mode to send a command to the selected agent
///   - PgUp/PgDn / w,s: scroll trace/peek view
///   - Home/End: jump to top/bottom
///   - Escape: exit peek/input mode
///   - q: quit
/// </summary>
public sealed class ForgeTui : IDisposable
{
    private const int RightPanelWidth = 44;
    private const int MaxTraceLines = 5000;
    private const int SummaryMaxLength = 60;
    private const int RenderIntervalMs = 400;
    private const int PeekRefreshMs = 1500;

    private readonly Dictionary<string, SessionView> _sessions;
    private readonly ConcurrentQueue<TraceEntry> _traceLog;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _tuiCts = new();
    private readonly ICommandRunner _runner;
    private readonly string _tmuxSocket;
    private Task _renderTask = Task.CompletedTask;
    private Task _keyTask = Task.CompletedTask;
    private bool _disposed;
    private string _statusMessage = string.Empty;
    private string _packId;
    private string _workingDirectory;

    // --- Interactive state ---
    private int _selectedIndex;
    private bool _peekMode;
    private bool _inputMode;
    private readonly StringBuilder _inputBuffer = new();
    private string _peekContent = string.Empty;
    private DateTime _lastPeekRefresh = DateTime.MinValue;
    private bool _quitRequested;
    private volatile int _dirtyFlag;

    // --- Scroll state ---
    private int _scrollOffset;
    private int _maxScroll;

    public bool QuitRequested => _quitRequested;

    public ForgeTui(
        IReadOnlyList<RuntimeRole> roles,
        ICommandRunner runner,
        string tmuxSocket,
        string packId = "",
        string workingDirectory = "")
    {
        _sessions = roles.ToDictionary(
            r => r.Role,
            r => new SessionView { DisplayName = r.DisplayName, Session = r.Session },
            StringComparer.OrdinalIgnoreCase);
        _traceLog = new ConcurrentQueue<TraceEntry>();
        _packId = packId;
        _workingDirectory = workingDirectory;
        _runner = runner;
        _tmuxSocket = tmuxSocket;
    }

    public void Start()
    {
        // Set UTF-8 output encoding early so all Unicode characters render correctly
        Console.OutputEncoding = Encoding.ASCII;
        _keyTask = Task.Run(KeyListenLoop);
        _renderTask = Task.Run(() => LiveRenderLoop(_tuiCts.Token));
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

    // ── Keyboard input loop (separate thread) ───────────────────────

    private void KeyListenLoop()
    {
        try
        {
            while (!_tuiCts.Token.IsCancellationRequested && !_quitRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    HandleKey(key);
                }
                else
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (_inputMode)
        {
            HandleInputKey(key);
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.Q:
                _quitRequested = true;
                break;
            case ConsoleKey.Escape:
                if (_peekMode || _inputMode)
                {
                    _peekMode = false;
                    _inputMode = false;
                    _peekContent = string.Empty;
                    _inputBuffer.Clear();
                    _scrollOffset = 0;
                    MarkDirty();
                }
                break;
            case ConsoleKey.Enter:
                if (!_peekMode)
                {
                    _peekMode = true;
                    _scrollOffset = 0;
                    _lastPeekRefresh = DateTime.MinValue;
                    RefreshPeekContent();
                    MarkDirty();
                }
                break;
            case ConsoleKey.I:
                if (!_peekMode && !_inputMode)
                {
                    _inputMode = true;
                    _inputBuffer.Clear();
                    MarkDirty();
                }
                break;
            case ConsoleKey.J:
            case ConsoleKey.DownArrow:
                MoveSelection(1);
                MarkDirty();
                break;
            case ConsoleKey.K:
            case ConsoleKey.UpArrow:
                MoveSelection(-1);
                MarkDirty();
                break;
            case ConsoleKey.PageDown:
            case ConsoleKey.S:
                ScrollDown(key.Modifiers.HasFlag(ConsoleModifiers.Control) ? 20 : 10);
                MarkDirty();
                break;
            case ConsoleKey.PageUp:
            case ConsoleKey.W:
                ScrollUp(key.Modifiers.HasFlag(ConsoleModifiers.Control) ? 20 : 10);
                MarkDirty();
                break;
            case ConsoleKey.Home:
                _scrollOffset = 0;
                MarkDirty();
                break;
            case ConsoleKey.End:
                _scrollOffset = int.MaxValue; // clamped during render
                MarkDirty();
                break;
        }
    }

    private void HandleInputKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (_inputBuffer.Length > 0)
                {
                    SendCommandToAgent(_inputBuffer.ToString());
                    _inputBuffer.Clear();
                }
                _inputMode = false;
                MarkDirty();
                break;
            case ConsoleKey.Escape:
                _inputMode = false;
                _inputBuffer.Clear();
                MarkDirty();
                break;
            case ConsoleKey.Backspace:
                if (_inputBuffer.Length > 0)
                {
                    _inputBuffer.Length--;
                    MarkDirty();
                }
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    _inputBuffer.Append(key.KeyChar);
                    MarkDirty();
                }
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = _sessions.Count;
        if (count == 0) return;
        _selectedIndex = (_selectedIndex + delta + count) % count;
    }

    private void ScrollDown(int lines)
    {
        _scrollOffset = Math.Min(_scrollOffset + lines, _maxScroll);
    }

    private void ScrollUp(int lines)
    {
        _scrollOffset = Math.Max(0, _scrollOffset - lines);
    }

    private string GetSelectedRole()
    {
        var i = 0;
        foreach (var kv in _sessions)
        {
            if (i == _selectedIndex) return kv.Key;
            i++;
        }
        return "";
    }

    private void RefreshPeekContent()
    {
        _lastPeekRefresh = DateTime.UtcNow;
        var role = GetSelectedRole();
        if (role is not { Length: > 0 } || !_sessions.TryGetValue(role, out var sv)) return;

        try
        {
            var result = _runner.Run(
                OperatingSystem.IsWindows() ? "bash" : "sh",
                ["-c", $"tmux -S \"{_tmuxSocket}\" capture-pane -t \"{sv.Session}\" -p"],
                throwOnError: false);
            _peekContent = result.StandardOutput;
        }
        catch
        {
            _peekContent = "(session may have ended)";
        }
    }

    private void SendCommandToAgent(string command)
    {
        var role = GetSelectedRole();
        if (role is not { Length: > 0 } || !_sessions.TryGetValue(role, out var sv)) return;

        try
        {
            var escaped = command.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var shell = OperatingSystem.IsWindows() ? "bash" : "sh";
            _runner.Run(shell, ["-c", $"tmux -S \"{_tmuxSocket}\" send-keys -t \"{sv.Session}\" -l \"{escaped}\""], throwOnError: false);
            _runner.Run(shell, ["-c", $"tmux -S \"{_tmuxSocket}\" send-keys -t \"{sv.Session}\" Enter"], throwOnError: false);
            Trace(role, $"[cmd] {command}");
        }
        catch (Exception ex)
        {
            Trace("forge", $"Failed to send: {ex.Message}");
        }
    }

    // ── Spectre.Console Live rendering loop ─────────────────────────

    private async Task LiveRenderLoop(CancellationToken ct)
    {
        Console.Write("\u001b[?1049h");
        Console.CursorVisible = false;
        var firstRender = true;
        try
        {
            await AnsiConsole.Live(new Layout("Root"))
                .AutoClear(true)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!ct.IsCancellationRequested && !_quitRequested)
                    {
                        // Refresh peek content periodically
                        if (_peekMode)
                        {
                            var elapsed = (DateTime.UtcNow - _lastPeekRefresh).TotalMilliseconds;
                            if (elapsed > PeekRefreshMs)
                            {
                                RefreshPeekContent();
                                MarkDirty();
                            }
                        }

                        if (_dirtyFlag == 1 || firstRender)
                        {
                            _dirtyFlag = 0;
                            firstRender = false;
                            ctx.UpdateTarget(BuildLayout());
                            ctx.Refresh();
                        }
                        await Task.Delay(RenderIntervalMs, ct);
                    }
                });
        }
        catch (OperationCanceledException) { }
        finally
        {
            Console.CursorVisible = true;
            Console.Write("\u001b[?1049l");
        }
    }

    private Layout BuildLayout()
    {
        var contentLayout = new Layout("Content");
        contentLayout.Update(_peekMode ? BuildPeekPane() : BuildTracePane());

        var bottomBar = new Layout("BottomBar").Size(3);
        bottomBar.Update(BuildStatusBar());

        var mainArea = new Layout("MainArea").SplitRows(contentLayout, bottomBar);
        var sidebar = new Layout("Sidebar").Size(RightPanelWidth);
        sidebar.Update(BuildSidebarContent());

        if (_inputMode)
        {
            var inputLayout = new Layout("InputPane").Size(3);
            inputLayout.Update(BuildInputBar());
            mainArea = new Layout("MainArea").SplitRows(contentLayout, inputLayout, bottomBar);
        }

        return new Layout("Root").SplitColumns(mainArea, sidebar);
    }

    private Panel BuildStatusBar()
    {
        var msg = string.IsNullOrEmpty(_statusMessage)
            ? "[dim]Forge is running[/]"
            : Markup.Escape(_statusMessage);
        return new Panel(new Markup($"[bold magenta]*[/] {msg}"))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    private Panel BuildTracePane()
    {
        var sb = new StringBuilder();
        var allEntries = _traceLog.Reverse().ToArray();
        var totalLines = allEntries.Length;

        // Determine visible window height (approximate)
        var visibleHeight = Console.WindowHeight - 8; // minus status bar, key hints, etc.
        if (_inputMode) visibleHeight -= 3;

        // Clamp scroll
        _maxScroll = Math.Max(0, totalLines - visibleHeight);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, _maxScroll);

        var start = _scrollOffset;
        var end = Math.Min(start + visibleHeight, totalLines);
        var entries = allEntries.Skip(start).Take(end - start).Reverse().ToArray();

        // Scroll indicator
        if (_maxScroll > 0)
        {
            var pct = _maxScroll == 0 ? 100 : (int)((double)_scrollOffset / _maxScroll * 100);
            sb.AppendLine($"[dim]── {pct}% scroll (PgUp/PgDn or w/s) ──[/]");
        }

        if (entries.Length == 0)
        {
            sb.AppendLine("[dim]  Waiting for agent activity…[/]");
        }
        else
        {
            foreach (var entry in entries)
            {
                var roleDisplay = _sessions.GetValueOrDefault(entry.Role)?.DisplayName ?? entry.Role;
                var time = entry.Timestamp.ToUniversalTime().ToString("HH:mm:ss");
                var color = entry.Role.ToLowerInvariant() switch
                {
                    "specifier" => "cyan",
                    "coder" => "green",
                    "refactorer" => "yellow",
                    "architect" => "blue",
                    _ => "white"
                };
                sb.AppendLine($"[bold {color}]{Markup.Escape(roleDisplay),-10}[/] [dim]{time}[/] {Markup.Escape(entry.Message)}");
            }
        }

        // Pad if fewer entries than visible height
        for (var i = entries.Length; i < visibleHeight; i++)
            sb.AppendLine();

        var keyHints = "[dim]↑↓/jk:select  Enter:peek  i:send  PgUp/PgDn:scroll  q:quit[/]";
        sb.Append(keyHints);

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Expand = true,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    private Panel BuildPeekPane()
    {
        var sb = new StringBuilder();
        var role = GetSelectedRole();
        var sessionName = role is { Length: > 0 } && _sessions.TryGetValue(role, out var sv)
            ? sv.Session
            : "?";

sb.AppendLine($"[bold cyan]+-- Peek: {Markup.Escape(sessionName)} --------------------------------------------------[/]");

        if (string.IsNullOrEmpty(_peekContent))
        {
            sb.AppendLine("[dim]  (empty or loading…)[/]");
        }
        else
        {
            var allLines = _peekContent.Split('\n');
            var totalLines = allLines.Length;
            var visibleHeight = Console.WindowHeight - 8;
            if (_inputMode) visibleHeight -= 3;

            _maxScroll = Math.Max(0, totalLines - visibleHeight);
            _scrollOffset = Math.Clamp(_scrollOffset, 0, _maxScroll);

            if (_maxScroll > 0)
            {
                var pct = _maxScroll == 0 ? 100 : (int)((double)_scrollOffset / _maxScroll * 100);
                sb.AppendLine($"[dim]── {pct}% ({_scrollOffset}/{_maxScroll}) ──[/]");
            }

            var start = _scrollOffset;
            var end = Math.Min(start + visibleHeight, totalLines);
            for (var i = start; i < end; i++)
            {
                var line = allLines[i].TrimEnd('\r');
                // Replace control chars that mess up display
                var clean = SanitizeForDisplay(line);
                if (string.IsNullOrWhiteSpace(clean))
                    sb.AppendLine();
                else
                    sb.AppendLine(Markup.Escape(clean));
            }

            for (var i = end - start; i < visibleHeight; i++)
                sb.AppendLine();
        }

        sb.AppendLine();
        sb.Append("[dim]Esc:back  i:send  PgUp/PgDn:scroll  q:quit[/]");

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Expand = true,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    /// <summary>Strip ANSI escape sequences and replace non-printable chars.</summary>
    private static string SanitizeForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\u001b' && i + 1 < text.Length)
            {
                i++; // skip ESC
                // CSI: ESC [ ... letter
                if (i < text.Length && text[i] == '[')
                {
                    i++;
                    while (i < text.Length && !char.IsLetter(text[i]))
                        i++;
                    i++; // skip terminating letter
                }
                // OSC: ESC ] ... BEL or ST
                else if (i < text.Length && text[i] == ']')
                {
                    i++;
                    while (i < text.Length && text[i] != '\u0007' && text[i] != '\u001b')
                        i++;
                    if (i < text.Length && text[i] == '\u001b') i++; // skip ST terminator '\'
                    if (i < text.Length) i++; // skip final char
                }
                else
                {
                    i++; // skip unknown escape char
                }
            }
            else if (text[i] < ' ' && text[i] != '\t' && text[i] != '\n' && text[i] != '\r')
            {
                sb.Append(' ');
                i++;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private Panel BuildInputBar()
    {
        var role = GetSelectedRole();
        var sessionName = role is { Length: > 0 } && _sessions.TryGetValue(role, out var sv)
            ? sv.Session
            : "?";

        var sb = new StringBuilder();
        sb.AppendLine($"[yellow]> Send to {Markup.Escape(sessionName)}:[/] {Markup.Escape(_inputBuffer.ToString())}_");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine("[dim]Enter: send  Esc: cancel[/]");

        return new Panel(new Markup(sb.ToString().TrimEnd()))
        {
            Border = BoxBorder.None,
            Padding = new Padding(1, 0, 1, 0),
        };
    }

    private Panel BuildSidebarContent()
    {
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;

        sb.AppendLine($"[bold]Build - {Markup.Escape(_packId)}[/]");
        sb.AppendLine($"[dim]{Markup.Escape(TruncatedPath(_workingDirectory))}[/]");
        sb.AppendLine();

        lock (_lock)
        {
            var index = 0;
            foreach (var (role, session) in _sessions)
            {
                var isSelected = index == _selectedIndex;
                var indicator = isSelected ? "[bold cyan]>[/]" : "  ";
                var name = isSelected
                    ? $"[bold cyan]{Markup.Escape(session.DisplayName)}[/]"
                    : $"[bold]{Markup.Escape(session.DisplayName)}[/]";

                var idleSeconds = session.LastActivity.HasValue
                    ? (now - session.LastActivity.Value).TotalSeconds
                    : 999;
                var activityDot = idleSeconds < 10 ? "[green]*[/]"
                    : idleSeconds < 60 ? "[yellow]*[/]"
                    : "[dim]*[/]";

                sb.AppendLine($"{indicator} {name} {activityDot}");

                var summary = string.IsNullOrEmpty(session.Summary)
                    ? "[dim]idle[/]"
                    : Markup.Escape(session.Summary.Length > SummaryMaxLength - 4
                        ? session.Summary[..(SummaryMaxLength - 4)] + "…"
                        : session.Summary);

                sb.AppendLine($"   {summary}");

                if (isSelected && _peekMode)
                    sb.AppendLine("   [cyan](peek)[/]");
                else if (isSelected && _inputMode)
                    sb.AppendLine("   [yellow](input)[/]");

                index++;
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

    private static string TruncatedPath(string path)
    {
        const int maxLen = 36;
        if (path.Length <= maxLen) return path;
        return "…" + path[^(maxLen - 2)..];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tuiCts.Cancel();
        try { Task.WaitAll([_renderTask, _keyTask], TimeSpan.FromSeconds(3)); } catch { }
        _tuiCts.Dispose();
    }

    private sealed class SessionView
    {
        public string DisplayName { get; set; } = "";
        public string Session { get; set; } = "";
        public string Summary { get; set; } = "";
        public bool NeedsAttention { get; set; }
        public DateTime? LastActivity { get; set; }
    }

    private sealed record TraceEntry(string Role, string Message, DateTime Timestamp);
}





