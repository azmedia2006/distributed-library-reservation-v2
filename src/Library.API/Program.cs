using Library.Domain;
using Library.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LibraryDb") 
    ?? "Host=localhost;Port=5432;Database=library_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<LibraryDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Hệ Thống Quản Lý Thư Viện Phân Tán API", Version = "v1" });
});

builder.Services.AddHealthChecks();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (int retry = 0; retry < 5; retry++)
    {
        try
        {
            await LibraryDbContext.SeedDataAsync(db);
            logger.LogInformation("Database connected and initialized successfully.");
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Waiting for Database to become ready (Attempt {Attempt}/5): {Message}", retry + 1, ex.Message);
            await Task.Delay(2000);
        }
    }
}

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

// 0. GET /health: Health Check
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }))
   .WithName("HealthCheck");

// GET /stats: Thống kê tổng quan cho Dashboard
app.MapGet("/stats", async (LibraryDbContext db) =>
{
    var totalBooks = await db.Books.CountAsync();
    var totalCopies = await db.Books.SumAsync(b => b.TotalCopies);
    var availableCopies = await db.Books.SumAsync(b => b.AvailableCopies);
    var totalUsers = await db.Users.CountAsync();
    var totalReservations = await db.Reservations.CountAsync();
    var pendingReservations = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.Pending);
    var reservedReservations = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.Reserved);
    var borrowedReservations = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.Borrowed);
    var returnedReservations = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.Returned);
    var rejectedReservations = await db.Reservations.CountAsync(r => r.Status == ReservationStatus.Rejected);

    return Results.Ok(new
    {
        totalBooks,
        totalCopies,
        availableCopies,
        borrowedCopies = totalCopies - availableCopies,
        totalUsers,
        totalReservations,
        pendingReservations,
        reservedReservations,
        borrowedReservations,
        returnedReservations,
        rejectedReservations
    });
})
.WithName("GetStats");

// 1. GET /books: Danh sách sách (hỗ trợ tìm kiếm theo từ khóa)
app.MapGet("/books", async ([FromQuery] string? search, LibraryDbContext db) =>
{
    var query = db.Books.AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var keyword = search.Trim().ToLower();
        query = query.Where(b => b.Title.ToLower().Contains(keyword) || 
                                 b.Author.ToLower().Contains(keyword) || 
                                 b.ISBN.ToLower().Contains(keyword));
    }
    var books = await query.OrderBy(b => b.Title).ToListAsync();
    return Results.Ok(books);
})
.WithName("GetBooks");

// 2. GET /books/{id}: Chi tiết sách
app.MapGet("/books/{id:guid}", async (Guid id, LibraryDbContext db) =>
{
    var book = await db.Books.FindAsync(id);
    if (book is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy sách có ID {id}" });
    }
    return Results.Ok(book);
})
.WithName("GetBookById");

// POST /books: Thêm sách mới
app.MapPost("/books", async ([FromBody] CreateBookRequest req, LibraryDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Title))
        return Results.BadRequest(new { message = "Tiêu đề sách không được để trống." });

    var book = new Book
    {
        Id = Guid.NewGuid(),
        Title = req.Title.Trim(),
        Author = req.Author?.Trim() ?? "Chưa rõ",
        ISBN = req.ISBN?.Trim() ?? string.Empty,
        Description = req.Description?.Trim() ?? string.Empty,
        TotalCopies = req.TotalCopies > 0 ? req.TotalCopies : 1,
        AvailableCopies = req.TotalCopies > 0 ? req.TotalCopies : 1,
        CreatedAt = DateTime.UtcNow
    };

    db.Books.Add(book);
    await db.SaveChangesAsync();
    return Results.Created($"/books/{book.Id}", book);
})
.WithName("CreateBook");

