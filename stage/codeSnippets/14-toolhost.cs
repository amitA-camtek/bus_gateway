// Realizes: §1.3.3 ToolHost supervisor
// Project: ToolHost\Camtek.ToolHost (net8.0) — the ONE Windows service (3 → 1)
// Responsibility: supervises headless children (broker, gateway, ToolServices, DataServer, FAR...),
//                job objects (KILL_ON_JOB_CLOSE), per-child restart + quarantine classes,
//                health API :5100, endpoint manifest (hash in fleet fingerprint).
// Note: net8 — modern C# 12 syntax.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Camtek.ToolHost
{
    // ═══ Entry point ═════════════════════════════════════════════════════════════════

    internal static class Program
    {
        static async Task Main(string[] args)
        {
            var cts  = new CancellationTokenSource();
            var host = new ToolHostService();
            await host.RunAsync(cts.Token);
        }
    }

    // ═══ ToolHostService ══════════════════════════════════════════════════════════════

    public sealed class ToolHostService
    {
        private readonly ProcessSupervisor _supervisor;
        private readonly HealthAggregator  _health;
        private readonly EndpointManifest  _manifest;

        public ToolHostService()
        {
            _manifest   = EndpointManifest.Load(); // single source of truth for all endpoints
            _supervisor = new ProcessSupervisor(_manifest);
            _health     = new HealthAggregator(_supervisor, _manifest);
        }

        private readonly CancellationTokenSource _run = new();

        public async Task RunAsync(CancellationToken ct)
        {
            // CC7-11: StartAllAsync must RETURN after spawning (retaining the supervisor tasks),
            // NOT await its own infinite supervision loops — otherwise the health API never serves.
            var supervisors = await _supervisor.StartAllAsync(ct);  // spawns children + returns supervision tasks
            await Task.WhenAll(_health.ServeAsync(ct), Task.WhenAll(supervisors));
        }

        // §1.3.3 — the Windows-service surface (SCM SERVICE_CONTROL_* entry points).
        public void OnStart() => _ = RunAsync(_run.Token);

        // R-OPS-3 + M-19/CC7-9: set the Stopping latch BEFORE the first drain signal so the
        // quarantine-never restart loop does not fight the drain (restarting broker/gateway
        // mid-drain then job-object-killing the fresh instance). Reverse-order drain, then cancel.
        public void OnStop()
        {
            _supervisor.Stopping = true;                      // child exits now treated as final
            _supervisor.StopAllAsync(TimeSpan.FromSeconds(30), CancellationToken.None).GetAwaiter().GetResult(); // reverse startOrder + per-child timeout
            _run.Cancel();
        }
    }

    // ═══ ProcessSupervisor ════════════════════════════════════════════════════════════
    // §1.3.3: "job objects (KILL_ON_JOB_CLOSE) — restart backoff — per-child quarantine class"
    // §1.3.3: broker/gateway: quarantine: never (infinite max-backoff + escalating alarm)
    //         leaf children: quarantine normally (maxPerHour threshold)

    public sealed class ProcessSupervisor
    {
        private readonly EndpointManifest       _manifest;
        // Keyed by child name so a restarted process REPLACES its entry (review S-5/CC-6): the old
        // code kept the dead ChildProcess in a List and reassigned only a local var, so the watch
        // loop re-restarted the same dead handle every tick → a new broker every second (split-brain).
        private readonly ConcurrentDictionary<string, ChildProcess> _children = new();
        // Restart counts decay per hour (sliding window) — a lifetime-cumulative count false-quarantines
        // a long-running healthy child after enough unrelated restarts.
        private readonly ConcurrentDictionary<string, RestartWindow> _restartCounts = new();

        private nint _jobHandle; // Win32 job object — KILL_ON_JOB_CLOSE for all children

        public ProcessSupervisor(EndpointManifest manifest)
        {
            _manifest = manifest;
        }

        // Returns the per-child supervision loop tasks WITHOUT awaiting them — the caller (RunAsync)
        // awaits them alongside the health API. Never await them here: that would block health serving
        // (CC7-11 / review S-5).
        public async Task<IReadOnlyList<Task>> StartAllAsync(CancellationToken ct)
        {
            CreateJobObject();

            // §1.3.3: startOrder: broker (0) → gateway → ToolServices → leaves
            var orderedConfigs = _manifest.Children;
            orderedConfigs.Sort((a, b) => a.StartOrder.CompareTo(b.StartOrder));

            var supervisors = new List<Task>();
            foreach (var config in orderedConfigs)
            {
                // C9-5: spawn failure is a restart-class failure. Initial-spawn semantics:
                // quarantine-never (broker/gateway) → fail-fast; leaf → degrade-and-alarm.
                ChildProcess child;
                try
                {
                    child = await StartChildAsync(config, ct);
                }
                catch (ChildSpawnException ex)
                {
                    if (config.QuarantineNever)
                        throw new InvalidOperationException($"Critical child '{config.Name}' failed initial spawn — ToolHost cannot start.", ex);
                    // Leaf: degrade-and-alarm; siblings continue.
                    Alarm(new ChildProcess(config, null), 1);
                    Quarantine(new ChildProcess(config, null), 1);
                    continue;
                }
                _children[config.Name] = child;
                // §1.3.3 startOrder gates the initial START order; readiness (broker pipe accepting)
                // should gate the next tier (review CN-15) — omitted here for brevity.

                // One INDEPENDENT supervision loop per child (review S-5/CC-6): a child in 30 s
                // backoff must not delay detection or restart of any sibling.
                supervisors.Add(SuperviseChildAsync(config.Name, ct));
            }
            return supervisors; // NOT awaited — caller does Task.WhenAll
        }

        // ── Per-child crash-containment loop ───────────────────────────────────────
        // §1.3.3: "child exits → log + backoff restart → maxPerHour exceeded →
        //          leaf children quarantine (siblings unaffected);
        //          broker/gateway never quarantine (infinite max-backoff + escalating alarm)"

        private async Task SuperviseChildAsync(string name, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (_children.TryGetValue(name, out var child) && !child.IsRunning)
                    await HandleChildExitAsync(name, ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        // M-19/CC7-9: while an ordered stop is in progress, a child exit is EXPECTED (we asked for it) —
        // never restart it, never escalate. Otherwise the quarantine-never loop restarts broker/gateway
        // mid-drain and the SCM teardown then job-object-kills the fresh instance.
        public volatile bool Stopping;

        private async Task HandleChildExitAsync(string name, CancellationToken ct)
        {
            if (Stopping) return;                              // supervision suspended during OnStop
            if (!_children.TryGetValue(name, out var child)) return;
            var config = child.Config;

            var window = _restartCounts.GetOrAdd(name, _ => new RestartWindow());
            var count  = window.RecordAndCountLastHour();  // sliding 1-hour window (not lifetime)

            if (config.QuarantineNever)
            {
                // §1.3.3 broker + gateway: infinite max-backoff restarts + escalating alarm
                var backoff = ExponentialBackoff(count, maxMs: 30_000);
                Alarm(child, count); // escalating — LOG → WARN → ERROR → PAGE
                await Task.Delay(backoff, ct);
                // C9-5: spawn failure counted in the same window, backed off, alarmed again.
                try
                {
                    _children[name] = await StartChildAsync(config, ct); // REPLACE the entry
                }
                catch (ChildSpawnException ex)
                {
                    var spawnCount = window.RecordAndCountLastHour(); // count spawn failure once
                    Alarm(child, spawnCount); // escalating alarm
                    // Loop continues — next iteration of SuperviseChildAsync will retry after delay.
                }
            }
            else if (count > config.MaxRestartsPerHour)
            {
                // Leaf: quarantine — log + alarm; sibling processes unaffected
                Quarantine(child, count);
            }
            else
            {
                var backoff = ExponentialBackoff(count, maxMs: config.MaxBackoffMs);
                await Task.Delay(backoff, ct);
                // C9-5: spawn failure counted in the same window, backed off, alarmed.
                try
                {
                    _children[name] = await StartChildAsync(config, ct); // REPLACE the entry
                }
                catch (ChildSpawnException)
                {
                    window.RecordAndCountLastHour(); // count the spawn failure; supervisor loop retries
                }
            }
        }

        private Task<ChildProcess> StartChildAsync(ChildConfig config, CancellationToken ct)
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo(config.ExecutablePath, config.Arguments)
                {
                    UseShellExecute = false
                }
            };

            // C9-5: spawn failure is a restart-class failure — caught and surfaced as ChildSpawnException
            // so the caller (HandleChildExitAsync / StartAllAsync) can count, back off, and alarm.
            bool started;
            try
            {
                started = proc.Start();
            }
            catch (Exception ex)
            {
                proc.Dispose();
                throw new ChildSpawnException(config.Name, ex);
            }
            if (!started)
            {
                proc.Dispose();
                throw new ChildSpawnException(config.Name, new Exception("Process.Start() returned false"));
            }

            // Assign to job object — KILL_ON_JOB_CLOSE ensures no orphans on ToolHost exit.
            AssignProcessToJob(proc.Handle, _jobHandle);

            return Task.FromResult(new ChildProcess(config, proc));
        }

        public sealed class ChildSpawnException : Exception
        {
            public string ChildName { get; }
            public ChildSpawnException(string name, Exception inner)
                : base($"Failed to spawn child '{name}': {inner?.Message}", inner)
            {
                ChildName = name;
            }
        }

        private void CreateJobObject()
        {
            // Win32: CreateJobObject + SetInformationJobObject(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)
            // with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
            // §1.3.3: "A killed ToolHost tears down all children via job objects — no orphans, ever"
        }

        private void AssignProcessToJob(nint procHandle, nint jobHandle) { /* Win32 */ }

        private static TimeSpan ExponentialBackoff(int attempt, int maxMs)
        {
            var ms = Math.Min(100 * (1 << Math.Min(attempt, 10)), maxMs);
            return TimeSpan.FromMilliseconds(ms);
        }

        private void Alarm(ChildProcess child, int count) { /* log + alert */ }
        private void Quarantine(ChildProcess child, int count) { /* log + disable restart */ }

        // Graceful stop (review R-OPS-3/OPS-4) — SERVICE_CONTROL_STOP and DeployUI must call this,
        // NOT a hard process kill: children are torn down in REVERSE start order (leaves first, then
        // gateway with a WAL flush, then broker with a journal drain), each with a timeout, and the
        // job-object KILL_ON_JOB_CLOSE is only the backstop for children that miss the timeout.
        // Without this, every Windows Update / service restart kills broker+gateway mid-write.
        public async Task StopAllAsync(TimeSpan perChildTimeout, CancellationToken ct)
        {
            var ordered = new List<ChildProcess>(_children.Values);
            ordered.Sort((a, b) => b.Config.StartOrder.CompareTo(a.Config.StartOrder)); // reverse
            foreach (var child in ordered)
                await child.RequestGracefulStopAsync(perChildTimeout, ct); // signal → await → (job kill backstop)
        }
    }

    // Sliding 1-hour restart window (review S-5): counts restarts in the last hour, not for the
    // process lifetime, so a healthy long-running child is not eventually false-quarantined.
    public sealed class RestartWindow
    {
        private readonly Queue<DateTime> _stamps = new();
        private readonly object _gate = new();
        public int RecordAndCountLastHour()
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;      // wall clock is fine here — coarse windowing, not Ttl
                _stamps.Enqueue(now);
                while (_stamps.Count > 0 && (now - _stamps.Peek()) > TimeSpan.FromHours(1))
                    _stamps.Dequeue();
                return _stamps.Count;
            }
        }
    }

    // ═══ HealthAggregator :5100 ════════════════════════════════════════════════════
    // §1.3.3: "HealthAggregator :5100 — per-child probes + bus counters mirror +
    //          broker delivered vs gateway processed"

    public sealed class HealthAggregator
    {
        private readonly ProcessSupervisor _supervisor;
        private readonly EndpointManifest  _manifest;

        public HealthAggregator(ProcessSupervisor supervisor, EndpointManifest manifest)
        {
            _supervisor = supervisor;
            _manifest   = manifest;
        }

        public async Task ServeAsync(CancellationToken ct)
        {
            // :5100 must bind the management LAN interface (NOT loopback) so Fleet can poll it off-box
            // (C8-CRIT-1/D16). The binding address is a signed-manifest config value — not hardcoded.
            // http://+:5100/ (all interfaces) is the safe default for sites without a fixed mgmt NIC.
            var listenAddress = _manifest.HealthBindAddress ?? "http://+:5100/";
            var listener = new HttpListener();
            listener.Prefixes.Add(listenAddress + "health/");
            listener.Start();

            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                _ = HandleRequestAsync(context, ct);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var report = BuildHealthReport();
            var json   = System.Text.Json.JsonSerializer.Serialize(report);
            var bytes  = System.Text.Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }

        private ToolHealthReport BuildHealthReport()
        {
            // §6.6: "counters pushed to ToolHost each heartbeat (survive broker death)"
            // Includes: per-child up/quarantined, bus counters mirror, broker lag probe result
            return new ToolHealthReport
            {
                Timestamp      = DateTime.UtcNow,
                ManifestHash   = _manifest.Hash, // in the fleet fingerprint (§1.4)
                Children       = new List<ChildHealthEntry>() // TODO: per child
            };
        }
    }

    // ═══ EndpointManifest ═══════════════════════════════════════════════════════════
    // §1.3.3: "children config + endpoint manifest (single source of truth — hash in fleet fingerprint)"
    // §1.4: "One ToolHost-owned manifest; endpoint hash in the fleet fingerprint; DNS for Fleet"

    public sealed class EndpointManifest
    {
        public string       Hash              { get; private set; } // SHA-256 of the manifest content
        public string       HealthBindAddress { get; private set; } // e.g. "http://192.168.10.1:5100/" (mgmt NIC, D16)
        public List<ChildConfig> Children     { get; private set; }

        public static EndpointManifest Load(string path = @"C:\bis\config\toolbus.json")
        {
            // Load from toolbus.json; compute Hash for the fleet fingerprint.
            var manifest = new EndpointManifest
            {
                Hash     = ComputeHash(path),
                Children = DefaultChildren()
            };
            manifest.VerifySignature(path); // SEC-2: verified BEFORE any child launch
            return manifest;
        }

        // R-7/SEC-2 (§6.8.4): the manifest is SIGNED; ToolHost verifies before launch and
        // FAILS CLOSED on mismatch — LocalSystem must never exec a path from a tamperable file.
        public void VerifySignature(string path)
        {
            // TODO(R-7/SEC-2): Authenticode/manifest signature check; throw (fail-closed) on mismatch.
        }

        // TODO(R-7/SEC-2): real SHA-256 of the manifest content — the "TODO" literal was
        // itself the SEC-2 finding artifact; the hash feeds the fleet fingerprint (§1.4).
        private static string ComputeHash(string path) { return "TODO"; }

        private static List<ChildConfig> DefaultChildren()
        {
            // §1.3.3 diagram: broker (startOrder 0), gateway, ToolServices host, DataServer, FAR...
            return new List<ChildConfig>
            {
                new ChildConfig
                {
                    Name            = "broker",
                    ExecutablePath  = @"C:\bis\bin\Camtek.Messaging.Broker.exe",
                    StartOrder      = 0,
                    QuarantineNever = true,   // §1.3.3 + §5.3: "quarantine: never"
                    PriorityClass   = ProcessPriorityClass.AboveNormal,
                    MaxBackoffMs    = 30_000
                },
                new ChildConfig
                {
                    Name            = "gateway",
                    ExecutablePath  = @"C:\bis\bin\ToolConnect.Service.exe",
                    StartOrder      = 1,
                    QuarantineNever = true,   // §5.3: "quarantine: never"
                    MaxBackoffMs    = 30_000
                },
                new ChildConfig
                {
                    Name               = "toolservices",
                    ExecutablePath     = @"C:\bis\bin\Camtek.ToolServices.Host.exe",
                    StartOrder         = 2,
                    MaxRestartsPerHour = 5,
                    MaxBackoffMs       = 10_000
                },
                new ChildConfig
                {
                    // The GEM process MUST be supervised here with startOrder > broker (review R-6/CN-3):
                    // it was previously unmanaged, so its "bus handshake before enabling REMOTE" had no
                    // guaranteed ordering relative to broker startup.
                    Name            = "secsgem",
                    ExecutablePath  = @"C:\bis\bin\SecsGemGui.Net.exe",
                    StartOrder      = 1,      // after broker (0), alongside gateway
                    QuarantineNever = true,   // the fab-facing door — never leave it dark silently
                    MaxBackoffMs    = 30_000
                }
                // DataServer, FAR, etc. added as children replaces their old service entries
            };
        }
    }

    // ═══ Supporting types ════════════════════════════════════════════════════════════

    public sealed class ChildConfig
    {
        public string               Name               { get; init; }
        public string               ExecutablePath     { get; init; }
        public string               Arguments          { get; init; } = "";
        public int                  StartOrder         { get; init; }
        public bool                 QuarantineNever    { get; init; }
        public ProcessPriorityClass PriorityClass      { get; init; } = ProcessPriorityClass.Normal;
        public int                  MaxRestartsPerHour { get; init; } = 3;
        public int                  MaxBackoffMs       { get; init; } = 5_000;
    }

    public sealed class ChildProcess
    {
        public ChildConfig Config    { get; }
        public Process     Process   { get; }
        public bool        IsRunning { get { return Process != null && !Process.HasExited; } }

        public ChildProcess(ChildConfig config, Process process)
        {
            Config  = config;
            Process = process;
        }

        // Graceful stop for R-OPS-3: signal the child to drain (named-pipe control message / CTRL
        // event), await exit up to the timeout, then let the job object kill it as a backstop.
        public async Task RequestGracefulStopAsync(TimeSpan timeout, CancellationToken ct)
        {
            SignalDrain(); // e.g. a control frame the child handles by flushing WAL/journal then exiting
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout);
                try { await Process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* timed out — job-object KILL_ON_JOB_CLOSE backstop */ }
            }
        }

        private void SignalDrain() { /* control-channel stop request to the child */ }
    }

    public sealed class ToolHealthReport
    {
        public DateTime          Timestamp    { get; set; }
        public string            ManifestHash { get; set; }
        public List<ChildHealthEntry> Children { get; set; }
    }

    public sealed class ChildHealthEntry
    {
        public string Name         { get; set; }
        public bool   IsRunning    { get; set; }
        public bool   IsQuarantined { get; set; }
        public int    RestartCount  { get; set; }
    }
}
