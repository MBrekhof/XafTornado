using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.Chat.Models;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using LlmTornado.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// One conversation: history, model, last tool calls. Scoped, so Blazor gets one per circuit
    /// and WinForms one per process (SEC-002). Only the <see cref="TornadoApi"/> is shared, via
    /// <see cref="TornadoApiProvider"/>.
    /// </summary>
    public sealed class AIChatService : IDisposable
    {
        private readonly AIOptions _options;
        private readonly TornadoApiProvider _apiProvider;
        private readonly ILogger<AIChatService> _logger;

        // Conversation history for continuity across messages
        private readonly List<ChatMessageEntry> _history = new();
        private const int MaxHistoryMessages = 50;

        // Two chat surfaces can share one circuit (AISidePanel + AIChat view item): one turn at a time.
        private readonly SemaphoreSlim _turnLock = new(1, 1);
        private CancellationTokenSource _turnCts;
        // Bumped by ClearHistory. A turn that straddles a bump (waiting for the lock, in flight, or
        // answered but not yet appended) yields nothing: the conversation it belonged to is gone.
        private int _generation;
        private string _model;

        private const string ResetMessage = "The conversation was reset.";

        /// <summary>Model for this conversation; defaults to <see cref="AIOptions.Model"/>.</summary>
        public string CurrentModel
        {
            get => _model ?? _options.Model;
            set => _model = value;
        }

        /// <summary>
        /// LLMTornado Tool definitions for the LLM to know what tools are available.
        /// </summary>
        public IReadOnlyList<Tool> TornadoTools { get; set; }

        /// <summary>
        /// AIFunction instances for executing tool calls by name.
        /// </summary>
        public IReadOnlyList<AIFunction> ToolFunctions { get; set; }

        /// <summary>
        /// Produces the system prompt for each turn. A factory rather than a string so the
        /// "current date and time" line is right on day two of an app run (AI-010).
        /// </summary>
        public Func<string> SystemPromptFactory { get; set; }

        /// <summary>One tool invocation made by the model during a turn.</summary>
        public sealed record ToolCall(string Name, string Arguments, string Result);

        private List<ToolCall> _lastToolCalls = new();

        /// <summary>Tool calls made during the most recent <see cref="AskAsync"/>, in order. Used by LLM evals.</summary>
        public IReadOnlyList<ToolCall> LastToolCalls => _lastToolCalls;

        /// <summary>Snapshot of the conversation so far (user/assistant pairs).</summary>
        public IReadOnlyList<ChatMessageEntry> History
        {
            get { lock (_history) return _history.ToList(); }
        }

        public AIChatService(TornadoApiProvider apiProvider, IOptions<AIOptions> optionsAccessor, ILogger<AIChatService> logger)
        {
            _apiProvider = apiProvider ?? throw new ArgumentNullException(nameof(apiProvider));
            _options = optionsAccessor?.Value ?? new AIOptions();
            _logger = logger;
        }

        public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            var api = _apiProvider.Api;
            var generation = Volatile.Read(ref _generation);

            await _turnLock.WaitAsync(cancellationToken);
            // AIOptions.TimeoutSeconds bounds the whole turn: every model round-trip plus every tool call (AI-008).
            var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                if (generation != Volatile.Read(ref _generation))
                    return ResetMessage;   // reset while this surface waited for the running turn
                _turnCts = turnCts;
                if (_options.TimeoutSeconds > 0)
                    turnCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
                return await AskCoreAsync(api, prompt, cancellationToken, turnCts, generation);
            }
            finally
            {
                _turnCts = null;
                turnCts.Dispose();
                _turnLock.Release();
            }
        }

        private async Task<string> AskCoreAsync(TornadoApi api, string prompt, CancellationToken cancellationToken, CancellationTokenSource turnCts, int generation)
        {
            // This turn's trace is a local: a Reset() mid-turn swaps _lastToolCalls for a fresh
            // list, and a late tool result must not land in the next conversation's trace.
            var calls = new List<ToolCall>();
            _lastToolCalls = calls;

            var model = CurrentModel;
            var provider = ResolveProvider(model);
            var attempt = new AttemptState();
            var pipeline = CreateRetryPipeline(attempt, model);
            int toolIterations = 0;

            ChatRichResponse response;
            try
            {
                response = await pipeline.ExecuteAsync(RunTurnAsync, turnCts.Token);
            }
            catch (OperationCanceledException) when (generation != Volatile.Read(ref _generation))
            {
                _logger.LogInformation("[AskAsync] Turn cancelled by a conversation reset");
                return ResetMessage;
            }
            catch (OperationCanceledException) when (turnCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("[AskAsync] Turn timed out after {Seconds}s ({Iterations} tool iterations)",
                    _options.TimeoutSeconds, toolIterations);
                // Tools that ran before the timeout may have committed: say so instead of inviting a replay.
                var ran = calls.Select(c => c.Name).Distinct().ToList();
                return ran.Count == 0
                    ? $"The AI request timed out after {_options.TimeoutSeconds} seconds. Please try again."
                    : $"The AI request timed out after {_options.TimeoutSeconds} seconds. {calls.Count} tool call(s) already ran " +
                      $"({string.Join(", ", ran)}); check the data before repeating a create or update.";
            }

            // Build conversation inside the retry lambda so a fresh Conversation is created on each attempt
            // (LlmTornado's Conversation is stateful and may be corrupted after a failure)
            async ValueTask<ChatRichResponse> RunTurnAsync(CancellationToken ct)
            {
                attempt.ToolsRan = false;
                var chatRequest = new ChatRequest
                {
                    Model = new ChatModel(model, provider),
                    MaxTokens = _options.MaxOutputTokens,
                    Temperature = 1.0
                };

                if (TornadoTools is { Count: > 0 })
                    chatRequest.Tools = TornadoTools.ToList();

                var conversation = api.Chat.CreateConversation(chatRequest);

                // System prompt
                var systemPrompt = SystemPromptFactory?.Invoke();
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    conversation.AppendSystemMessage(systemPrompt);

                // Replay conversation history for continuity
                var history = History;
                foreach (var entry in history)
                {
                    if (entry.Role == "user")
                        conversation.AppendUserInput(entry.Content);
                    else
                        conversation.AppendExampleChatbotOutput(entry.Content);
                }

                // Current user message
                conversation.AppendUserInput(prompt);

                _logger.LogInformation("[AskAsync] Sending (model={Model}, provider={Provider}, tools={Tools}, history={History})",
                    model, provider, TornadoTools?.Count ?? 0, history.Count);

                // GetResponseRich(fnHandler) populates tool results in the conversation
                // but does NOT automatically re-send to the LLM. We must loop manually:
                // call GetResponseRich(fnHandler), then if tools were called, call
                // GetResponseRich() again on the same conversation (which now includes
                // the tool results as messages) until no more tool calls are returned.
                ChatRichResponse richResponse = null;
                bool hasToolCalls = true;

                while (hasToolCalls && toolIterations < _options.MaxToolIterations)
                {
                    richResponse = await conversation.GetResponseRich(async functionCalls =>
                    {
                        // From here on this attempt may have side effects: never replay it (AI-002).
                        attempt.ToolsRan = true;
                        toolIterations++;
                        _logger.LogInformation("[ToolLoop] Iteration {Iter}: {Count} tool call(s)",
                            toolIterations, functionCalls.Count);

                        foreach (var fc in functionCalls)
                        {
                            ct.ThrowIfCancellationRequested();
                            var result = await ExecuteToolCoreAsync(fc.Name, fc.Arguments ?? "{}", ct);
                            calls.Add(new ToolCall(fc.Name, fc.Arguments ?? "{}", result));
                            _logger.LogInformation("[ToolLoop] {Name} → {ResultLen} chars", fc.Name, result.Length);
                            fc.Result = new FunctionResult(fc, result);
                        }
                    }, ct);

                    // Check if the response still contains unresolved tool calls
                    hasToolCalls = richResponse?.Blocks?.Any(b =>
                        b.Type == ChatRichResponseBlockTypes.Function && b.FunctionCall != null) == true;

                    if (hasToolCalls)
                        _logger.LogInformation("[ToolLoop] Response still has tool calls, continuing loop");
                }

                if (toolIterations >= _options.MaxToolIterations)
                    _logger.LogWarning("[ToolLoop] Hit max iterations ({Max})", _options.MaxToolIterations);

                return richResponse;
            }

            // Extract text from response
            var finalText = string.Empty;
            if (response?.Blocks != null)
            {
                var textParts = new List<string>();
                foreach (var block in response.Blocks)
                {
                    if (block.Type == ChatRichResponseBlockTypes.Message && block.Message != null)
                        textParts.Add(block.Message);
                }
                finalText = string.Join("\n", textParts);
            }

            // Fallback to simple text property
            if (string.IsNullOrEmpty(finalText) && response != null)
                finalText = response.Text ?? string.Empty;

            // Update conversation history
            lock (_history)
            {
                if (generation != _generation)
                {
                    // Reset landed between the model's answer and this append: the answer belongs
                    // to a conversation (or, in WinForms, a user) that no longer exists.
                    _logger.LogInformation("[AskAsync] Answer discarded: conversation was reset");
                    return ResetMessage;
                }
                _history.Add(new ChatMessageEntry("user", prompt));
                if (!string.IsNullOrEmpty(finalText))
                    _history.Add(new ChatMessageEntry("assistant", finalText));

                // Trim history to prevent unbounded growth
                while (_history.Count > MaxHistoryMessages * 2)
                {
                    _history.RemoveAt(0);
                    _history.RemoveAt(0); // Remove in pairs (user+assistant)
                }
            }

            // Log token usage if available
            if (response?.Usage != null)
            {
                _logger.LogInformation("[AskAsync] Tokens — input: {In}, output: {Out}",
                    response.Usage.PromptTokens, response.Usage.CompletionTokens);
            }

            _logger.LogInformation("[AskAsync] Response: {Len} chars, {Iterations} tool iterations",
                finalText.Length, toolIterations);

            return string.IsNullOrEmpty(finalText)
                ? "No response received from the AI model. Please try again."
                : finalText;
        }

        /// <summary>
        /// Clears conversation history (e.g. when user switches models) and cancels the turn in
        /// flight, if any, so it cannot append to the cleared history. Cancel rather than wait on
        /// the turn lock: this is called from the UI thread and a turn can run for TimeoutSeconds.
        /// </summary>
        public void ClearHistory()
        {
            lock (_history)
            {
                _generation++;
                try { _turnCts?.Cancel(); }
                catch (ObjectDisposedException) { /* the turn finished between the null check and Cancel */ }
                _history.Clear();
            }
        }

        /// <summary>Replaces the history (test API: continuity across stateless requests).</summary>
        public void LoadHistory(IEnumerable<ChatMessageEntry> entries)
        {
            lock (_history)
            {
                _history.Clear();
                _history.AddRange(entries);
            }
        }

        /// <summary>
        /// Back to a fresh conversation: history, model and tool trace. WinForms calls this on
        /// logoff because its application and DI scope outlive the user (SEC-002).
        /// </summary>
        public void Reset()
        {
            ClearHistory();
            _model = null;
            _lastToolCalls = new List<ToolCall>();
        }

        public async IAsyncEnumerable<string> AskStreamingAsync(
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // LLMTornado supports streaming but tool calls require the full
            // GetResponseRich() loop. Yield the complete response as one chunk.
            var response = await AskAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response))
                yield return response;
        }

        private async Task<string> ExecuteToolCoreAsync(string toolName, string argumentsJson, CancellationToken cancellationToken)
        {
            if (ToolFunctions == null) return "Error: No tools registered.";

            var function = ToolFunctions.FirstOrDefault(f => f.Name == toolName);
            if (function == null) return $"Error: Unknown tool '{toolName}'.";

            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(argumentsJson)
                    ?? new Dictionary<string, object>();

                var args = new AIFunctionArguments(dict);
                var result = await function.InvokeAsync(args, cancellationToken);
                return result?.ToString() ?? "Tool returned no result.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ExecuteTool] {Name} failed", toolName);
                return $"Error executing {toolName}: {ex.Message}";
            }
        }

        /// <summary>Per-turn state the retry predicate consults; reset at the start of every attempt.</summary>
        private sealed class AttemptState
        {
            public bool ToolsRan;
        }

        private ResiliencePipeline CreateRetryPipeline(AttemptState attempt, string model)
        {
            return new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(2),
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    {
                        // A tool may have committed (create_entity/update_entity): replaying the turn
                        // would let the model do it twice (AI-002). Surface the failure instead.
                        if (attempt.ToolsRan) return false;
                        // User stop or turn timeout is final, not a transient fault (AI-008).
                        if (ex is OperationCanceledException) return false;
                        if (ex is HttpRequestException httpEx)
                        {
                            var status = (int)(httpEx.StatusCode ?? 0);
                            return status == 429 || status >= 500;
                        }
                        return false;
                    }),
                    OnRetry = args =>
                    {
                        _logger.LogWarning(args.Outcome.Exception,
                            "[Retry] Attempt {Attempt}/3 for model {Model}, retrying in {Delay:F1}s",
                            args.AttemptNumber + 1, model, args.RetryDelay.TotalSeconds);
                        return ValueTask.CompletedTask;
                    }
                })
                .Build();
        }

        private LLmProviders ResolveProvider(string modelId)
        {
            if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Anthropic;
            if (modelId.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
            if (modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
            if (modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase)) return LLmProviders.OpenAi;
            if (modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Google;
            if (modelId.StartsWith("mistral", StringComparison.OrdinalIgnoreCase)) return LLmProviders.Mistral;

            return TornadoApiProvider.MapProvider(_options.DefaultProvider) ?? LLmProviders.Anthropic;
        }

        public void Dispose()
        {
            _turnLock.Dispose();
        }

        /// <summary>One history entry; <c>Role</c> is "user" or "assistant".</summary>
        public sealed record ChatMessageEntry(string Role, string Content);
    }
}
