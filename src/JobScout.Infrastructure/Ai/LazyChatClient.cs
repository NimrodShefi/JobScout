using Microsoft.Extensions.AI;

namespace JobScout.Infrastructure.Ai;

/// <summary>Defers building the real chat client until something actually asks the model
/// a question.
///
/// Without this, resolving any service that transitively needs <see cref="IJobMatcher"/>
/// would construct the provider client, and a missing API key would fail the whole run -
/// even a run that never needed the AI, such as an email check with no mailbox configured.
/// With it, the failure lands only on the call that genuinely required a model, carrying
/// the same clear message.</summary>
public sealed class LazyChatClient(Func<IChatClient> factory) : IChatClient
{
    private readonly Lazy<IChatClient> _inner = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.Value.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.Value.GetStreamingResponseAsync(messages, options, cancellationToken);

    /// <summary>Asking for a service must not force construction - callers use this to probe
    /// for optional features, and probing is not a reason to demand an API key.</summary>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
            return this;

        return _inner.IsValueCreated ? _inner.Value.GetService(serviceType, serviceKey) : null;
    }

    public void Dispose()
    {
        if (_inner.IsValueCreated)
            _inner.Value.Dispose();
    }
}
