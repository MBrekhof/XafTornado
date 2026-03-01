using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;

namespace XafTornado.Module.Services
{
    public record AILogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message);

    public sealed class AILogStore
    {
        private const int MaxEntries = 500;
        private readonly LinkedList<AILogEntry> _entries = new();
        private readonly object _lock = new();
        private static readonly string LogFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ai-debug.log");

        private static bool _logFileCleared;

        public event Action<AILogEntry> OnNewEntry;

        public void Add(AILogEntry entry)
        {
            lock (_lock)
            {
                _entries.AddLast(entry);
                while (_entries.Count > MaxEntries)
                    _entries.RemoveFirst();
            }

            // Write to disk for debugging — clear on first write each app run
            try
            {
                if (!_logFileCleared)
                {
                    File.WriteAllText(LogFilePath, string.Empty);
                    _logFileCleared = true;
                }

                var level = entry.Level.ToString().Substring(0, 3).ToUpperInvariant();
                var line = $"{entry.Timestamp:HH:mm:ss.fff} {level} {entry.Category,-15} {entry.Message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line);
            }
            catch { /* best effort */ }

            OnNewEntry?.Invoke(entry);
        }

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
            }
        }
    }
}
