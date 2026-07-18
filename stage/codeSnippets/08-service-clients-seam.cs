// Realizes: §2.5 Component: ServiceClients (the Connect() seam)
// Project: apps\Falcon.Net\AOI_Main (net48, C# 7.3)
// Responsibility: typed gRPC proxies for SVC-lane services behind the connector seam.
//                Call sites keep their interfaces; a per-service config flag selects
//                the transport (rollback lever).
// Constraint: C# 7.3 / net48-compatible syntax.

using System;
using Grpc.Core; // Grpc.Core still used in net48; migrate to Grpc.Net.Client in net8/gateway

namespace Falcon.Net.Services
{
    // ═══ The seam pattern (reproduced exactly from §2.5) ════════════════════════════
    // §2.5: "one swap point per service (pattern; limits in lane B, doc 03)"

    // Example: SystemLogger — the pilot service.
    public static class SystemLoggerConnector
    {
        public static ISystemLogger Instance { get { return _lazy.Value; } }

        private static readonly Lazy<ISystemLogger> _lazy = new Lazy<ISystemLogger>(() =>
        {
            switch (ToolConfig.ServiceTransport("SystemLogger"))  // "grpc" | "rot"
            {
                case "grpc":
                    return new SystemLoggerGrpcProxy(          // mandatory client policy:
                        deadline:  TimeSpan.FromSeconds(3),    //   per-call deadline,
                        breaker:   CircuitBreaker.Default,     //   fail-fast when down,
                        degraded:  LocalFileLoggerFallback.Instance); // defined fallback
                default:
                    return new SystemLoggerRotProxy();         // legacy ROT COM path
            }
        });
    }

    // ═══ Why the failure policy is mandatory (§2.5) ══════════════════════════════════
    // "Without deadlines + breaker + fallback, the migration would convert today's
    //  fail-fast COM errors into 30-second UI freezes."
    // Every ServiceClients connector MUST wire these three. No exceptions.

    // ═══ Interface — stable; no change for either transport path ════════════════════

    public interface ISystemLogger
    {
        void LogInfo   (string message, string correlationId = null);
        void LogWarning(string message, string correlationId = null);
        void LogError  (string message, Exception ex = null, string correlationId = null);
    }

    // ═══ gRPC proxy — with mandatory client policy ══════════════════════════════════

    internal sealed class SystemLoggerGrpcProxy : ISystemLogger
    {
        private readonly TimeSpan        _deadline;
        private readonly CircuitBreaker  _breaker;
        private readonly ISystemLogger   _fallback;
        private readonly Channel         _channel; // Grpc.Core channel (net48)

        public SystemLoggerGrpcProxy(TimeSpan deadline, CircuitBreaker breaker, ISystemLogger fallback)
        {
            _deadline = deadline;
            _breaker  = breaker;
            _fallback = fallback;
            // §2.5: connect to ToolServices host :5060 (§1.4 ports)
            _channel  = new Channel("localhost:5060", ChannelCredentials.Insecure);
        }

        public void LogInfo(string message, string correlationId = null)
            => Execute(() => CallGrpc(message, "INFO", correlationId), message, correlationId);

        public void LogWarning(string message, string correlationId = null)
            => Execute(() => CallGrpc(message, "WARN", correlationId), message, correlationId);

        public void LogError(string message, Exception ex = null, string correlationId = null)
            => Execute(() => CallGrpc(message, "ERROR", correlationId), message, correlationId);

        private void Execute(Action grpcCall, string message, string correlationId)
        {
            if (!_breaker.IsAllowed)
            {
                _fallback.LogInfo(message, correlationId); // breaker open — fallback immediately
                return;
            }
            try
            {
                var deadline = DateTime.UtcNow.Add(_deadline);
                // TODO: call through the gRPC stub with CallOptions(deadline: deadline)
                grpcCall();
                _breaker.RecordSuccess();
            }
            catch
            {
                _breaker.RecordFailure();
                _fallback.LogInfo(message, correlationId); // defined fallback, not a stack melt
                // Note: swallow after fallback — log errors in the logger itself must not cascade
            }
        }

        private void CallGrpc(string message, string level, string correlationId)
        {
            // TODO: invoke generated stub — deadline set in CallOptions
        }
    }

    // ═══ Legacy ROT proxy ════════════════════════════════════════════════════════════

    internal sealed class SystemLoggerRotProxy : ISystemLogger
    {
        // Existing COM/ROT path — no change; this is the rollback path.
        public void LogInfo   (string m, string c = null) { /* existing impl */ }
        public void LogWarning(string m, string c = null) { /* existing impl */ }
        public void LogError  (string m, Exception e = null, string c = null) { /* existing impl */ }
    }

    // ═══ Fallback (net48 local file — used when breaker is open or gRPC is down) ════

    internal sealed class LocalFileLoggerFallback : ISystemLogger
    {
        public static readonly LocalFileLoggerFallback Instance = new LocalFileLoggerFallback();
        private LocalFileLoggerFallback() { }

        public void LogInfo   (string m, string c = null) => AppendToFallbackFile("INFO",  m, c);
        public void LogWarning(string m, string c = null) => AppendToFallbackFile("WARN",  m, c);
        public void LogError  (string m, Exception e = null, string c = null)
            => AppendToFallbackFile("ERROR", m + (e != null ? " | " + e.Message : ""), c);

        private void AppendToFallbackFile(string level, string msg, string cid)
        {
            // Fallback: append-only to %LOCALAPPDATA%\Camtek\fallback.log
            // Size-capped; never throws.
        }
    }

    // ═══ Infrastructure stubs ════════════════════════════════════════════════════════

    public sealed class CircuitBreaker
    {
        public static readonly CircuitBreaker Default = new CircuitBreaker();
        public bool IsAllowed   { get { return true; /* TODO: implement half-open FSM */ } }
        public void RecordSuccess() { }
        public void RecordFailure() { }
    }

    public static class ToolConfig
    {
        // Reads from the endpoint manifest (§1.4, §1.3.3 — ToolHost-owned manifest).
        public static string ServiceTransport(string serviceName)
        {
            // TODO: read from toolbus.json / endpoint manifest
            return "rot"; // default to legacy until flipped
        }

        public static string ModuleMode(string moduleName)
        {
            // TODO: read from toolbus.json — "inproc" or "rot"
            return "rot"; // default to out-of-proc (ROT) until P2 flag flipped
        }
    }
}
