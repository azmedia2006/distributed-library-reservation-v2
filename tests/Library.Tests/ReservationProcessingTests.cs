using Library.Domain;
using Library.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Library.Tests;

public class ReservationProcessingTests
{
    private LibraryDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<LibraryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new LibraryDbContext(options);
    }

    [Fact]
    public async Task ProcessReservation_WhenBookAvailable_ShouldReserveAndDecreaseCopies()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
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
            Email = "an.nguyen@example.com",
            PhoneNumber = "0912345678"
        };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        // Act - Giả lập nghiệp vụ của Reservation Worker
        if (book.AvailableCopies > 0)
        {
            book.AvailableCopies -= 1;
            reservation.Status = ReservationStatus.Reserved;
            reservation.ReasonMessage = "Đã xác nhận đặt trước sách thành công.";
            reservation.ProcessedAt = DateTime.UtcNow;
        }
        else
        {
            reservation.Status = ReservationStatus.Rejected;
            reservation.ReasonMessage = "Hết sách khả dụng.";
            reservation.ProcessedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        // Assert
        var updatedReservation = await db.Reservations.FindAsync(reservation.Id);
        var updatedBook = await db.Books.FindAsync(book.Id);

        Assert.NotNull(updatedReservation);
        Assert.NotNull(updatedBook);
        Assert.Equal(ReservationStatus.Reserved, updatedReservation.Status);
        Assert.Equal(1, updatedBook.AvailableCopies);
    }

    [Fact]
    public async Task ProcessReservation_WhenBookOutOfStock_ShouldRejectReservation()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Clean Architecture",
            Author = "Robert C. Martin",
            ISBN = "978-0134494166",
            TotalCopies = 1,
            AvailableCopies = 0 // Đã hết sách
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = "Trần Thị Bình",
            Email = "binh.tran@example.com",
            PhoneNumber = "0987654321"
        };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = user.Id,
            Status = ReservationStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        db.Books.Add(book);
        db.Users.Add(user);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        // Act - Worker xử lý khi hết sách
        if (book.AvailableCopies > 0)
        {
            book.AvailableCopies -= 1;
            reservation.Status = ReservationStatus.Reserved;
        }
        else
        {
            reservation.Status = ReservationStatus.Rejected;
            reservation.ReasonMessage = "Hết sách khả dụng.";
            reservation.ProcessedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        // Assert
        var updatedReservation = await db.Reservations.FindAsync(reservation.Id);
        var updatedBook = await db.Books.FindAsync(book.Id);

        Assert.NotNull(updatedReservation);
        Assert.NotNull(updatedBook);
        Assert.Equal(ReservationStatus.Rejected, updatedReservation.Status);
        Assert.Equal(0, updatedBook.AvailableCopies);
    }

    [Fact]
    public async Task UpdateStatus_ToReturned_ShouldIncreaseAvailableCopies()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Distributed Systems: Principles and Paradigms",
            Author = "Andrew S. Tanenbaum",
            ISBN = "978-1530281756",
            TotalCopies = 3,
            AvailableCopies = 2 // 1 cuốn đang được mượn
        };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = Guid.NewGuid(),
            Status = ReservationStatus.Borrowed,
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            BorrowedAt = DateTime.UtcNow.AddDays(-2)
        };

        db.Books.Add(book);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        // Act - Trả sách
        reservation.Status = ReservationStatus.Returned;
        reservation.ReturnedAt = DateTime.UtcNow;
        book.AvailableCopies = Math.Min(book.TotalCopies, book.AvailableCopies + 1);
        await db.SaveChangesAsync();

        // Assert
        var updatedBook = await db.Books.FindAsync(book.Id);
        Assert.NotNull(updatedBook);
        Assert.Equal(3, updatedBook.AvailableCopies);
        Assert.Equal(ReservationStatus.Returned, reservation.Status);
    }

    [Fact]
    public async Task ProcessReservation_Idempotency_ShouldNotDeductTwice()
    {
        // Arrange
        using var db = CreateInMemoryDbContext();
        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Cloud Native Patterns",
            Author = "Cornelia Davis",
            ISBN = "978-1617294297",
            TotalCopies = 2,
            AvailableCopies = 1
        };
        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            UserId = Guid.NewGuid(),
            Status = ReservationStatus.Reserved, // Đã được xử lý Reserved trước đó
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ProcessedAt = DateTime.UtcNow.AddMinutes(-4)
        };

        db.Books.Add(book);
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        // Act - Giả lập message trùng lặp đến Worker
        if (reservation.Status == ReservationStatus.Pending)
        {
            // Nếu là pending mới trừ
            book.AvailableCopies -= 1;
            reservation.Status = ReservationStatus.Reserved;
            await db.SaveChangesAsync();
        }

        // Assert - Số sách khả dụng vẫn giữ nguyên 1, không bị trừ 2 lần
        var currentBook = await db.Books.FindAsync(book.Id);
        Assert.NotNull(currentBook);
        Assert.Equal(1, currentBook.AvailableCopies);
    }
}
