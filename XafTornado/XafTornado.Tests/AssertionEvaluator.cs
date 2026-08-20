using System.Text.RegularExpressions;
using XafTornado.Tests.Models;

namespace XafTornado.Tests;

public static class AssertionEvaluator
{
    public static (bool Passed, string Message) Evaluate(StepAssertions assertions, string result)
    {
        var failures = new List<string>();

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
