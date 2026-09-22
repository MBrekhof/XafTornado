using System;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.Logging;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Forwards the AI-related log categories to XAF's own trace log (eXpressAppFramework.log via
    /// <see cref="Tracing.Tracer"/>). WinForms has no console and registers no other provider, so
    /// without this an executor failure would vanish. Blazor keeps its console logger.
    /// </summary>
    public sealed class XafTracingLoggerProvider : ILoggerProvider
    {
        private static readonly string[] TrackedPrefixes =
        {
            "XafTornado.Module.Services.",
            "XafTornado.Module.Controllers.",
            "XafTornado.Win.Controllers.",
            "XafTornado.Blazor.Server.Controllers.",
        };

        public ILogger CreateLogger(string categoryName) =>
            Array.Exists(TrackedPrefixes, p => categoryName.StartsWith(p, StringComparison.Ordinal))
                ? new TracingLogger(categoryName[(categoryName.LastIndexOf('.') + 1)..])
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private sealed class TracingLogger(string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && Tracing.Tracer != null;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                var text = $"[{category}] {formatter(state, exception)}";
                if (exception != null)
                    text += $" | {exception.GetType().Name}: {exception.Message}";
                // One-to-one with XAF's verbosity gates: an errors-only trace keeps every Error with
                // its message, and a retry warning that carries an exception stays a warning.
                if (logLevel >= LogLevel.Error)
                    Tracing.Tracer.LogError(text);
                else if (logLevel == LogLevel.Warning)
                    Tracing.Tracer.LogWarning(text);
                else
                    Tracing.Tracer.LogText(text);
            }
        }
    }
}
