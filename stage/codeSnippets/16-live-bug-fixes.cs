// Realizes: §05 §5.5 Live bugs LB1–LB5 (independent of the fabric program, Wave 0)
// LB3 + LB4 recommended for immediate filing.
// Each fix is shown as a MINIMAL diff (before → after) without touching surrounding code.

// ═══════════════════════════════════════════════════════════════════════════════════════
// LB1 — Spool poison loop
// File: Utilities\ToolGateway\ToolGateway.BL\EventMessages\FailedMessagesHandler.cs:98-153
// Bug: no retry cap or age limit → a deterministic-fail message cycles forever,
//      blocking all subsequent messages in the spool indefinitely.
// ═══════════════════════════════════════════════════════════════════════════════════════

namespace ToolGateway.BL.EventMessages
{
    // BEFORE (simplified — the infinite loop):
    //   while (true)
    //   {
    //       var msg = DequeueNextFailed();
    //       if (!TryDeliver(msg)) Reenqueue(msg); // no cap — loops forever on poison
    //   }

    // AFTER: split outage-retry from poison; cap attempts; dead-letter after max.
    internal sealed class FailedMessagesHandler_Fixed
    {
        private const int MaxAttempts       = 10;
        private static readonly TimeSpan MaxAge = System.TimeSpan.FromHours(24);

        private void ProcessNextFailedMessage(SpoolMessage msg)
        {
            if (msg.Attempts >= MaxAttempts || msg.Age > MaxAge)
            {
                // Poison: dead-letter to a READABLE file + alarm. Stop cycling.
                DeadLetter(msg, "max-attempts exceeded");
                return;
            }

            if (!TryDeliver(msg))
            {
                // Still outage: increment + requeue with exponential backoff.
                msg.Attempts++;
                msg.NextRetryAt = System.DateTime.UtcNow.AddSeconds(ExponentialBackoffSeconds(msg.Attempts));
                Reenqueue(msg);
            }
            // On success: message is consumed and removed from spool.
        }

        private static double ExponentialBackoffSeconds(int attempt)
            => System.Math.Min(System.Math.Pow(2, attempt), 300); // cap at 5 minutes

        private bool TryDeliver(SpoolMessage msg) { return false; }
        private void Reenqueue(SpoolMessage msg) { }
        private void DeadLetter(SpoolMessage msg, string reason) { }
    }

    internal sealed class SpoolMessage
    {
        public int      Attempts    { get; set; }
        public System.TimeSpan Age { get; set; }
        public System.DateTime NextRetryAt { get; set; }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// LB2 — ToolApiPublisher: Thread.Sleep on scan thread + no gRPC deadline + stale exe name
// File: system\CamtekSystem\PubSub\ToolApi\ToolApiPublisher.cs
// Bug: (a) Thread.Sleep(1000) on the scan thread blocks scan throughput.
//      (b) No gRPC deadline — gateway down ⇒ 30 s UI freeze per scan.
//      (c) Write-only dead-letter file (never read/drained).
//      (d) Stale self-restart exe name causes silent failure on gateway restart.
// Fix at Wave 0: add deadline; fix exe name. Full fix: retire at P1b (BusAdapter replaces it).
// ═══════════════════════════════════════════════════════════════════════════════════════

namespace CamtekSystem.PubSub.ToolApi
{
    internal sealed class ToolApiPublisher_Fixes
    {
        // BEFORE (verified — ToolApiPublisher.cs:32-36,124-137,204-211):
        //   private void PushEvent(ScanEvent e) {
        //       try { _client.Push(e); }                        // (c) no gRPC deadline
        //       catch { Thread.Sleep(1000); TryRestartGateway(); }  // (a) sleep + (b) spawn ON CALLER
        //   }
        //   private void TryRestartGateway() {
        //       // (d) The shipped code self-restarts a FOREIGN path that does not exist in the repo:
        //       Process.Start(@"c:\bis\bin\x64\fleet\toolapi\Fleet.ToolAPI.Endpoint.exe");
        //       // The launcher clsInitAOI actually starts c:\bis\bin\x64\ToolGateway\ToolGateway.Endpoint.exe
        //       // → on a tool with both deployed, this RESURRECTS the old gateway (dual-launcher, LB2/FEA-7).
        //   }
        //   NOTE: the inline `await PublishAsync` sites (frmScanTab.cs:1819,1830,1838,1848,1873) block the
        //   scan-completion flow DIRECTLY; the :1888 hook is Task.Run-wrapped (blocks a pool thread).

        // AFTER (minimal wave-0 fix; full retirement at P1b):
        private static readonly System.TimeSpan GrpcDeadline = System.TimeSpan.FromSeconds(5);

        private void PushEvent(ScanEvent e)
        {
            try
            {
                var deadline = System.DateTime.UtcNow.Add(GrpcDeadline);
                // TODO: pass deadline to gRPC call options — never wait bare.
                _client.Push(e /*, deadline: deadline */);
            }
            catch
            {
                // Enqueue to dead-letter for a BACKGROUND retry thread — never sleep here.
                _deadLetterQueue.TryAdd(e);
                TryRestartGateway_Fixed();
            }
        }

        private void TryRestartGateway_Fixed()
        {
            // (d) Restart the CURRENT gateway the launcher actually uses — never the foreign
            // Fleet.ToolAPI path (which resurrects the old gateway):
            //   Process.Start(@"c:\bis\bin\x64\ToolGateway\ToolGateway.Endpoint.exe");
            // Best: don't spawn at all — call the ToolHost restart API (:5100 /control/restart-child/gateway),
            // which owns the gateway's lifecycle post-fabric. (This whole publisher retires at P1b.)
        }

