using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace XafTornado.Module.Services
{
    public sealed class AILoggerProvider : ILoggerProvider
    {
        private static readonly Dictionary<string, string> TrackedCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            [typeof(AIToolsProvider).FullName] = "Tools",
            [typeof(AIChatService).FullName] = "ChatService",
            ["XafTornado.Blazor.Server.Controllers.NavigationExecutorController"] = "NavExecutor",
            ["XafTornado.Win.Controllers.WinNavigationExecutorController"] = "WinNavExec",
            ["XafTornado.Win.Controllers.AISidePanelController"] = "SidePanel",
            ["XafTornado.Module.Controllers.ActiveViewTrackingController"] = "ViewTracker",
        };

        private readonly AILogStore _store;

        public AILoggerProvider(AILogStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public ILogger CreateLogger(string categoryName)
        {
            if (TrackedCategories.TryGetValue(categoryName, out var shortName))
                return new AILogger(_store, shortName);

            return NullLogger.Instance;
        }

        public void Dispose() { }

        private sealed class AILogger : ILogger
        {
            private readonly AILogStore _store;
            private readonly string _category;

            public AILogger(AILogStore store, string category)
            {
                _store = store;
                _category = category;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var message = formatter(state, exception);

                // Filter out AI SDK internal trace noise — these flood the log
                // with hundreds of "[LoggerTraceSource]" entries per request.
                if (_category == "ChatService" && message.StartsWith("[LoggerTraceSource]", StringComparison.Ordinal))
                    return;

                if (exception != null)
                    message += $" | {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}";

                _store.Add(new AILogEntry(DateTime.Now, logLevel, _category, message));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        private sealed class NullLogger : ILogger
        {
            public static readonly NullLogger Instance = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => false;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
