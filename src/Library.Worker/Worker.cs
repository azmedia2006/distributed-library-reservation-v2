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
    private IConnection? _connection;
    private IModel? _channel;

    public Worker(ILogger<Worker> logger, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reservation Worker is starting and connecting to RabbitMQ...");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retry loop to connect to RabbitMQ
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                InitRabbitMq();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("RabbitMQ not ready yet, retrying in 3s... ({Message})", ex.Message);
                await Task.Delay(3000, stoppingToken);
            }
        }

        if (stoppingToken.IsCancellationRequested) return;

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var json = Encoding.UTF8.GetString(body);
            var correlationId = ea.BasicProperties.CorrelationId ?? "Unknown";

            _logger.LogInformation("[CorrelationId: {CorrelationId}] Received message: {Json}", correlationId, json);

            bool success = false;
            try
            {
                var message = JsonSerializer.Deserialize<ReservationRequestedMessage>(json);
                if (message != null)
                {
                    success = await ProcessReservationAsync(message, correlationId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CorrelationId: {CorrelationId}] Error while processing message", correlationId);
            }

            if (success)
            {
                _channel?.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
            }
            else
            {
                // Reject message; route to DLQ if max retries exceeded or dead-letter configured
                _channel?.BasicReject(deliveryTag: ea.DeliveryTag, requeue: false);
                _logger.LogWarning("[CorrelationId: {CorrelationId}] Message rejected and sent to Dead Letter Queue.", correlationId);
            }
        };

        _channel.BasicConsume(queue: RabbitMqConstants.ReservationQueueName, autoAck: false, consumer: consumer);
        _logger.LogInformation("Worker subscribed to queue: {Queue}. Waiting for messages...", RabbitMqConstants.ReservationQueueName);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private void InitRabbitMq()
    {
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

        // Ensure DLX and DLQ
        _channel.ExchangeDeclare(RabbitMqConstants.DeadLetterExchangeName, ExchangeType.Direct, durable: true);
        _channel.QueueDeclare(RabbitMqConstants.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(RabbitMqConstants.DeadLetterQueueName, RabbitMqConstants.DeadLetterExchangeName, RabbitMqConstants.DeadLetterRoutingKey);

        // Ensure Main exchange and queue
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

    private async Task<bool> ProcessReservationAsync(ReservationRequestedMessage message, string correlationId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();

        // Idempotency check: Tra cứu yêu cầu đặt mượn
        var reservation = await db.Reservations.Include(r => r.Book).FirstOrDefaultAsync(r => r.Id == message.ReservationId);
        if (reservation == null)
        {
            _logger.LogWarning("[CorrelationId: {CorrelationId}] Reservation {ReservationId} not found in database.", correlationId, message.ReservationId);
            return false;
        }

        // Nếu trạng thái đã được xử lý trước đó (Idempotent handling)
        if (reservation.Status != ReservationStatus.Pending)
        {
            _logger.LogInformation("[CorrelationId: {CorrelationId}] Reservation {ReservationId} already processed with status {Status}. Skipping.", correlationId, reservation.Id, reservation.Status);
            return true;
        }

        var book = reservation.Book ?? await db.Books.FindAsync(message.BookId);
        if (book == null)
        {
            reservation.Status = ReservationStatus.Rejected;
            reservation.ReasonMessage = "Không tìm thấy thông tin sách trong hệ thống.";
            reservation.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return true;
        }

        // Kiểm tra số bản khả dụng
        _logger.LogInformation("[CorrelationId: {CorrelationId}] Checking availability for book '{Title}'. Available copies: {Available}",
            correlationId, book.Title, book.AvailableCopies);

        if (book.AvailableCopies > 0)
        {
            // Trừ số bản và xác nhận giữ sách (Reserved)
            book.AvailableCopies -= 1;
            reservation.Status = ReservationStatus.Reserved;
            reservation.ReasonMessage = "Đã xác nhận đặt trước sách thành công. Vui lòng đến nhận sách.";
            reservation.ProcessedAt = DateTime.UtcNow;

            _logger.LogInformation("[CorrelationId: {CorrelationId}] Reservation APPROVED for ReservationId {ReservationId}. Remaining copies: {Remaining}",
                correlationId, reservation.Id, book.AvailableCopies);
        }
        else
        {
            // Không đủ sách -> Từ chối (Rejected)
            reservation.Status = ReservationStatus.Rejected;
            reservation.ReasonMessage = "Hết sách khả dụng. Yêu cầu đặt mượn đã bị từ chối.";
            reservation.ProcessedAt = DateTime.UtcNow;

            _logger.LogWarning("[CorrelationId: {CorrelationId}] Reservation REJECTED for ReservationId {ReservationId}. No copies available.",
                correlationId, reservation.Id);
        }

        await db.SaveChangesAsync();
        return true;
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
