using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Library.Infrastructure;

public interface IMessagePublisher
{
    Task PublishAsync<T>(string exchange, string routingKey, T message, string? correlationId = null);
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

    private void EnsureConnected()
    {
        if (_channel != null && _channel.IsOpen) return;

        lock (_lock)
        {
            if (_channel != null && _channel.IsOpen) return;

            var factory = new ConnectionFactory
            {
                HostName = _configuration["RabbitMQ:Host"] ?? "localhost",
                Port = int.TryParse(_configuration["RabbitMQ:Port"], out var port) ? port : 5672,
                UserName = _configuration["RabbitMQ:UserName"] ?? "guest",
                Password = _configuration["RabbitMQ:Password"] ?? "guest",
                DispatchConsumersAsync = true,
                AutomaticRecoveryEnabled = true
            };

            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // DLX & DLQ
            _channel.ExchangeDeclare(RabbitMqConstants.DeadLetterExchangeName, ExchangeType.Direct, durable: true);
            _channel.QueueDeclare(RabbitMqConstants.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
            _channel.QueueBind(RabbitMqConstants.DeadLetterQueueName, RabbitMqConstants.DeadLetterExchangeName, RabbitMqConstants.DeadLetterRoutingKey);

            // Main exchange & queue with DLX configured
            _channel.ExchangeDeclare(RabbitMqConstants.ExchangeName, ExchangeType.Direct, durable: true);
            var queueArgs = new Dictionary<string, object>
            {
                { "x-dead-letter-exchange", RabbitMqConstants.DeadLetterExchangeName },
                { "x-dead-letter-routing-key", RabbitMqConstants.DeadLetterRoutingKey }
            };
            _channel.QueueDeclare(RabbitMqConstants.ReservationQueueName, durable: true, exclusive: false, autoDelete: false, arguments: queueArgs);
            _channel.QueueBind(RabbitMqConstants.ReservationQueueName, RabbitMqConstants.ExchangeName, RabbitMqConstants.ReservationRoutingKey);

            _logger.LogInformation("Connected to RabbitMQ and declared topology successfully.");
        }
    }

    public Task PublishAsync<T>(string exchange, string routingKey, T message, string? correlationId = null)
    {
        EnsureConnected();

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = _channel!.CreateBasicProperties();
        props.Persistent = true;
        props.CorrelationId = correlationId ?? Guid.NewGuid().ToString();

        _channel.BasicPublish(exchange: exchange, routingKey: routingKey, basicProperties: props, body: body);
        _logger.LogInformation("Published message with CorrelationId {CorrelationId} to exchange {Exchange} with routingKey {RoutingKey}",
            props.CorrelationId, exchange, routingKey);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