// 3. POST /reservations: Tiếp nhận yêu cầu đặt mượn sách
app.MapPost("/reservations", async ([FromBody] CreateReservationRequest request, LibraryDbContext db, IMessagePublisher publisher, ILogger<Program> logger) =>
{
    var book = await db.Books.FindAsync(request.BookId);
    if (book is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy sách có ID {request.BookId}" });
    }

    var user = await db.Users.FindAsync(request.UserId);
    if (user is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy bạn đọc có ID {request.UserId}" });
    }

    var correlationId = Guid.NewGuid().ToString();

    var reservation = new Reservation
    {
        Id = Guid.NewGuid(),
        BookId = request.BookId,
        UserId = request.UserId,
        Status = ReservationStatus.Pending,
        CreatedAt = DateTime.UtcNow
    };

    db.Reservations.Add(reservation);
    await db.SaveChangesAsync();

    logger.LogInformation("[CorrelationId: {CorrelationId}] Tiếp nhận yêu cầu đặt mượn {ReservationId} (Pending)", correlationId, reservation.Id);

    // Bắn tin nhắn vào RabbitMQ để xử lý bất đồng bộ
    var queueMessage = new ReservationRequestedMessage
    {
        ReservationId = reservation.Id,
        BookId = reservation.BookId,
        UserId = reservation.UserId,
        RequestedAt = reservation.CreatedAt,
        CorrelationId = correlationId
    };

    try
    {
        await publisher.PublishAsync(
            RabbitMqConstants.ExchangeName, 
            RabbitMqConstants.ReservationRoutingKey, 
            queueMessage, 
            correlationId);

        logger.LogInformation("[CorrelationId: {CorrelationId}] Tin nhắn đã đẩy vào RabbitMQ exchange", correlationId);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[CorrelationId: {CorrelationId}] Lỗi khi đẩy tin nhắn vào RabbitMQ", correlationId);
    }

    return Results.Accepted($"/reservations/{reservation.Id}", new
    {
        reservation.Id,
        reservation.BookId,
        reservation.UserId,
        Status = reservation.Status.ToString(),
        reservation.CreatedAt,
        correlationId,
        message = "Yêu cầu đặt mượn đã được tiếp nhận và đưa vào hàng đợi xử lý."
    });
})
.WithName("CreateReservation");

// 4. GET /reservations/{id}: Tra cứu đơn theo ID
app.MapGet("/reservations/{id:guid}", async (Guid id, LibraryDbContext db) =>
{
    var reservation = await db.Reservations
        .Include(r => r.Book)
        .Include(r => r.User)
        .FirstOrDefaultAsync(r => r.Id == id);

    if (reservation is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy yêu cầu đặt mượn có ID {id}" });
    }

    return Results.Ok(new
    {
        reservation.Id,
        reservation.BookId,
        BookTitle = reservation.Book?.Title,
        reservation.UserId,
        UserName = reservation.User?.FullName,
        Status = reservation.Status.ToString(),
        reservation.ReasonMessage,
        reservation.CreatedAt,
        reservation.ProcessedAt,
        reservation.BorrowedAt,
        reservation.ReturnedAt
    });
})
.WithName("GetReservationById");

// GET /reservations: Danh sách tất cả đơn đặt mượn trong hệ thống (Quản lý)
app.MapGet("/reservations", async ([FromQuery] string? status, LibraryDbContext db) =>
{
    var query = db.Reservations
        .Include(r => r.Book)
        .Include(r => r.User)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ReservationStatus>(status, true, out var parsedStatus))
    {
        query = query.Where(r => r.Status == parsedStatus);
    }

    var list = await query
        .OrderByDescending(r => r.CreatedAt)
        .Select(r => new
        {
            r.Id,
            r.BookId,
            BookTitle = r.Book != null ? r.Book.Title : string.Empty,
            r.UserId,
            UserName = r.User != null ? r.User.FullName : string.Empty,
            UserPhone = r.User != null ? r.User.PhoneNumber : string.Empty,
            Status = r.Status.ToString(),
            r.ReasonMessage,
            r.CreatedAt,
            r.ProcessedAt,
            r.BorrowedAt,
            r.ReturnedAt
        })
        .ToListAsync();

    return Results.Ok(list);
})
.WithName("GetAllReservations");

