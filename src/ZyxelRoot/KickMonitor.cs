using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ZyxelRoot.At;

namespace ZyxelRoot;

/// <summary>Origin of one <see cref="KickRecord"/>.</summary>
public enum KickRecordKind
{
    /// <summary>A line from the mipc_tap log (raw-URC channel 0x4001 + SIM IND).</summary>
    Tap = 0,

    /// <summary>One 5 s AT poll (AT+CSQ / AT+CREG / AT+CGREG through mipc_wan_cli).</summary>
    AtPoll = 1,

    /// <summary>A vendor-log line matching the configured pattern.</summary>
    Log = 2,

    /// <summary>A manual mark from the caller (<see cref="KickMonitor.Mark"/>) — e.g. the phone flip.</summary>
    Mark = 3,

    /// <summary>Monitor lifecycle note (tap started/stopped, registration, failures).</summary>
    Notice = 4,

    /// <summary>
    /// The AT path is hung: an AT poll timed out (a healthy module answers in
    /// milliseconds; the vendor client blocks up to 300 s on a frozen module).
    /// The remaining AT commands of the round are skipped — they would only
    /// re-hang the client.
    /// </summary>
    ModemHang = 5
}

/// <summary>
/// One timestamped record in the kick-detection capture. The complete capture
/// (the stream between <see cref="KickMonitor.StartAsync"/> and
/// <see cref="KickMonitor.StopAsync"/>) is what gets scored against
/// the author's kick-detection scorecard.
/// </summary>
public sealed class KickRecord
{
    public required KickRecordKind Kind { get; init; }

    /// <summary>Local time on the machine running the monitor, at emission.</summary>
    public required DateTime At { get; init; }

    /// <summary>
    /// Origin label: <c>tap</c>, the AT command (<c>AT+CSQ</c>…), the log path,
    /// <c>mark</c> or <c>kick-monitor</c>.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>The line, or the one-line summary of the AT round.</summary>
    public required string Text { get; init; }

    /// <summary>Raw material for scoring (raw AT output, raw log line), when useful.</summary>
    public string? Detail { get; init; }

    public override string ToString() =>
        $"{At:HH:mm:ss.fff} [{Kind}] {Label}: {Text}";
}

/// <summary>One tailed vendor log with its filter pattern.</summary>
public sealed class KickLogSource
{
    public KickLogSource(string path, string pattern)
    {
        Path = path;
        Pattern = pattern;
    }

    /// <summary>Log file on the device.</summary>
    public string Path { get; init; }

    /// <summary>.NET regex; only matching lines are emitted.</summary>
    public string Pattern { get; init; }

    private Regex? _filter;

    internal Regex Filter => _filter ??= new Regex(Pattern, RegexOptions.Compiled);
}

/// <summary>Configuration for <see cref="KickMonitor"/>.</summary>
public sealed class KickMonitorOptions
{
    /// <summary>Tap daemon on the device (push the binary to the device first).</summary>
    public string TapBinary { get; set; } = "/tmp/mipc_tap";

    /// <summary>Tap log file (the daemon appends here, flushed per line).</summary>
    public string TapLog { get; set; } = "/tmp/mipc_tap.log";

    /// <summary>
    /// Extra IND registration passed to the tap (<c>0x4001:0x10</c> = the raw-URC
    /// mirror channel, the "jackpot" axis). The tap always keeps its default
    /// 0x4502:0x10 SIM IND.
    /// </summary>
    public string TapIndRegistration { get; set; } = "0x4001:0x10";

    /// <summary>
    /// Expected md5 of <see cref="TapBinary"/>; a mismatch is reported as a
    /// Notice (stale push?) but does not abort. <c>null</c>/empty = skip the check.
    /// </summary>
    public string? TapExpectedMd5 { get; set; } = "4178BD3375918DC2F542740BA7D8A1C4";

    /// <summary>Remove the tap log on <see cref="KickMonitor.StopAsync"/> (the capture is in the stream).</summary>
    public bool RemoveTapLogOnStop { get; set; } = true;

    /// <summary>
    /// Start and tail the mipc_tap daemon. Set false to run log-tails + AT
    /// poll only — for long-lived monitors where the tap binary (a /tmp push)
    /// is absent or not wanted; the kick signature is in the vendor logs either
    /// way. When true (default), startup throws when the binary is missing.
    /// </summary>
    public bool TapEnabled { get; set; } = true;

