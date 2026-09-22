using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace XafTornado.Module.Services
{
    public record AILogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message);

    /// <summary>
    /// The AI trace one user sees in the log panel: their own turns and tool calls, nothing else.
    /// Scoped like <see cref="AIChatService"/> and <see cref="AIToolsProvider"/>, which write it
    /// (one per Blazor circuit, one per WinForms process), so the panel can no longer show another
    /// user's record values (SEC-004). Console logging is unaffected.
    /// </summary>
    public sealed class AILogScope
    {
        private const int MaxEntries = 500;
        private readonly LinkedList<AILogEntry> _entries = new();
        private readonly object _lock = new();

        public event Action<AILogEntry> OnNewEntry;

        /// <summary>Bumped by <see cref="Clear"/>; a writer that started before a clear must not add.</summary>
        public int Generation { get; private set; }

        /// <summary>Adds unless the scope was cleared since <paramref name="generation"/> was read.</summary>
        public void Add(int generation, LogLevel level, string category, string message)
        {
            lock (_lock)
            {
                if (generation != Generation) return;   // written for a conversation that is gone (WinForms logoff)
            }
            Add(level, category, message);
        }

        public void Add(AILogEntry entry)
        {
            lock (_lock)
            {
                _entries.AddLast(entry);
                while (_entries.Count > MaxEntries)
                    _entries.RemoveFirst();
            }

            OnNewEntry?.Invoke(entry);
        }

        public void Add(LogLevel level, string category, string message) =>
            Add(new AILogEntry(DateTime.Now, level, category, message));

        public List<AILogEntry> GetEntries()
        {
            lock (_lock)
            {
                return new List<AILogEntry>(_entries);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                Generation++;
            }
        }
    }
}
