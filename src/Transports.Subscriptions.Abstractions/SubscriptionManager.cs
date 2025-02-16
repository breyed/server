using System.Collections;
using System.Collections.Concurrent;
using GraphQL.Server.Transports.Subscriptions.Abstractions.Internal;
using GraphQL.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GraphQL.Server.Transports.Subscriptions.Abstractions;

/// <inheritdoc />
public class SubscriptionManager : ISubscriptionManager, IDisposable
{
    private readonly IDocumentExecuter _executer;

    private readonly ILogger<SubscriptionManager> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private volatile bool _disposed;

    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    public SubscriptionManager(IDocumentExecuter executer, ILoggerFactory loggerFactory, IServiceScopeFactory serviceScopeFactory)
    {
        _executer = executer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SubscriptionManager>();
        _serviceScopeFactory = serviceScopeFactory;
    }

    public Subscription this[string id] => _subscriptions[id];

    public IEnumerator<Subscription> GetEnumerator() => _subscriptions.Values.GetEnumerator();

    /// <inheritdoc />
    public async Task SubscribeOrExecuteAsync(
        string id,
        GraphQLRequest payload,
        MessageHandlingContext context)
    {
        if (id == null)
            throw new ArgumentNullException(nameof(id));
        if (payload == null)
            throw new ArgumentNullException(nameof(payload));
        if (context == null)
            throw new ArgumentNullException(nameof(context));
        if (_disposed)
            throw new ObjectDisposedException(nameof(SubscriptionManager));

        var subscription = await ExecuteAsync(id, payload, context).ConfigureAwait(false);

        if (subscription == null)
            return;

        if (_disposed)
            subscription.Dispose();
        else
            _subscriptions[id] = subscription;
    }

    /// <inheritdoc />
    public Task UnsubscribeAsync(string id)
    {
        if (_subscriptions.TryRemove(id, out var removed))
            return removed.UnsubscribeAsync();

        _logger.LogDebug("Subscription: {subscriptionId} unsubscribed", id);
        return Task.CompletedTask;
    }

    IEnumerator IEnumerable.GetEnumerator() => _subscriptions.Values.GetEnumerator();

    private async Task<Subscription> ExecuteAsync(
        string id,
        GraphQLRequest payload,
        MessageHandlingContext context)
    {
        var writer = context.Writer;
        _logger.LogDebug("Executing operation: {operationName} query: {query}",
            payload.OperationName,
            payload.Query);

        ExecutionResult result;
        using (var scope = _serviceScopeFactory.CreateScope())
        {
            result = await _executer.ExecuteAsync(new ExecutionOptions
            {
                Query = payload.Query,
                OperationName = payload.OperationName,
                Variables = payload.Variables,
                Extensions = payload.Extensions,
                RequestServices = scope.ServiceProvider,
                UserContext = context,
            }).ConfigureAwait(false);
        }

        if (result.Errors != null && result.Errors.Any())
        {
            _logger.LogError("Execution errors: {errors}", ResultHelper.GetErrorString(result));
            await writer.SendAsync(new OperationMessage
            {
                Type = MessageType.GQL_ERROR,
                Id = id,
                Payload = result
            }).ConfigureAwait(false);

            return null;
        }

        // is sub
        if (result.Streams != null)
            using (_logger.BeginScope("Subscribing to: {subscriptionId}", id))
            {
                if (result.Streams?.Values.SingleOrDefault() == null)
                {
                    _logger.LogError("Cannot subscribe as no result stream available");
                    await writer.SendAsync(new OperationMessage
                    {
                        Type = MessageType.GQL_ERROR,
                        Id = id,
                        Payload = result
                    }).ConfigureAwait(false);

                    return null;
                }

                _logger.LogDebug("Creating subscription");
                return new Subscription(
                    id,
                    payload,
                    result,
                    writer,
                    sub => _subscriptions.TryRemove(id, out _),
                    _loggerFactory.CreateLogger<Subscription>());
            }

        //is query or mutation
        await writer.SendAsync(new OperationMessage
        {
            Type = MessageType.GQL_DATA,
            Id = id,
            Payload = result
        }).ConfigureAwait(false);

        await writer.SendAsync(new OperationMessage
        {
            Type = MessageType.GQL_COMPLETE,
            Id = id
        }).ConfigureAwait(false);

        return null;
    }

    public virtual void Dispose()
    {
        _disposed = true;
        while (_subscriptions.Count > 0)
        {
            var subscriptions = _subscriptions.ToArray();
            foreach (var subscriptionPair in subscriptions)
            {
                if (_subscriptions.TryRemove(subscriptionPair.Key, out var subscription))
                {
                    try
                    {
                        subscription.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Failed to dispose subscription '{subscriptionPair.Key}': ${ex}");
                    }
                }
            }
        }
    }
}
