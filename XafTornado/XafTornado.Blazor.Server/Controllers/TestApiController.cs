using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using XafTornado.Module.Services;

namespace XafTornado.Blazor.Server.Controllers
{
    /// <summary>
    /// Minimal REST API used by the XafTornado.Tests runner to execute AI tools
    /// and natural-language prompts directly against the running application.
    /// Only intended for development/testing — compiled out of Release builds:
    /// it is unauthenticated and writes through a non-secured ObjectSpace.
    /// </summary>
#if DEBUG
    [ApiController]
    [Route("api/test")]
    public class TestApiController : ControllerBase
    {
        private readonly AIToolsProvider _toolsProvider;
        private readonly AIChatService _chatService;

        public TestApiController(AIToolsProvider toolsProvider, AIChatService chatService)
        {
            _toolsProvider = toolsProvider;
            _chatService = chatService;
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
                var result = await _chatService.AskAsync(request.Prompt);
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
            _chatService.ClearHistory();
            return Ok(new { cleared = true });
        }

        public record ToolRequest(string Tool, Dictionary<string, JsonElement> Params);
        public record ToolResponse(string Result);
        public record AskRequest(string Prompt);
    }
#endif
}
