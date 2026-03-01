using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace XafTornado.Module.Services
{
    /// <summary>
    /// Adapter that wraps <see cref="AIChatService"/> as an
    /// <see cref="IChatClient"/> so the DevExpress AI infrastructure
    /// (<c>DxAIChat</c>, <c>AIChatControl</c>) can route messages
    /// through LLMTornado automatically.
    /// </summary>
    public sealed class AIChatClient : IChatClient
    {
        private readonly AIChatService _service;

        public AIChatClient(AIChatService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public ChatClientMetadata Metadata => new("LLMTornado");

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var lastUserMessage = chatMessages.LastOrDefault(m => m.Role == ChatRole.User);
            var prompt = lastUserMessage?.Text ?? string.Empty;

            var response = await _service.AskAsync(prompt, cancellationToken);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var lastUserMessage = chatMessages.LastOrDefault(m => m.Role == ChatRole.User);
            var prompt = lastUserMessage?.Text ?? string.Empty;

            await foreach (var chunk in _service.AskStreamingAsync(prompt, cancellationToken).ConfigureAwait(false))
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(chunk)]
                };
            }
        }

        public object GetService(Type serviceType, object serviceKey = null)
            => serviceType == typeof(AIChatClient) ? this : null;

        public void Dispose() { }
    }
}
