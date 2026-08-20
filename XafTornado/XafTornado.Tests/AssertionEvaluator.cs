using System.Text.RegularExpressions;
using XafTornado.Tests.Models;

namespace XafTornado.Tests;

public static class AssertionEvaluator
{
    public static (bool Passed, string Message) Evaluate(StepAssertions assertions, string result,
        IReadOnlyList<ToolCallInfo>? toolCalls = null)
    {
        var failures = new List<string>();
        var calls = toolCalls ?? Array.Empty<ToolCallInfo>();

        foreach (var expected in assertions.Called ?? new())
        {
            var candidates = calls.Where(c => c.Name.Equals(expected.Tool, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0)
                failures.Add($"Expected tool '{expected.Tool}' to be called; called: [{string.Join(", ", calls.Select(c => c.Name))}]");
            else if (expected.With is { Count: > 0 } && !candidates.Any(c => ArgumentsMatch(c.Arguments, expected.With)))
                failures.Add($"Tool '{expected.Tool}' was called but never with {FormatWith(expected.With)}; actual: {string.Join(" | ", candidates.Select(c => c.Arguments.GetRawText()))}");
        }

        foreach (var forbidden in assertions.NotCalled ?? new())
        {
            if (calls.Any(c => c.Name.Equals(forbidden, StringComparison.OrdinalIgnoreCase)))
                failures.Add($"Expected tool '{forbidden}' NOT to be called");
        }

        if (!string.IsNullOrEmpty(assertions.Count))
        {
            var (passed, msg) = EvaluateCount(assertions.Count, result);
            if (!passed) failures.Add(msg);
        }

        if (!string.IsNullOrEmpty(assertions.Contains))
        {
            if (!result.Contains(assertions.Contains, StringComparison.OrdinalIgnoreCase))
                failures.Add($"Expected result to contain '{assertions.Contains}'");
        }

        if (!string.IsNullOrEmpty(assertions.NotContains))
        {
            if (result.Contains(assertions.NotContains, StringComparison.OrdinalIgnoreCase))
                failures.Add($"Expected result NOT to contain '{assertions.NotContains}'");
        }

        if (!string.IsNullOrEmpty(assertions.StartsWith))
        {
            if (!result.TrimStart().StartsWith(assertions.StartsWith, StringComparison.OrdinalIgnoreCase))
                failures.Add($"Expected result to start with '{assertions.StartsWith}'");
        }

        if (!string.IsNullOrEmpty(assertions.Matches))
        {
            if (!Regex.IsMatch(result, assertions.Matches, RegexOptions.IgnoreCase))
                failures.Add($"Expected result to match pattern '{assertions.Matches}'");
        }

        return failures.Count == 0
            ? (true, "All assertions passed")
            : (false, string.Join("; ", failures));
    }

    /// <summary>Every expected argument must be present; strings match case-insensitively by contains, others by text equality; <c>/pattern/</c> is a regex.</summary>
    private static bool ArgumentsMatch(System.Text.Json.JsonElement actual, Dictionary<string, object> expected)
    {
        if (actual.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (var (key, value) in expected)
        {
            var prop = actual.EnumerateObject().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined) return false;
            var actualText = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
            var expectedText = value?.ToString() ?? "";
            var ok = expectedText.Length > 2 && expectedText.StartsWith('/') && expectedText.EndsWith('/')
                ? Regex.IsMatch(actualText, expectedText[1..^1], RegexOptions.IgnoreCase)
                : prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? actualText.Contains(expectedText, StringComparison.OrdinalIgnoreCase)
                    : actualText.Equals(expectedText, StringComparison.OrdinalIgnoreCase);
            if (!ok) return false;
        }
        return true;
    }

    private static string FormatWith(Dictionary<string, object> with) =>
        "{" + string.Join(", ", with.Select(kv => $"{kv.Key}: {kv.Value}")) + "}";

    private static (bool Passed, string Message) EvaluateCount(string assertion, string result)
    {
        var actual = ExtractCount(result);
        var parsed = ParseCountAssertion(assertion.Trim());

        if (parsed == null)
            return (false, $"Cannot parse count assertion '{assertion}'");

        var (op, expected) = parsed.Value;
        var passed = op switch
        {
            ">=" => actual >= expected,
            "<=" => actual <= expected,
            "==" => actual == expected,
            "!="  => actual != expected,
            ">"  => actual > expected,
            "<"  => actual < expected,
            _    => false
        };

        return passed
            ? (true, $"count {actual} {op} {expected}")
            : (false, $"count assertion failed: got {actual}, expected {op} {expected}");
    }

    private static (string Op, int Expected)? ParseCountAssertion(string assertion)
    {
        // Accepts: ">= 1", "== 5", "< 10", "> 0", "!= 0" or bare integer "3"
        var match = Regex.Match(assertion, @"^(>=|<=|==|!=|>|<)\s*(\d+)$");
        if (match.Success)
            return (match.Groups[1].Value, int.Parse(match.Groups[2].Value));

        if (int.TryParse(assertion, out var n))
            return ("==", n);

        return null;
    }

    private static int ExtractCount(string result)
    {
        // JSON tool result: {"entity":"Order","count":N,...}
        var j = Regex.Match(result, @"""count""\s*:\s*(\d+)");
        if (j.Success) return int.Parse(j.Groups[1].Value);

        // "Found N record(s):"
        var m = Regex.Match(result, @"Found (\d+) \w.*?record", RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value);

        // "No X records found" → 0
        if (Regex.IsMatch(result, @"No \w+ records found", RegexOptions.IgnoreCase)) return 0;

        // Fall back: count non-empty lines after the first
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Math.Max(0, lines.Length - 1);
    }
}
