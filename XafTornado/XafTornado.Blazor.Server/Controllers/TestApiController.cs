using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XafTornado.Module.BusinessObjects;
using XafTornado.Module.Services;

namespace XafTornado.Blazor.Server.Controllers
{
    /// <summary>
    /// Minimal REST API used by the XafTornado.Tests runner to execute AI tools
    /// and natural-language prompts directly against the running application.
    /// Only intended for development/testing — compiled out of Release builds:
    /// it is unauthenticated and signs Admin in for every request, so it
    /// only answers callers on the loopback interface (SEC-005).
    /// </summary>
#if DEBUG
    /// <summary>
    /// Rejects callers that are not on the loopback interface. The in-process test host
    /// (WebApplicationFactory) has no remote address and passes.
    /// </summary>
    public sealed class LoopbackOnlyAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var ip = context.HttpContext.Connection.RemoteIpAddress;
            if (ip != null && !IPAddress.IsLoopback(ip))
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }

    [ApiController]
    [Route("api/test")]
    [LoopbackOnly]
    public class TestApiController : ControllerBase, IDisposable
    {
        // AIChatService is scoped and an MVC request scope has no circuit, so each request gets a
        // fresh conversation. The runner needs continuity across "say" steps: keep the history
        // data (nothing else) per X-Test-Session header, replayed into the request's service.
        // ponytail: load/ask/store is not atomic; the runner sends one request at a time per key.
        private static readonly ConcurrentDictionary<string, List<AIChatService.ChatMessageEntry>> Sessions = new();

        private readonly AIToolsProvider _toolsProvider;
        private readonly AIChatService _chatService;

        private string SessionKey => Request.Headers["X-Test-Session"].FirstOrDefault() ?? "default";

        private readonly IDisposable _signIn;

        public TestApiController(AIToolsProvider toolsProvider, AIChatService chatService, NavigationRequestQueue navigation, IServiceProvider services)
        {
            _toolsProvider = toolsProvider;
            _chatService = chatService;
            // Tools read through the scope's secured object space (SEC-001): a request scope has no
            // user, so log Admin on here, the way the evals always ran.
            _signIn = SignIn(services, "Admin");
            // A request scope has no window to execute UI requests: acknowledge them so the evals
            // can assert on the tool trace (the YAML runner checks which tools were called, not the UI).
            navigation.OnRequest += () =>
            {
                while (navigation.TryDequeue(out var request))
                {
                    request.Outcome = NavigationResult.Success;
                    request.MarkDone();
                }
            };
        }

        /// <summary>
        /// Execute a named AI tool directly and return its raw text result.
        /// POST /api/test/tool
        /// Body: { "tool": "query_entity", "params": { "entityName": "Order", "top": 10 } }
        /// </summary>
        [HttpPost("tool")]
        public async Task<IActionResult> RunTool([FromBody] ToolRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Tool))
                return BadRequest(new { error = "Tool name is required." });

            var function = _toolsProvider.Tools.FirstOrDefault(f => f.Name == request.Tool);
            if (function == null)
            {
                var available = string.Join(", ", _toolsProvider.Tools.Select(f => f.Name));
                return BadRequest(new { error = $"Unknown tool: '{request.Tool}'. Available: {available}" });
            }

            try
            {
                // Serialize params back to JSON then deserialize as Dictionary<string, object>
                // so AIFunctionArguments gets JsonElement values — the same path AIChatService uses.
                var paramsJson = JsonSerializer.Serialize(request.Params ?? []);
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(paramsJson) ?? new();
                var args = new AIFunctionArguments(dict);
                var result = await function.InvokeAsync(args);
                return Ok(new ToolResponse(result?.ToString() ?? ""));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Send a natural-language prompt through the full AI tool loop and return the response
        /// plus the tool calls the model made this turn (for trace-based assertions).
        /// POST /api/test/ask
        /// Body: { "prompt": "How many orders are there?" }
        /// Response: { "result": "...", "toolCalls": [{ "name": "query_entity", "arguments": {...}, "result": "{...}" }] }
        /// </summary>
        [HttpPost("ask")]
        public async Task<IActionResult> Ask([FromBody] AskRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return BadRequest(new { error = "Prompt is required." });

            try
            {
                if (Sessions.TryGetValue(SessionKey, out var history))
                    _chatService.LoadHistory(history);
                var result = await _chatService.AskAsync(request.Prompt);
                Sessions[SessionKey] = _chatService.History.ToList();
                var toolCalls = _chatService.LastToolCalls.Select(c => new
                {
                    name = c.Name,
                    arguments = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments),
                    result = c.Result,
                }).ToList();
                return Ok(new { result, toolCalls });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Clear the AI conversation history (call between independent test runs).
        /// POST /api/test/clear
        /// </summary>
        [HttpPost("clear")]
        public IActionResult ClearHistory()
        {
            Sessions.TryRemove(SessionKey, out _);
            return Ok(new { cleared = true });
        }

        /// <summary>
        /// Logs <paramref name="userName"/> on in <paramref name="scope"/> (dxdocs "User Logon and
        /// Authentication", the nested-scope impersonation pattern). Returns the object space the
        /// user was loaded from; keep it alive while the scope's security is in use.
        /// </summary>
        public static IDisposable SignIn(IServiceProvider scope, string userName)
        {
            var os = scope.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace<ApplicationUser>();
            var user = scope.GetRequiredService<UserManager>().FindUserByName<ApplicationUser>(os, userName)
                ?? throw new InvalidOperationException($"No user '{userName}'.");
            scope.GetRequiredService<SignInManager>().SignIn(user);
            return os;
        }

        public void Dispose() => _signIn?.Dispose();

        public record ToolRequest(string Tool, Dictionary<string, JsonElement> Params);
        public record ToolResponse(string Result);
        public record AskRequest(string Prompt);
    }
#endif
}
