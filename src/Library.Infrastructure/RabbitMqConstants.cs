namespace Library.Infrastructure;

public static class RabbitMqConstants
{
    public const string ExchangeName = "library.exchange";
    public const string ReservationQueueName = "library.reservations.queue";
    public const string DeadLetterExchangeName = "library.exchange.dlx";
    public const string DeadLetterQueueName = "library.reservations.dlq";
    public const string ReservationRoutingKey = "reservation.created";
    public const string DeadLetterRoutingKey = "reservation.deadletter";
    public const string RetryHeaderName = "x-retry-count";
    public const int MaxRetryCount = 3;
}