// 5. GET /users/{userId}/reservations: Lịch sử đặt mượn của 1 bạn đọc
app.MapGet("/users/{userId:guid}/reservations", async (Guid userId, LibraryDbContext db) =>
{
    var userExists = await db.Users.AnyAsync(u => u.Id == userId);
    if (!userExists)
    {
        return Results.NotFound(new { message = $"Không tìm thấy bạn đọc có ID {userId}" });
    }

    var list = await db.Reservations
        .Include(r => r.Book)
        .Where(r => r.UserId == userId)
        .OrderByDescending(r => r.CreatedAt)
        .Select(r => new
        {
            r.Id,
            r.BookId,
            BookTitle = r.Book != null ? r.Book.Title : string.Empty,
            Status = r.Status.ToString(),
            r.ReasonMessage,
            r.CreatedAt,
            r.ProcessedAt,
            r.BorrowedAt,
            r.ReturnedAt
        })
        .ToListAsync();

    return Results.Ok(list);
})
.WithName("GetUserReservations");

// GET /users: Danh sách bạn đọc
app.MapGet("/users", async (LibraryDbContext db) =>
{
    var users = await db.Users.OrderBy(u => u.FullName).ToListAsync();
    return Results.Ok(users);
})
.WithName("GetUsers");

// POST /users: Thêm bạn đọc mới
app.MapPost("/users", async ([FromBody] CreateUserRequest req, LibraryDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.FullName))
        return Results.BadRequest(new { message = "Họ và tên không được để trống." });

    var user = new User
    {
        Id = Guid.NewGuid(),
        FullName = req.FullName.Trim(),
        Email = req.Email?.Trim() ?? string.Empty,
        PhoneNumber = req.PhoneNumber?.Trim() ?? string.Empty,
        CreatedAt = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Created($"/users/{user.Id}", user);
})
.WithName("CreateUser");

// 6. PUT /reservations/{id}/status: Cập nhật trạng thái sách (Borrowed hoặc Returned)
app.MapPut("/reservations/{id:guid}/status", async (Guid id, [FromBody] UpdateReservationStatusRequest req, LibraryDbContext db) =>
{
    var reservation = await db.Reservations.Include(r => r.Book).FirstOrDefaultAsync(r => r.Id == id);
    if (reservation is null) return Results.NotFound(new { message = "Không tìm thấy yêu cầu đặt mượn" });

    if (req.NewStatus == ReservationStatus.Borrowed)
    {
        if (reservation.Status != ReservationStatus.Reserved)
        {
            return Results.BadRequest(new { message = "Chỉ đơn ở trạng thái 'Reserved' (Đã duyệt giữ sách) mới có thể nhận sách (Borrowed)" });
        }
        reservation.Status = ReservationStatus.Borrowed;
        reservation.BorrowedAt = DateTime.UtcNow;
    }
    else if (req.NewStatus == ReservationStatus.Returned)
    {
        if (reservation.Status != ReservationStatus.Borrowed)
        {
            return Results.BadRequest(new { message = "Chỉ đơn ở trạng thái 'Borrowed' (Đang mượn) mới có thể trả sách (Returned)" });
        }
        reservation.Status = ReservationStatus.Returned;
        reservation.ReturnedAt = DateTime.UtcNow;
        if (reservation.Book != null)
        {
            reservation.Book.AvailableCopies = Math.Min(reservation.Book.TotalCopies, reservation.Book.AvailableCopies + 1);
        }
    }
    else
    {
        return Results.BadRequest(new { message = "Trạng thái hợp lệ là Borrowed hoặc Returned" });
    }

    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        reservation.Id,
        Status = reservation.Status.ToString(),
        reservation.BorrowedAt,
        reservation.ReturnedAt,
        AvailableCopies = reservation.Book?.AvailableCopies
    });
})
.WithName("UpdateReservationStatus");

app.Run();

public record CreateReservationRequest(Guid BookId, Guid UserId);
public record UpdateReservationStatusRequest(ReservationStatus NewStatus);
public record CreateBookRequest(string Title, string Author, string ISBN, string Description, int TotalCopies);
public record CreateUserRequest(string FullName, string Email, string PhoneNumber);
