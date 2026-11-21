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
    c.SwaggerDoc("v1", new() { Title = "Distributed Library Reservation API", Version = "v1" });
});

builder.Services.AddHealthChecks()
    ;

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

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", timestamp = DateTime.UtcNow }))
   .WithName("HealthCheck");

// 1. GET /books: Danh sách sách
app.MapGet("/books", async (LibraryDbContext db) =>
{
    var books = await db.Books.OrderBy(b => b.Title).ToListAsync();
    return Results.Ok(books);
})
.WithName("GetBooks");

// 2. GET /books/{id}: Chi tiết sách và số bản khả dụng
app.MapGet("/books/{id:guid}", async (Guid id, LibraryDbContext db) =>
{
    var book = await db.Books.FindAsync(id);
    if (book is null)
    {
        return Results.NotFound(new { message = $"Khong tim thay sach co ID {id}" });
    }
    return Results.Ok(book);
})
.WithName("GetBookById");

// 3. POST /reservations: Gửi yêu cầu đặt mượn sách
app.MapPost("/reservations", async ([FromBody] CreateReservationRequest request, LibraryDbContext db, IMessagePublisher publisher, ILogger<Program> logger) =>
{
    var book = await db.Books.FindAsync(request.BookId);
    if (book is null)
    {
        return Results.NotFound(new { message = $"Khong tim thay sach co ID {request.BookId}" });
    }

    var user = await db.Users.FindAsync(request.UserId);
    if (user is null)
    {
        return Results.NotFound(new { message = $"Khong tim thay nguoi dung co ID {request.UserId}" });
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

    logger.LogInformation("[CorrelationId: {CorrelationId}] Created reservation request {ReservationId} with status Pending", correlationId, reservation.Id);

    // Đưa yêu cầu vào RabbitMQ để xử lý bất đồng bộ
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

        logger.LogInformation("[CorrelationId: {CorrelationId}] Reservation message dispatched to RabbitMQ", correlationId);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[CorrelationId: {CorrelationId}] Failed to enqueue message to RabbitMQ", correlationId);
    }

    return Results.Accepted($"/reservations/{reservation.Id}", new
    {
        reservation.Id,
        reservation.BookId,
        reservation.UserId,
        Status = reservation.Status.ToString(),
        reservation.CreatedAt,
        correlationId,
        message = "Yeu cau dat muon da duoc tiep nhan va dang duoc xu ly bat dong bo."
    });
})
.WithName("CreateReservation");

// 4. GET /reservations/{id}: Tra cứu trạng thái yêu cầu đặt mượn
app.MapGet("/reservations/{id:guid}", async (Guid id, LibraryDbContext db) =>
{
    var reservation = await db.Reservations
        .Include(r => r.Book)
        .Include(r => r.User)
        .FirstOrDefaultAsync(r => r.Id == id);

    if (reservation is null)
    {
        return Results.NotFound(new { message = $"Khong tim thay yeu cau dat muon co ID {id}" });
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

// 5. GET /users/{userId}/reservations: Tra cứu danh sách sách đã đặt/mượn của user
app.MapGet("/users/{userId:guid}/reservations", async (Guid userId, LibraryDbContext db) =>
{
    var userExists = await db.Users.AnyAsync(u => u.Id == userId);
    if (!userExists)
    {
        return Results.NotFound(new { message = $"Khong tim thay nguoi dung co ID {userId}" });
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

// GET /users: Danh sách người dùng mẫu để test tiện lợi
app.MapGet("/users", async (LibraryDbContext db) =>
{
    var users = await db.Users.OrderBy(u => u.FullName).ToListAsync();
    return Results.Ok(users);
})
.WithName("GetUsers");

// Cập nhật trạng thái sách: Mượn (Borrowed) hoặc Trả (Returned)
app.MapPut("/reservations/{id:guid}/status", async (Guid id, [FromBody] UpdateReservationStatusRequest req, LibraryDbContext db) =>
{
    var reservation = await db.Reservations.Include(r => r.Book).FirstOrDefaultAsync(r => r.Id == id);
    if (reservation is null) return Results.NotFound(new { message = "Khong tim thay yeu cau dat muon" });

    if (req.NewStatus == ReservationStatus.Borrowed)
    {
        if (reservation.Status != ReservationStatus.Reserved)
        {
            return Results.BadRequest(new { message = "Chi don o trang thai Reserved moi co the nhan sach (Borrowed)" });
        }
        reservation.Status = ReservationStatus.Borrowed;
        reservation.BorrowedAt = DateTime.UtcNow;
    }
    else if (req.NewStatus == ReservationStatus.Returned)
    {
        if (reservation.Status != ReservationStatus.Borrowed)
        {
            return Results.BadRequest(new { message = "Chi don o trang thai Borrowed moi co the tra sach (Returned)" });
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
        return Results.BadRequest(new { message = "Trang thai hop le la Borrowed hoac Returned" });
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
