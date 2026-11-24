using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Library.Infrastructure;

public record RabbitMqQueueMetrics(
    bool Connected,
    string MainQueueName,
    uint MainQueueMessages,
    uint MainQueueConsumers,
    string DeadLetterQueueName,
    uint DeadLetterMessages);

public interface IMessagePublisher
{
    Task PublishAsync<T>(string exchange, string routingKey, T message, string? correlationId = null, int retryCount = 0);
    Task PublishRawAsync(string exchange, string routingKey, string rawPayload, string? correlationId = null);
    RabbitMqQueueMetrics GetQueueMetrics();
    bool CheckHealth();
}

public class RabbitMqPublisher : IMessagePublisher, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly object _lock = new();

    public RabbitMqPublisher(IConfiguration configuration, ILogger<RabbitMqPublisher> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private void EnsureConnectedLocked()
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
        {
            return;
        }

        _channel?.Dispose();
        _connection?.Dispose();

        var factory = new ConnectionFactory
        {
            HostName = _configuration["RabbitMQ:Host"] ?? "localhost",
            Port = int.TryParse(_configuration["RabbitMQ:Port"], out var port) ? port : 5672,
            UserName = _configuration["RabbitMQ:UserName"] ?? "guest",
            Password = _configuration["RabbitMQ:Password"] ?? "guest",
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        // Dead Letter Exchange & Dead Letter Queue
        _channel.ExchangeDeclare(RabbitMqConstants.DeadLetterExchangeName, ExchangeType.Direct, durable: true);
        _channel.QueueDeclare(RabbitMqConstants.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(RabbitMqConstants.DeadLetterQueueName, RabbitMqConstants.DeadLetterExchangeName, RabbitMqConstants.DeadLetterRoutingKey);

        // Main Direct Exchange & Reservation Queue configured with DLX
        _channel.ExchangeDeclare(RabbitMqConstants.ExchangeName, ExchangeType.Direct, durable: true);
        var queueArgs = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", RabbitMqConstants.DeadLetterExchangeName },
            { "x-dead-letter-routing-key", RabbitMqConstants.DeadLetterRoutingKey }
        };
        _channel.QueueDeclare(RabbitMqConstants.ReservationQueueName, durable: true, exclusive: false, autoDelete: false, arguments: queueArgs);
        _channel.QueueBind(RabbitMqConstants.ReservationQueueName, RabbitMqConstants.ExchangeName, RabbitMqConstants.ReservationRoutingKey);

        _logger.LogInformation("Connected to RabbitMQ and verified exchange/queue/DLQ topology.");
    }

    public Task PublishAsync<T>(string exchange, string routingKey, T message, string? correlationId = null, int retryCount = 0)
    {
        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString()
            : correlationId;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

        lock (_lock)
        {
            EnsureConnectedLocked();

            var props = _channel!.CreateBasicProperties();
            props.Persistent = true;
            props.ContentType = "application/json";
            props.CorrelationId = resolvedCorrelationId;
            props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            props.Headers = new Dictionary<string, object>
            {
                { RabbitMqConstants.RetryHeaderName, retryCount }
            };

            _channel.BasicPublish(
                exchange: exchange,
                routingKey: routingKey,
                basicProperties: props,
                body: body);
        }

        _logger.LogInformation(
            "[CorrelationId: {CorrelationId}] Published message to {Exchange}/{RoutingKey} (Retry: {RetryCount})",
            resolvedCorrelationId, exchange, routingKey, retryCount);

        return Task.CompletedTask;
    }

    public Task PublishRawAsync(string exchange, string routingKey, string rawPayload, string? correlationId = null)
    {
        var resolvedCorrelationId = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString()
            : correlationId;
        var body = Encoding.UTF8.GetBytes(rawPayload);

        lock (_lock)
        {
            EnsureConnectedLocked();

            var props = _channel!.CreateBasicProperties();
            props.Persistent = true;
            props.ContentType = "application/json";
            props.CorrelationId = resolvedCorrelationId;
            props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            _channel.BasicPublish(
                exchange: exchange,
                routingKey: routingKey,
                basicProperties: props,
                body: body);
        }

        _logger.LogInformation(
            "[CorrelationId: {CorrelationId}] Published raw diagnostic payload to {Exchange}/{RoutingKey}",
            resolvedCorrelationId, exchange, routingKey);

        return Task.CompletedTask;
    }

    public RabbitMqQueueMetrics GetQueueMetrics()
    {
        try
        {
            lock (_lock)
            {
                EnsureConnectedLocked();
                var mainOk = _channel!.QueueDeclarePassive(RabbitMqConstants.ReservationQueueName);
                var dlqOk = _channel.QueueDeclarePassive(RabbitMqConstants.DeadLetterQueueName);

                return new RabbitMqQueueMetrics(
                    true,
                    RabbitMqConstants.ReservationQueueName,
                    mainOk.MessageCount,
                    mainOk.ConsumerCount,
                    RabbitMqConstants.DeadLetterQueueName,
                    dlqOk.MessageCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Unable to inspect RabbitMQ queue metrics: {Message}", ex.Message);
            return new RabbitMqQueueMetrics(
                false,
                RabbitMqConstants.ReservationQueueName,
                0,
                0,
                RabbitMqConstants.DeadLetterQueueName,
                0);
        }
    }

    public bool CheckHealth()
    {
        try
        {
            lock (_lock)
            {
                EnsureConnectedLocked();
                return _connection is { IsOpen: true } && _channel is { IsOpen: true };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("RabbitMQ health check failed: {Message}", ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _channel?.Dispose();
            _connection?.Dispose();
        }
    }
}
