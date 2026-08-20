namespace XafTornado.Tests.Models;

/// <summary>Top-level YAML test script.</summary>
public class TestScript
{
    public string Name { get; set; } = "Unnamed Test";

    /// <summary>Base URL of the running XafTornado Blazor app.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>Clear AI conversation history before the first step.</summary>
    public bool ClearHistory { get; set; } = true;

    public List<TestStep> Steps { get; set; } = new();
}

/// <summary>
/// A single test step. Exactly one of <see cref="Tool"/>, <see cref="Say"/>,
/// or <see cref="Playwright"/> must be set.
/// </summary>
public class TestStep
{
    /// <summary>Human-readable label shown in test output.</summary>
    public string? Name { get; set; }

    // -- Step type discriminators ------------------------------------------

    /// <summary>Call a named AI tool directly (deterministic). E.g. "query_entity".</summary>
    public string? Tool { get; set; }

    /// <summary>Send a natural-language prompt through the full AI loop.</summary>
    public string? Say { get; set; }

    /// <summary>Playwright action (Phase 2). E.g. "navigate", "click", "fill".</summary>
    public string? Playwright { get; set; }

    // -- Parameters --------------------------------------------------------

    /// <summary>Parameters passed to the tool (for <see cref="Tool"/> steps).</summary>
    public Dictionary<string, object>? Params { get; set; }

    // -- Playwright-specific params ----------------------------------------
    public string? Selector { get; set; }
    public string? Url { get; set; }
    public string? Value { get; set; }

    // -- Assertions --------------------------------------------------------
    public StepAssertions? Assert { get; set; }

    /// <summary>
    /// Capture the step result under this key for use in later steps
    /// via <c>{key}</c> interpolation.
    /// </summary>
    public string? Capture { get; set; }

    public string StepType =>
        Tool != null ? "tool" :
        Say  != null ? "say"  :
        Playwright != null ? "playwright" :
        "unknown";
}

public class StepAssertions
{
    /// <summary>Assert on the record count. E.g. "&gt;= 1", "== 5", "&lt; 10".</summary>
    public string? Count { get; set; }

    /// <summary>Result text must contain this string (case-insensitive).</summary>
    public string? Contains { get; set; }

    /// <summary>Result text must NOT contain this string (case-insensitive).</summary>
    public string? NotContains { get; set; }

    /// <summary>Result text must start with this string (case-insensitive).</summary>
    public string? StartsWith { get; set; }

    /// <summary>Result text must match this regex pattern.</summary>
    public string? Matches { get; set; }

    /// <summary>
    /// For <c>say</c> steps: tools the model must have called this turn (any order).
    /// Each entry: <c>- tool: query_entity</c>, optionally <c>with: { entityName: Order }</c> —
    /// every listed argument must appear in the call (strings: case-insensitive contains, <c>/regex/</c> supported).
    /// Prefer this over text assertions — wording drifts, behaviour is the contract.
    /// </summary>
    public List<ToolCallAssertion>? Called { get; set; }

    /// <summary>For <c>say</c> steps: tool names that must NOT have been called this turn.</summary>
    public List<string>? NotCalled { get; set; }
}

public class ToolCallAssertion
{
    public string Tool { get; set; } = "";
    public Dictionary<string, object>? With { get; set; }
}

/// <summary>A tool call reported by <c>/api/test/ask</c>.</summary>
public sealed record ToolCallInfo(string Name, System.Text.Json.JsonElement Arguments);
