using System.Text;
using System.Text.Json;
using Library.Domain;
using Library.Infrastructure;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Library.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMessagePublisher _publisher;
    private IConnection? _connection;
    private IModel? _channel;

    public Worker(
        ILogger<Worker> logger,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        IMessagePublisher publisher)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _publisher = publisher;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reservation Worker starting up...");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                InitRabbitMq();
                SubscribeConsumer(stoppingToken);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("RabbitMQ not ready yet, retrying in 3s... ({Message})", ex.Message);
                await Task.Delay(3000, stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_connection is not { IsOpen: true } || _channel is not { IsOpen: true })
                {
                    _logger.LogWarning("RabbitMQ consumer channel disconnected. Reconnecting...");
                    InitRabbitMq();
                    SubscribeConsumer(stoppingToken);
                }

                await RecoverStalePendingReservationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Background recovery loop warning: {Message}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private void SubscribeConsumer(CancellationToken stoppingToken)
    {
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) =>
        {
            var body = ea.Body.ToArray();
            var json = Encoding.UTF8.GetString(body);
            var correlationId = ea.BasicProperties.CorrelationId ?? "Unknown";
            var retryCount = GetRetryCount(ea.BasicProperties);

            using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            {
                _logger.LogInformation(
                    "[CorrelationId: {CorrelationId}] Consumed message (Attempt {Attempt}/{MaxAttempts}): {Json}",
                    correlationId, retryCount + 1, RabbitMqConstants.MaxRetryCount + 1, json);

                ReservationRequestedMessage? message = null;
                try
                {
                    message = JsonSerializer.Deserialize<ReservationRequestedMessage>(json);
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(
                        jsonEx,
                        "[CorrelationId: {CorrelationId}] Malformed JSON payload. Routing poison message directly to DLQ.",
                        correlationId);

                    await RecordWorkerAuditAsync(
                        null,
                        correlationId,
                        "DeadLetterRouted",
                        $"Phát hiện gói tin hỏng định dạng JSON — đã từ chối (BasicReject) và chuyển sang '{RabbitMqConstants.DeadLetterQueueName}'.",
                        stoppingToken);

                    _channel?.BasicReject(deliveryTag: ea.DeliveryTag, requeue: false);
                    return;
                }

                if (message is null || message.ReservationId == Guid.Empty)
                {
                    _logger.LogWarning(
                        "[CorrelationId: {CorrelationId}] Invalid message payload (missing ReservationId). Routing to DLQ.",
                        correlationId);

                    await RecordWorkerAuditAsync(
                        null,
                        correlationId,
                        "DeadLetterRouted",
                        $"Gói tin thiếu ReservationId hợp lệ — đã cô lập sang '{RabbitMqConstants.DeadLetterQueueName}'.",
                        stoppingToken);

                    _channel?.BasicReject(deliveryTag: ea.DeliveryTag, requeue: false);
                    return;
                }

                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var reservationService = scope.ServiceProvider.GetRequiredService<IReservationService>();
                    var result = await reservationService.ProcessReservationAsync(message, correlationId, stoppingToken);

                    if (result.Outcome == ReservationProcessOutcome.NotFound)
                    {
                        await HandleRetryOrDeadLetterAsync(ea.DeliveryTag, message, correlationId, retryCount, "Không tìm thấy bản ghi phiếu trong DB", stoppingToken);
                        return;
                    }

                    _channel?.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "[CorrelationId: {CorrelationId}] Transient error processing Reservation {ReservationId}",
                        correlationId, message.ReservationId);

                    await HandleRetryOrDeadLetterAsync(ea.DeliveryTag, message, correlationId, retryCount, ex.Message, stoppingToken);
                }
            }
        };

        _channel!.BasicConsume(
            queue: RabbitMqConstants.ReservationQueueName,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation(
            "Worker subscribed to queue '{Queue}' (DLQ: '{Dlq}'). Waiting for reservation requests...",
            RabbitMqConstants.ReservationQueueName,
            RabbitMqConstants.DeadLetterQueueName);
    }

    private async Task HandleRetryOrDeadLetterAsync(
        ulong deliveryTag,
        ReservationRequestedMessage message,
        string correlationId,
        int currentRetryCount,
        string reason,
        CancellationToken ct)
    {
        if (currentRetryCount < RabbitMqConstants.MaxRetryCount)
        {
            var nextRetry = currentRetryCount + 1;
            var delaySeconds = Math.Pow(2, currentRetryCount);
            _logger.LogWarning(
                "[CorrelationId: {CorrelationId}] Scheduling retry {NextRetry}/{MaxRetries} after {Delay}s ({Reason})",
                correlationId, nextRetry, RabbitMqConstants.MaxRetryCount, delaySeconds, reason);

            await RecordWorkerAuditAsync(
                message.ReservationId,
                correlationId,
                "WorkerRetryScheduled",
                $"Thử lại lần {nextRetry}/{RabbitMqConstants.MaxRetryCount} sau {delaySeconds}s do lỗi tạm thời: {reason}",
                ct);

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            await _publisher.PublishAsync(
                RabbitMqConstants.ExchangeName,
                RabbitMqConstants.ReservationRoutingKey,
                message,
                correlationId,
                nextRetry);

            _channel?.BasicAck(deliveryTag: deliveryTag, multiple: false);
        }
        else
        {
            await RecordWorkerAuditAsync(
                message.ReservationId,
                correlationId,
                "DeadLetterRouted",
                $"Đã vượt quá {RabbitMqConstants.MaxRetryCount} lần thử lại ({reason}) — chuyển thông điệp sang '{RabbitMqConstants.DeadLetterQueueName}'.",
                ct);

            _channel?.BasicReject(deliveryTag: deliveryTag, requeue: false);
            _logger.LogError(
                "[CorrelationId: {CorrelationId}] Max retries ({MaxRetries}) exceeded ({Reason}). Message routed to Dead Letter Queue '{Dlq}'.",
                correlationId, RabbitMqConstants.MaxRetryCount, reason, RabbitMqConstants.DeadLetterQueueName);
        }
    }

    private async Task RecordWorkerAuditAsync(
        Guid? reservationId,
        string correlationId,
        string eventType,
        string message,
        CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
            db.AuditLogs.Add(new AuditLog
            {
                ReservationId = reservationId,
                CorrelationId = correlationId,
                ServiceName = "Library.Worker",
                EventType = eventType,
                Message = message
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not write worker audit log: {Message}", ex.Message);
        }
    }

    private async Task RecoverStalePendingReservationsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
        var reservationService = scope.ServiceProvider.GetRequiredService<IReservationService>();

        var cutoff = DateTime.UtcNow.AddSeconds(-15);
        var stalePending = await db.Reservations
            .AsNoTracking()
            .Where(r => r.Status == ReservationStatus.Pending && r.CreatedAt <= cutoff)
            .OrderBy(r => r.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var pending in stalePending)
        {
            var corrId = pending.CorrelationId ?? $"recovery-{Guid.NewGuid():N}";
            _logger.LogInformation(
                "[CorrelationId: {CorrelationId}] Recovering stale Pending reservation {ReservationId} created at {CreatedAt}",
                corrId, pending.Id, pending.CreatedAt);

            var msg = new ReservationRequestedMessage
            {
                ReservationId = pending.Id,
                BookId = pending.BookId,
                UserId = pending.UserId,
                RequestedAt = pending.CreatedAt,
                CorrelationId = corrId
            };

            await reservationService.ProcessReservationAsync(msg, corrId, ct);
        }
    }

    private static int GetRetryCount(IBasicProperties? props)
    {
        if (props?.Headers is null || !props.Headers.TryGetValue(RabbitMqConstants.RetryHeaderName, out var raw) || raw is null)
        {
            return 0;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            byte b => b,
            sbyte sb => sb,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            _ => 0
        };
    }

    private void InitRabbitMq()
    {
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

        // Ensure Dead Letter Exchange & Queue
        _channel.ExchangeDeclare(RabbitMqConstants.DeadLetterExchangeName, ExchangeType.Direct, durable: true);
        _channel.QueueDeclare(RabbitMqConstants.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(RabbitMqConstants.DeadLetterQueueName, RabbitMqConstants.DeadLetterExchangeName, RabbitMqConstants.DeadLetterRoutingKey);

        // Ensure Main Exchange & Queue
        _channel.ExchangeDeclare(RabbitMqConstants.ExchangeName, ExchangeType.Direct, durable: true);
        var queueArgs = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", RabbitMqConstants.DeadLetterExchangeName },
            { "x-dead-letter-routing-key", RabbitMqConstants.DeadLetterRoutingKey }
        };
        _channel.QueueDeclare(RabbitMqConstants.ReservationQueueName, durable: true, exclusive: false, autoDelete: false, arguments: queueArgs);
        _channel.QueueBind(RabbitMqConstants.ReservationQueueName, RabbitMqConstants.ExchangeName, RabbitMqConstants.ReservationRoutingKey);

        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