        private object _client;
        private System.Collections.Concurrent.ConcurrentQueue<ScanEvent> _deadLetterQueue
            = new System.Collections.Concurrent.ConcurrentQueue<ScanEvent>();
    }

    internal sealed class ScanEvent { }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// LB3 — Fleet ToolId identity collapse (recommended for IMMEDIATE filing)
// File: Utilities\ToolGateway\ToolGateway.Endpoint\Services\FleetMainServerClientImpl.cs:115-125
// Bug: int.TryParse("BH01") → 0; every alphanumeric tool registers as ToolId 0
//      → fleet-wide collision (all alphanumeric tools appear as the same tool).
// ═══════════════════════════════════════════════════════════════════════════════════════

namespace ToolGateway.Endpoint.Services
{
    internal static class ToolIdFix
    {
        // BEFORE:
        //   int toolId = 0;
        //   int.TryParse(toolName, out toolId);  // silently 0 for "BH01"
        //   RegisterWithFleet(toolId);

        // AFTER: use the string identity directly; numeric id is a secondary opaque key.
        public static (string Identity, int NumericId) ResolveToolId(string toolName)
        {
            // The string identity is authoritative — never parse-and-lose alphanumeric names.
            int numericId = 0;
            int.TryParse(toolName, out numericId);

            return (
                Identity:  toolName,   // "BH01" — the real key for fleet registration
                NumericId: numericId   // 0 if non-numeric — only used if the API truly requires int
            );
        }

        // Call site change: pass Identity to Fleet gRPC registration, not NumericId.
        //   BEFORE: RegisterWithFleet(toolId);
        //   AFTER:  RegisterWithFleet(toolIdentity: toolName, numericId: numericId);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// LB4 — NonBlockingUITask timeout bug (recommended for IMMEDIATE filing)
// File: system\CamtekSystem\AsyncTask\NonBlockingUITask.cs:24,41
// Bug: timeout?.Milliseconds reads the .Milliseconds COMPONENT (0–999), NOT .TotalMilliseconds.
//      Any whole-second timeout (e.g. 3s, 30s) becomes 0 ms → spurious cancellation.
// ═══════════════════════════════════════════════════════════════════════════════════════

namespace CamtekSystem.AsyncTask
{
    internal static class NonBlockingUITask_Fix
    {
        // BEFORE (line 24 / 41):
        //   var timeoutMs = timeout?.Milliseconds;   // BUG: .Milliseconds component (0-999)
        //   if (!done.Wait(timeoutMs ?? -1)) ...

        // AFTER:
        //   var timeoutMs = (int?)timeout?.TotalMilliseconds;  // CORRECT: total duration in ms
        //   if (!done.Wait(timeoutMs ?? -1)) ...

        // This is the same fix pattern already applied in UiMarshaller (file 06).
        // Note: UiMarshaller is the REPLACEMENT for NonBlockingUITask — new code should use it.
        //       This fix is for any remaining call sites that still use NonBlockingUITask.

        public static int ToTimeoutMs(System.TimeSpan? timeout)
        {
            // Correct conversion: must be TotalMilliseconds, never .Milliseconds
            return timeout.HasValue ? (int)timeout.Value.TotalMilliseconds : -1;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// LB5 — No spool drain loop (restore-only-at-startup + TryWrite overflow)
// Files: ToolGateway.BL\Sinks\SinkDispatcher.cs
//        ToolGateway.BL\EventMessages\FailedMessagesHandler.cs:49-52, 108-109
// Bug: (a) Restore only at process start, capped at 10,000 — excess to a never-read overflow file.
//      (b) TryWrite into a 1,000-capacity channel re-spools ~9k of 10k (90% loss under backlog).
//      (c) No periodic drain loop — outage recovery never happens except on restart.
// Fix (at Wave 0, part of spool role change described in 12-gateway-additions.cs).
// ═══════════════════════════════════════════════════════════════════════════════════════

namespace ToolGateway.BL.Sinks
{
    internal static class SpoolDrainFix
    {
        // BEFORE (startup restore):
        //   var messages = ReadFromSpool(maxCount: 10_000);        // capped; excess lost
        //   foreach (var m in messages)
        //       _channel.Writer.TryWrite(m);                       // TryWrite drops at capacity!
        //   // No subsequent drain loop exists.

        // AFTER:
        public static System.Threading.Tasks.Task StartPeriodicDrainAsync(
            System.Threading.Channels.ChannelWriter<object> channel,
            string spoolDirectory,
            System.Threading.CancellationToken ct)
        {
            // §1.3.2: "periodic 60 s drain — oldest-first, interleaved with live traffic, capped rate"
            return System.Threading.Tasks.Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await DrainBatchAsync(channel, spoolDirectory, batchSize: 100, ct);
                    await System.Threading.Tasks.Task.Delay(
                        System.TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                }
            }, ct);
        }

        private static async System.Threading.Tasks.Task DrainBatchAsync(
            System.Threading.Channels.ChannelWriter<object> channel,
            string spoolDirectory,
            int batchSize,
            System.Threading.CancellationToken ct)
        {
            // Read oldest-first (by file creation time). Use WriteAsync with back-pressure
            // instead of TryWrite — never lose messages from the spool on channel full.
            // §6.9 load model: gateway channel 1000 (~30 min burst absorption at 60 wph).
            // At cap: wait until a slot is free (WriteAsync blocks) — do not overflow.
        }
    }
}
