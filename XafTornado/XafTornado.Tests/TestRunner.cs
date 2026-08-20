using System.Net.Http.Json;
using System.Text.Json;
using XafTornado.Tests.Models;

namespace XafTornado.Tests;

public class TestRunner
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _captures = new();
    private List<ToolCallInfo> _lastToolCalls = new();
    private int _passed;
    private int _failed;

    public TestRunner(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task<bool> RunAsync(TestScript script)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"  {script.Name}");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        _passed = 0;
        _failed = 0;
        _captures.Clear();

        if (script.ClearHistory)
            await ClearHistoryAsync();

        for (var i = 0; i < script.Steps.Count; i++)
        {
            var step = script.Steps[i];
            var stepName = step.Name ?? $"Step {i + 1}";

            Console.Write($"  [{i + 1}/{script.Steps.Count}] {stepName} ... ");

            string? result = null;
            string? error = null;
            _lastToolCalls = new();

            try
            {
                result = step.StepType switch
                {
                    "tool"       => await RunToolStepAsync(step),
                    "say"        => await RunSayStepAsync(step),
                    "playwright" => RunPlaywrightStep(step),
                    _            => throw new InvalidOperationException(
                                        "Step must have 'tool', 'say', or 'playwright' key.")
                };

                if (!string.IsNullOrEmpty(step.Capture) && result != null)
                    _captures[step.Capture] = result;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error != null)
            {
                WriteColored("FAIL", ConsoleColor.Red);
                Console.WriteLine($"       Error: {error}");
                _failed++;
                continue;
            }

            if (step.Assert != null && result != null)
            {
                var (passed, message) = AssertionEvaluator.Evaluate(step.Assert, result, _lastToolCalls);
                if (passed)
                {
                    WriteColored("PASS", ConsoleColor.Green);
                    _passed++;
                }
                else
                {
                    WriteColored("FAIL", ConsoleColor.Red);
                    Console.WriteLine($"       Assertion: {message}");
                    Console.WriteLine($"       Result:    {result.Truncate(300)}");
                    if (_lastToolCalls.Count > 0)
                        Console.WriteLine($"       Calls:     {string.Join(" -> ", _lastToolCalls.Select(c => $"{c.Name}{c.Arguments.GetRawText()}"))}");
                    _failed++;
                }
            }
            else
            {
                WriteColored("OK  ", ConsoleColor.Cyan);
                _passed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 60));
        var allPassed = _failed == 0;
        Console.ForegroundColor = allPassed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  Result: {_passed} passed, {_failed} failed");
        Console.ResetColor();
        Console.WriteLine(new string('─', 60));
        Console.WriteLine();

        return allPassed;
    }

    private async Task<string> RunToolStepAsync(TestStep step)
    {
        var paramsWithCaptures = InterpolateCaptures(step.Params);
        var payload = new { tool = step.Tool, @params = paramsWithCaptures };

        var response = await _http.PostAsJsonAsync("/api/test/tool", payload);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (!response.IsSuccessStatusCode)
        {
            var errMsg = json.TryGetProperty("error", out var err)
                ? err.GetString()
                : response.ReasonPhrase;
            throw new Exception(errMsg ?? "HTTP error");
        }

        return json.GetProperty("result").GetString() ?? "";
    }

    private async Task<string> RunSayStepAsync(TestStep step)
    {
        var prompt = InterpolateString(step.Say!);
        var payload = new { prompt };

        var response = await _http.PostAsJsonAsync("/api/test/ask", payload);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (!response.IsSuccessStatusCode)
        {
            var errMsg = json.TryGetProperty("error", out var err)
                ? err.GetString()
                : response.ReasonPhrase;
            throw new Exception(errMsg ?? "HTTP error");
        }

        if (json.TryGetProperty("toolCalls", out var calls))
            _lastToolCalls = calls.EnumerateArray()
                .Select(c => new ToolCallInfo(c.GetProperty("name").GetString() ?? "", c.GetProperty("arguments").Clone()))
                .ToList();

        return json.GetProperty("result").GetString() ?? "";
    }

    private static string RunPlaywrightStep(TestStep step) =>
        $"[playwright:{step.Playwright}] not yet implemented";

    private async Task ClearHistoryAsync()
    {
        try { await _http.PostAsync("/api/test/clear", null); }
        catch { /* non-fatal */ }
    }

    private Dictionary<string, object>? InterpolateCaptures(Dictionary<string, object>? original)
    {
        if (original == null) return null;
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in original)
            result[key] = value is string s ? InterpolateString(s) : value;
        return result;
    }

    private string InterpolateString(string s)
    {
        foreach (var (key, value) in _captures)
            s = s.Replace($"{{{key}}}", value);
        return s;
    }

    private static void WriteColored(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
