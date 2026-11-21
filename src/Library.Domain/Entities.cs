namespace Library.Domain;

public enum ReservationStatus
{
    Pending = 0,
    Reserved = 1,
    Rejected = 2,
    Borrowed = 3,
    Returned = 4
}

public class Book
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string ISBN { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int TotalCopies { get; set; }
    public int AvailableCopies { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Reservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Book? Book { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Pending;
    public string? ReasonMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public DateTime? BorrowedAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
}

public class ReservationRequestedMessage
{
    public Guid ReservationId { get; set; }
    public Guid BookId { get; set; }
    public Guid UserId { get; set; }
    public DateTime RequestedAt { get; set; }
    public string? CorrelationId { get; set; }
}