    /// <summary>Cadence of one AT round + all log tails (default 5 s).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Lines of existing history each log tail replays on start.</summary>
    public int HistoryLines { get; set; } = 10;

    /// <summary>Maximum lines read per file per poll.</summary>
    public int MaxLinesPerPoll { get; set; } = 200;

    /// <summary>Per-AT-command timeout (the module may not answer while it re-camps).</summary>
    public int AtTimeoutSeconds { get; set; } = 30;

    /// <summary>The polled AT commands (CREG/CGREG read CME 100 on this modem — the CS text path is broken, which is itself a data point).</summary>
    public string[] AtCommands { get; set; } = { "AT+CSQ", "AT+CREG", "AT+CGREG" };

    /// <summary>The filtered log tails (file + pattern).</summary>
    public IReadOnlyList<KickLogSource> LogSources { get; set; } = new KickLogSource[]
    {
        new("/var/log/mmm.log", @"CampOnStatus|not registered|is disconnected|ps_state|AT channel was timeout"),
        new("/var/log/cm.log", @"CHANGE STATE|Can not camp|Execute radio reopen|Retry camping|backoff|Backoff over|checkRecovery"),
        new("/var/log/wan.log", @"CampOnStatus|disconnected|interface ID")
    };
}

/// <summary>
/// Live A/B capture for IMSI-collision "kick" detection (same-IMSI client
/// re-attaching and detaching the module): streams, with local timestamps, the
/// raw-URC tap, a 5 s AT poll, and the filtered vendor-log tails that
/// the author's kick-detection notes prescribe. Score the captured
/// stream against §4 of that doc.
/// </summary>
/// <remarks>
/// <para>
/// Sources (all through the one root shell, serialized by its command lock):
/// <list type="bullet">
/// <item><b>Tap</b> — the passive <c>/tmp/mipc_tap</c> daemon on its default
/// port (netagent → CMIPC3, no production holder) with
/// <c>--ind 0x4001:0x10</c> (raw-URC mirror) plus its built-in 0x4502 SIM IND.
/// Lines from <c>/tmp/mipc_tap.log</c> are emitted as <c>Tap</c> records.</item>
/// <item><b>AT poll</b> — every <see cref="KickMonitorOptions.PollInterval"/>:
/// <c>AT+CSQ</c>, <c>AT+CREG</c>, <c>AT+CGREG</c> through
/// <see cref="AtCommandClient"/>; one <c>AtPoll</c> record each, parsed to a
/// one-line summary (timeouts and CME errors included — the module may not
/// answer while it re-camps). A timed-out poll also emits a
/// <c>ModemHang</c> record (the freeze detector: a healthy module answers in
/// milliseconds) and stops the round.</item>
/// <item><b>Log tails</b> — the three vendor logs with the kick-signature patterns
/// (camp status, ps_state, the cm recovery ladder).</item>
/// </list>
///
/// <see cref="Mark"/> writes a <c>Mark</c> record — call it at the instant the
/// phone leaves airplane mode so the capture can be scored around it.
/// </para>
/// <para>
/// <b>Startup</b> refuses to run a second tap instance (throws), verifies the
/// binary exists (md5 mismatch → Notice), truncates the tap log, starts the
/// daemon and waits for its <c>register_ind … rc=0</c> lines — a failed
/// registration throws, because the jackpot channel would be silent.
/// <b>StopAsync</b> SIGTERMs the tap (falls back to SIGKILL), removes the tap
/// log (unless <see cref="KickMonitorOptions.RemoveTapLogOnStop"/> is false)
/// and leaves the device clean.
/// </para>
/// </remarks>
public sealed class KickMonitor : IAsyncDisposable
{
    // The tap prefixes every line with an ISO timestamp.
    private static readonly Regex RegisterLineRegex = new(
        @"register_ind\s+(0x[0-9a-f]{4})\s+mask=0x[0-9a-f]+\s+rc=(-?\d+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly ICommandRunner _shell;
    private readonly KickMonitorOptions _options;
    private readonly AtCommandClient _at;
    private readonly Channel<KickRecord> _channel = Channel.CreateUnbounded<KickRecord>();
    private readonly CancellationTokenSource _cts = new();

    private LogTailSource? _tapTail;
    private List<LogTailSource>? _logTails;
    private List<KickLogSource>? _logSources;
    private Task? _loop;
    private bool _started;
    private bool _stopped;
    private bool _tapExitReported;
    private bool _disposed;
    /// <summary>True while the tap daemon started by this instance is (or may be) running on the device.</summary>
    private bool _tapAlive;

    /// <param name="shell">
    /// The command channel — a live <see cref="RootShell"/> or a
    /// <see cref="PersistentRootShell"/> (which keeps the monitor working across
    /// daemon restarts and router reboots without a rebuild).
    /// </param>
    /// <param name="options">Capture options (tap, poll interval, log sources).</param>
    public KickMonitor(ICommandRunner shell, KickMonitorOptions? options = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _options = options ?? new KickMonitorOptions();
        _at = new AtCommandClient(shell);
    }

    /// <summary>The capture stream (unbounded: a stalled consumer grows memory, nothing is dropped).</summary>
    public ChannelReader<KickRecord> Records => _channel.Reader;

    /// <summary>True between <see cref="StartAsync"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => _started && !_stopped;

    /// <summary>
    /// Writes a <c>Mark</c> record at this instant — call it the moment the
    /// phone is flipped out of airplane mode. Never blocks.
    /// </summary>
    public void Mark(string? note = null) =>
        _channel.Writer.TryWrite(NewRecord(KickRecordKind.Mark, "mark", note ?? "mark", null));

    /// <summary>Streams records until the monitor stops (or the token fires).</summary>
    public async IAsyncEnumerable<KickRecord> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (KickRecord record in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return record;
    }

    /// <summary>Health of the tailed sources (tap log + the three vendor logs).</summary>
    public IReadOnlyList<AtLogSourceStatus> GetStatus()
    {
        var list = new List<AtLogSourceStatus>();
        if (_tapTail is not null) list.Add(_tapTail.GetStatus());
        if (_logTails is not null)
            list.AddRange(_logTails.Select(t => t.GetStatus()));
        return list;
    }

    /// <summary>
    /// Pre-checks, starts the tap daemon (when <see cref="KickMonitorOptions.TapEnabled"/>),
    /// initializes the tails and starts the poll loop. Idempotent. Throws if the
    /// tap binary is missing, another <c>mipc_tap</c> instance is running, or the
    /// IND registration fails (tap-enabled starts only).
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;

        try
        {
            await StartCoreAsync(ct).ConfigureAwait(false);
            _started = true;
            _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        }
        catch
        {
            // Never leave the tap daemon behind on a failed start — but only
            // kill what THIS call started (a pre-existing instance is the
            // caller's problem, and we refused to start over it above).
            if (_tapAlive)
            {
                _tapAlive = false;
                try { await KillTapAsync(_shell, CancellationToken.None).ConfigureAwait(false); } catch { }
                try
                {
                    await _shell.RunAsync($"rm -f {_options.TapLog}", TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        if (!_options.TapEnabled)
            Notice("tap disabled — log tails + AT poll only");

        if (_options.TapEnabled)
            await StartTapAsync(ct).ConfigureAwait(false);

        // --- tails: tap log raw (when enabled), vendor logs filtered ---
        if (_options.TapEnabled)
            _tapTail = new LogTailSource(_options.TapLog,
                (source, line) => new AtLogRecord { Source = source, RawLine = line, Message = line });

        _logSources = _options.LogSources.ToList();
        _logTails = _logSources.Select(s => new LogTailSource(s.Path)).ToList();

        foreach (LogTailSource source in _logTails)
        {
            try
            {
                await source.InitializeAsync(_shell, _options.HistoryLines, ct).ConfigureAwait(false);
            }
            catch
            {
                // Status is recorded by the source; the loop retries it.
            }
        }
        // _started/_loop are set by StartAsync (its catch also cleans a
        // half-started tap); doing it here as well would leak a second loop.
    }

    private async Task StartTapAsync(CancellationToken ct)
    {
        CommandResult running = await _shell.RunAsync("pidof mipc_tap 2>/dev/null", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        string existing = (running.Output ?? "").Trim();
        if (existing.Length > 0)
            throw new ZyxelRootException(
                $"mipc_tap is already running (pid {existing}) — refusing a second instance; stop it first (KickMonitor.StopTapOnDeviceAsync)");

        string bin = _options.TapBinary;
        CommandResult info = await _shell.RunAsync($"ls -l {bin} 2>&1; md5sum {bin} 2>/dev/null", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        string[] lines = (info.Output ?? "").Split('\n');
        string md5Line = "";
        foreach (string l in lines)
        {
            string t = l.Trim();
            if (t.Length >= 33 && Regex.IsMatch(t[..32], @"^[0-9a-fA-F]{32}$"))
            {
                md5Line = t;
                break;
            }
        }

        if (md5Line.Length == 0)
            throw new ZyxelRootException(
                $"tap binary {bin} is missing on the device ({lines[0]}) — push the tap binary to {bin} first (mode 0755)");

        if (!string.IsNullOrEmpty(_options.TapExpectedMd5) &&
            !md5Line.StartsWith(_options.TapExpectedMd5, StringComparison.OrdinalIgnoreCase))
            Notice($"tap md5 mismatch: expected {_options.TapExpectedMd5}, found {md5Line.Split(' ')[0]}");

        await _shell.RunAsync($"rm -f {_options.TapLog}", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        string pid = (await _shell.RunAsync(
            $"{bin} --ind {_options.TapIndRegistration} </dev/null >/dev/null 2>&1 & pidof mipc_tap 2>/dev/null",
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false)).Output.Trim();
        if (pid.Length == 0)
            throw new ZyxelRootException("mipc_tap started but no pid appeared — check the device");
        _tapAlive = true;

        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); // let registrations settle

        string tapLog = (await _shell.RunAsync($"cat {_options.TapLog} 2>/dev/null | head -n 30", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false)).Output ?? "";
        if (!tapLog.Contains("mipc_init rc=0", StringComparison.Ordinal))
            throw new ZyxelRootException($"tap failed to initialize (CMIPC3/netagent): {tapLog.Trim()}");

        // Normalize the requested msgid to the 0x%04x form the tap logs.
        string wantMsgid = "0x4001";
        string[] part = _options.TapIndRegistration.Split(':');
        string msgidText = part[0].Trim();
        if (msgidText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            uint v = Convert.ToUInt32(msgidText[2..], 16);
            wantMsgid = "0x" + v.ToString("x4");
        }
        else if (uint.TryParse(msgidText, out uint u))
        {
            wantMsgid = "0x" + u.ToString("x4");
        }
        var registered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RegisterLineRegex.Matches(tapLog))
            registered[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);

        foreach (string msgid in new[] { "0x4502", wantMsgid }.Distinct())
        {
            if (!registered.TryGetValue(msgid, out int rc) || rc != 0)
                throw new ZyxelRootException(
                    $"tap IND registration failed for {msgid} (rc={(registered.TryGetValue(msgid, out int r2) ? r2.ToString() : "missing")}) — the capture would be blind; log: {tapLog.Trim()}");
            Notice($"tap: register_ind {msgid} rc=0");
        }

        Notice($"tap started (pid {pid}), ind={_options.TapIndRegistration}");
    }

    /// <summary>
    /// Stops the loop, SIGTERMs the tap daemon (SIGKILL fallback), removes the
    /// tap log and completes the stream. Idempotent.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_started || _stopped) return;
        _stopped = true;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }

        // Only touch a tap this instance started: with TapEnabled=false a
        // pre-existing tap belongs to someone else (startup did not refuse it).
        if (_tapAlive)
        {
            bool clean = await KillTapAsync(_shell, ct).ConfigureAwait(false);
            _tapAlive = false;
            Notice(clean ? "tap stopped, no mipc_tap left running" : "WARNING: mipc_tap is still running after TERM/KILL — kill it manually");

            if (_options.RemoveTapLogOnStop)
                await _shell.RunAsync($"rm -f {_options.TapLog}", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }

        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// Stops any <c>mipc_tap</c> on the device (SIGTERM, then SIGKILL) — the
    /// cleanup for a monitor that died without stopping. Returns true when no
    /// tap instance is left.
    /// </summary>
    public static async Task<bool> StopTapOnDeviceAsync(ICommandRunner shell, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return await KillTapAsync(shell, ct).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            Stopwatch sw = Stopwatch.StartNew();
            bool roundFailed = false;

            try
            {
                // 1. tap log (raw; absent when TapEnabled=false)
                if (_tapTail is { } tapTail)
                    foreach (AtLogRecord rec in await tapTail.PollAsync(_shell, _options.MaxLinesPerPoll, ct).ConfigureAwait(false))
                {
                    string line = rec.RawLine;
                    WriteNew(KickRecordKind.Tap, "tap", line, null);
                    if (!_tapExitReported &&
                        (line.StartsWith("STOP", StringComparison.Ordinal) ||
                         line.StartsWith("ERROR", StringComparison.Ordinal)))
                    {
                        _tapExitReported = true;
                        Notice($"tap daemon exited: {line}");
                    }
                }

                // 2. filtered vendor-log tails
                for (int i = 0; i < _logTails!.Count; i++)
                {
                    KickLogSource config = _logSources![i];
                    foreach (AtLogRecord rec in await _logTails[i].PollAsync(_shell, _options.MaxLinesPerPoll, ct).ConfigureAwait(false))
                    {
                        if (!config.Filter.IsMatch(rec.RawLine)) continue;
                        WriteNew(KickRecordKind.Log, config.Path, rec.ToString(), rec.RawLine);
                    }
                }

                // 3. AT round (a timeout means the AT path is hung; the rest of
                //    the round would only re-hang the vendor client, so stop)
                foreach (string command in _options.AtCommands)
                {
                    AtCommandResult result = await _at.SendAtCommandAsync(command, _options.AtTimeoutSeconds, ct).ConfigureAwait(false);
                    WriteNew(KickRecordKind.AtPoll, command, Summarize(command, result), result.Output);
                    if (result.TimedOut)
                    {
                        WriteNew(KickRecordKind.ModemHang, command,
                            "AT path hung (poll timed out; the vendor client blocks up to 300 s on a frozen module) — shell recovering",
                            null);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                roundFailed = true;
            }

            consecutiveFailures = roundFailed ? consecutiveFailures + 1 : 0;
            if (roundFailed && consecutiveFailures % 10 == 0)
                Notice($"poll round failed {consecutiveFailures} rounds in a row (shell not answering?)");
            if (consecutiveFailures >= 10)
            {
                _channel.Writer.TryComplete(new ZyxelRootException("kick monitor stopped: the root shell stopped answering"));
                return;
            }

            try
            {
                TimeSpan remaining = _options.PollInterval - sw.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void WriteNew(KickRecordKind kind, string label, string text, string? detail) =>
        _channel.Writer.TryWrite(NewRecord(kind, label, text, detail));

    private KickRecord NewRecord(KickRecordKind kind, string label, string text, string? detail) =>
        new()
        {
            Kind = kind,
            At = DateTime.Now,
            Label = label,
            Text = text,
            Detail = detail
        };

    private void Notice(string text) => WriteNew(KickRecordKind.Notice, "kick-monitor", text, null);

    private static string Summarize(string command, AtCommandResult result)
    {
        if (result.TimedOut) return "timeout";

        string o = result.Output ?? "";
        Match cme = AtCommandClient.CmeRegex.Match(o);
        if (cme.Success) return $"CME {cme.Groups[1].Value}";

        switch (command.Trim().ToUpperInvariant())
        {
            case "AT+CSQ":
                {
                    Match m = AtCommandClient.CsqRegex.Match(o);
                    return m.Success ? $"rssi={m.Groups[1].Value}/31 ber={m.Groups[2].Value}" : "(no +CSQ in reply)";
                }
            case "AT+CREG":
                {
                    Match m = AtCommandClient.CregRegex.Match(o);
                    return m.Success ? $"stat={m.Groups[1].Value}" : "(no +CREG in reply)";
                }
            case "AT+CGREG":
                {
                    Match m = AtCommandClient.CgregRegex.Match(o);
                    return m.Success ? $"stat={m.Groups[1].Value}" : "(no +CGREG in reply)";
                }
            default:
                {
                    string? first = o.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
                    return first is null ? "(empty reply)" : (first.Trim().Length > 80 ? first.Trim()[..80] : first.Trim());
                }
        }
    }

    private static async Task<bool> KillTapAsync(ICommandRunner shell, CancellationToken ct)
    {
        string pids = (await shell.RunAsync("pidof mipc_tap 2>/dev/null", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false)).Output.Trim();
        if (pids.Length == 0) return true;

        await shell.RunAsync("kill -TERM $(pidof mipc_tap) 2>/dev/null; sleep 1", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

        pids = (await shell.RunAsync("pidof mipc_tap 2>/dev/null", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false)).Output.Trim();
        if (pids.Length == 0) return true;

        await shell.RunAsync("kill -9 $(pidof mipc_tap) 2>/dev/null; sleep 1", TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        pids = (await shell.RunAsync("pidof mipc_tap 2>/dev/null", TimeSpan.FromSeconds(10), ct).ConfigureAwait(false)).Output.Trim();
        return pids.Length == 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_started && !_stopped)
                await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort cleanup; the device state is reported in the stream
        }

        try { _cts.Dispose(); } catch (ObjectDisposedException) { }
    }
}
