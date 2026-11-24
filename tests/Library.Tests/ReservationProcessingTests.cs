using Library.Domain;
using Library.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Library.Tests;

public class ReservationProcessingTests
{
    private static LibraryDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new LibraryDbContext(options);
    }

    private static ReservationService CreateService(LibraryDbContext db) =>
        new(db, NullLogger<ReservationService>.Instance);

    [Fact]
    public async Task ProcessReservation_WhenBookAvailable_ShouldReserveAndDecreaseCopies()
    {
        using var db = CreateInMemoryDbContext();
        var service = CreateService(db);

        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Designing Data-Intensive Applications",
            Author = "Martin Kleppmann",
            ISBN = "978-1449373320",
            TotalCopies = 2,
            AvailableCopies = 2
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = "Nguyễn Văn An",
            Email = "an.nguyen@library.edu.vn"
        };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Pending
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var msg = new ReservationRequestedMessage
        {
            ReservationId = reservation.Id,
            BookId = book.Id,
            UserId = user.Id,
            RequestedAt = reservation.CreatedAt,
            CorrelationId = "corr-001"
        };

        var result = await service.ProcessReservationAsync(msg, "corr-001");

        Assert.Equal(ReservationProcessOutcome.Approved, result.Outcome);
        Assert.Equal(1, result.AvailableCopies);

        var updatedReservation = await db.Reservations.FindAsync(reservation.Id);
        var updatedBook = await db.Books.FindAsync(book.Id);
        Assert.NotNull(updatedReservation);
        Assert.NotNull(updatedBook);
        Assert.Equal(ReservationStatus.Reserved, updatedReservation.Status);
        Assert.Equal("corr-001", updatedReservation.CorrelationId);
        Assert.NotNull(updatedReservation.ProcessedAt);
        Assert.Equal(1, updatedBook.AvailableCopies);
    }

    [Fact]
    public async Task ProcessReservation_WhenBookOutOfStock_ShouldRejectReservation()
    {
        using var db = CreateInMemoryDbContext();
        var service = CreateService(db);

        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Clean Architecture",
            Author = "Robert C. Martin",
            ISBN = "978-0134494166",
            TotalCopies = 1,
            AvailableCopies = 0
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = "Trần Thị Bình",
            Email = "binh.tran@library.edu.vn"
        };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Pending
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var result = await service.ProcessReservationAsync(
            new ReservationRequestedMessage
            {
                ReservationId = reservation.Id,
                BookId = book.Id,
                UserId = user.Id,
                RequestedAt = DateTime.UtcNow
            },
            "corr-out-of-stock");

        Assert.Equal(ReservationProcessOutcome.Rejected, result.Outcome);
        Assert.Equal(0, result.AvailableCopies);

        var updatedReservation = await db.Reservations.FindAsync(reservation.Id);
        Assert.NotNull(updatedReservation);
        Assert.Equal(ReservationStatus.Rejected, updatedReservation.Status);
    }

    [Fact]
    public async Task ProcessReservation_Idempotency_DuplicateMessageShouldNotDeductTwice()
    {
        using var db = CreateInMemoryDbContext();
        var service = CreateService(db);

        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Enterprise Integration Patterns",
            Author = "Gregor Hohpe",
            TotalCopies = 2,
            AvailableCopies = 2
        };
        var user = new User { Id = Guid.NewGuid(), FullName = "Lê Hoàng Cường" };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Pending
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var msg = new ReservationRequestedMessage
        {
            ReservationId = reservation.Id,
            BookId = book.Id,
            UserId = user.Id,
            RequestedAt = DateTime.UtcNow
        };

        var firstResult = await service.ProcessReservationAsync(msg, "corr-dup");
        var secondResult = await service.ProcessReservationAsync(msg, "corr-dup");

        Assert.Equal(ReservationProcessOutcome.Approved, firstResult.Outcome);
        Assert.Equal(ReservationProcessOutcome.AlreadyProcessed, secondResult.Outcome);

        var currentBook = await db.Books.FindAsync(book.Id);
        Assert.NotNull(currentBook);
        Assert.Equal(1, currentBook.AvailableCopies);
    }

    [Fact]
    public async Task UpdateStatus_FromReservedToBorrowed_ShouldSucceed()
    {
        using var db = CreateInMemoryDbContext();
        var service = CreateService(db);

        var book = new Book { Id = Guid.NewGuid(), Title = "Book A", TotalCopies = 2, AvailableCopies = 1 };
        var user = new User { Id = Guid.NewGuid(), FullName = "Reader A" };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Reserved
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var update = await service.UpdateStatusAsync(reservation.Id, ReservationStatus.Borrowed);

        Assert.True(update.Success);
        Assert.Equal(ReservationStatus.Borrowed, update.Reservation?.Status);
        Assert.NotNull(update.Reservation?.BorrowedAt);
    }

    [Fact]
    public async Task UpdateStatus_FromBorrowedToReturned_ShouldRestoreAvailableCopiesUpToTotal()
    {
        using var db = CreateInMemoryDbContext();
        var service = CreateService(db);

        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Distributed Systems",
            TotalCopies = 3,
            AvailableCopies = 2
        };
        var user = new User { Id = Guid.NewGuid(), FullName = "Reader B" };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Borrowed,
            BorrowedAt = DateTime.UtcNow.AddDays(-1)
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var update = await service.UpdateStatusAsync(reservation.Id, ReservationStatus.Returned);

        Assert.True(update.Success);
        Assert.Equal(3, update.AvailableCopies);
        Assert.Equal(ReservationStatus.Returned, update.Reservation?.Status);
        Assert.NotNull(update.Reservation?.ReturnedAt);
    }

    [Fact]
    public async Task UpdateStatus_InvalidTransition_PendingToBorrowed_ShouldReturnFailure()
    {
        using var db = CreateInMemoryDbContext();
        var service = CreateService(db);

        var book = new Book { Id = Guid.NewGuid(), Title = "Book C", TotalCopies = 1, AvailableCopies = 1 };
        var user = new User { Id = Guid.NewGuid(), FullName = "Reader C" };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Pending
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var update = await service.UpdateStatusAsync(reservation.Id, ReservationStatus.Borrowed);

        Assert.False(update.Success);
        Assert.False(update.NotFound);
        Assert.Equal(ReservationStatus.Pending, reservation.Status);
    }

    [Fact]
    public async Task CancelReservation_WhenReserved_ShouldRestoreAvailableCopyAndMarkRejected()
    {
        using var db = CreateInMemoryDbContext();
        var service = CreateService(db);

        var book = new Book { Id = Guid.NewGuid(), Title = "Book D", TotalCopies = 1, AvailableCopies = 0 };
        var user = new User { Id = Guid.NewGuid(), FullName = "Reader D" };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Reserved
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        var cancel = await service.CancelReservationAsync(reservation.Id, "Bạn đọc hủy đặt trước.");

        Assert.True(cancel.Success);
        Assert.Equal(1, cancel.AvailableCopies);
        Assert.Equal(ReservationStatus.Rejected, cancel.Reservation?.Status);
    }

    [Fact]
    public async Task ProcessReservation_BurstRequestsForSingleCopy_ShouldApproveExactlyOneAndRejectRest()
    {
        using var db = CreateInMemoryDbContext();
        var service = CreateService(db);

        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Building Microservices",
            Author = "Sam Newman",
            TotalCopies = 1,
            AvailableCopies = 1
        };
        var user = new User { Id = Guid.NewGuid(), FullName = "Reader Burst" };
        db.Books.Add(book);
        db.Users.Add(user);

        var reservations = Enumerable.Range(1, 5).Select(_ => new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Pending
        }).ToList();

        db.Reservations.AddRange(reservations);
        await db.SaveChangesAsync();

        int approvedCount = 0;
        int rejectedCount = 0;

        foreach (var r in reservations)
        {
            var res = await service.ProcessReservationAsync(
                new ReservationRequestedMessage
                {
                    ReservationId = r.Id,
                    BookId = book.Id,
                    UserId = user.Id,
                    RequestedAt = r.CreatedAt
                },
                $"burst-{r.Id}");

            if (res.Outcome == ReservationProcessOutcome.Approved) approvedCount++;
            if (res.Outcome == ReservationProcessOutcome.Rejected) rejectedCount++;
        }

        var finalBook = await db.Books.FindAsync(book.Id);
        Assert.Equal(1, approvedCount);
        Assert.Equal(4, rejectedCount);
        Assert.NotNull(finalBook);
        Assert.Equal(0, finalBook.AvailableCopies);
    }
}
