using System.Text.Json.Serialization;
using Library.Domain;
using Library.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LibraryDb")
    ?? "Host=localhost;Port=5432;Database=library_db;Username=postgres;Password=postgrespassword";

builder.Services.AddDbContext<LibraryDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
builder.Services.AddScoped<IReservationService, ReservationService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "Hệ Thống Thư Viện & Đặt Mượn Sách Phân Tán API",
        Version = "v2.0",
        Description = "ASP.NET Core Minimal API kết hợp RabbitMQ, Background Worker, Retry + Dead Letter Queue và PostgreSQL."
    });
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
    for (int retry = 0; retry < 10; retry++)
    {
        try
        {
            await LibraryDbContext.SeedDataAsync(db);
            logger.LogInformation("Database connected, schema verified, and seed data initialized.");
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Waiting for PostgreSQL to become ready (Attempt {Attempt}/10): {Message}", retry + 1, ex.Message);
            await Task.Delay(2000);
        }
    }
}

app.UseCors();

// Correlation ID Middleware xuyên suốt toàn bộ request/response
app.Use(async (context, next) =>
{
    var incomingId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    var correlationId = string.IsNullOrWhiteSpace(incomingId) ? Guid.NewGuid().ToString() : incomingId.Trim();
    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;

    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
    {
        await next();
    }
});

app.UseSwagger();
app.UseSwaggerUI();

// 0. GET /health: Kiểm tra tình trạng thực tế của PostgreSQL và RabbitMQ
app.MapGet("/health", async (LibraryDbContext db, IMessagePublisher publisher) =>
{
    bool dbHealthy = false;
    try
    {
        dbHealthy = await db.Database.CanConnectAsync();
    }
    catch
    {
        dbHealthy = false;
    }

    bool mqHealthy = publisher.CheckHealth();
    bool overallHealthy = dbHealthy && mqHealthy;

    return Results.Ok(new
    {
        status = overallHealthy ? "Healthy" : "Degraded",
        database = dbHealthy ? "Connected" : "Disconnected",
        rabbitMq = mqHealthy ? "Connected" : "Disconnected",
        timestamp = DateTime.UtcNow
    });
})
.WithName("HealthCheck");

// GET /stats: Thống kê tổng quan cho Dashboard
app.MapGet("/stats", async (LibraryDbContext db) =>
{
    var totalBooks = await db.Books.CountAsync();
    var totalCopies = await db.Books.SumAsync(b => (int?)b.TotalCopies) ?? 0;
    var availableCopies = await db.Books.SumAsync(b => (int?)b.AvailableCopies) ?? 0;
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
        borrowedCopies = Math.Max(0, totalCopies - availableCopies),
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

// 1. GET /books: Danh sách sách (hỗ trợ tìm kiếm và lọc sách còn bản khả dụng)
app.MapGet("/books", async ([FromQuery] string? search, [FromQuery] bool? availableOnly, LibraryDbContext db) =>
{
    var query = db.Books.AsNoTracking().AsQueryable();

    if (!string.IsNullOrWhiteSpace(search))
    {
        var keyword = search.Trim().ToLower();
        query = query.Where(b =>
            b.Title.ToLower().Contains(keyword) ||
            b.Author.ToLower().Contains(keyword) ||
            b.ISBN.ToLower().Contains(keyword));
    }

    if (availableOnly == true)
    {
        query = query.Where(b => b.AvailableCopies > 0);
    }

    var books = await query.OrderBy(b => b.Title).ToListAsync();
    return Results.Ok(books);
})
.WithName("GetBooks");

// 2. GET /books/{id}: Chi tiết sách và số bản đang khả dụng
app.MapGet("/books/{id:guid}", async (Guid id, LibraryDbContext db) =>
{
    var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
    if (book is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy sách có ID {id}" });
    }

    var activeReservedCount = await db.Reservations.CountAsync(r => r.BookId == id && r.Status == ReservationStatus.Reserved);
    var activeBorrowedCount = await db.Reservations.CountAsync(r => r.BookId == id && r.Status == ReservationStatus.Borrowed);
    var pendingCount = await db.Reservations.CountAsync(r => r.BookId == id && r.Status == ReservationStatus.Pending);

    return Results.Ok(new
    {
        book.Id,
        book.Title,
        book.Author,
        book.ISBN,
        book.Description,
        book.TotalCopies,
        book.AvailableCopies,
        book.CreatedAt,
        pendingCount,
        reservedCount = activeReservedCount,
        borrowedCount = activeBorrowedCount
    });
})
.WithName("GetBookById");

// POST /books: Thêm đầu sách mới
app.MapPost("/books", async ([FromBody] CreateBookRequest req, LibraryDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Title))
    {
        return Results.BadRequest(new { message = "Tiêu đề sách không được để trống." });
    }

    var copies = req.TotalCopies > 0 ? req.TotalCopies : 1;
    var book = new Book
    {
        Id = Guid.NewGuid(),
        Title = req.Title.Trim(),
        Author = string.IsNullOrWhiteSpace(req.Author) ? "Chưa rõ" : req.Author.Trim(),
        ISBN = req.ISBN?.Trim() ?? string.Empty,
        Description = req.Description?.Trim() ?? string.Empty,
        TotalCopies = copies,
        AvailableCopies = copies,
        CreatedAt = DateTime.UtcNow
    };

    db.Books.Add(book);
    await db.SaveChangesAsync();
    return Results.Created($"/books/{book.Id}", book);
})
.WithName("CreateBook");

// PUT /books/{id}: Cập nhật thông tin sách và bổ sung số lượng bản
app.MapPut("/books/{id:guid}", async (Guid id, [FromBody] UpdateBookRequest req, LibraryDbContext db) =>
{
    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy sách có ID {id}" });
    }

    if (!string.IsNullOrWhiteSpace(req.Title))
    {
        book.Title = req.Title.Trim();
    }
    if (req.Author is not null)
    {
        book.Author = string.IsNullOrWhiteSpace(req.Author) ? "Chưa rõ" : req.Author.Trim();
    }
    if (req.ISBN is not null)
    {
        book.ISBN = req.ISBN.Trim();
    }
    if (req.Description is not null)
    {
        book.Description = req.Description.Trim();
    }

    if (req.TotalCopies.HasValue)
    {
        var currentlyCheckedOut = Math.Max(0, book.TotalCopies - book.AvailableCopies);
        if (req.TotalCopies.Value < currentlyCheckedOut)
        {
            return Results.BadRequest(new
            {
                message = $"Tổng số bản mới ({req.TotalCopies.Value}) không thể nhỏ hơn số bản đang được giữ/mượn ({currentlyCheckedOut})."
            });
        }

        book.TotalCopies = req.TotalCopies.Value;
        book.AvailableCopies = book.TotalCopies - currentlyCheckedOut;
    }

    await db.SaveChangesAsync();
    return Results.Ok(book);
})
.WithName("UpdateBook");

// 3. POST /reservations: Tiếp nhận yêu cầu đặt mượn sách và đưa vào RabbitMQ
app.MapPost("/reservations", async (
    HttpContext httpContext,
    [FromBody] CreateReservationRequest request,
    LibraryDbContext db,
    IMessagePublisher publisher,
    ILogger<Program> logger) =>
{
    if (request.BookId == Guid.Empty || request.UserId == Guid.Empty)
    {
        return Results.BadRequest(new { message = "BookId và UserId không hợp lệ." });
    }

    var book = await db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BookId);
    if (book is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy sách có ID {request.BookId}" });
    }

    var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId);
    if (user is null)
    {
        return Results.NotFound(new { message = $"Không tìm thấy bạn đọc có ID {request.UserId}" });
    }

    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();

    var reservation = new Reservation
    {
        Id = Guid.NewGuid(),
        BookId = request.BookId,
        UserId = request.UserId,
        Status = ReservationStatus.Pending,
        CorrelationId = correlationId,
        ReasonMessage = "Yêu cầu đang xếp hàng chờ Reservation Worker kiểm tra số bản khả dụng.",
        CreatedAt = DateTime.UtcNow
    };

    db.Reservations.Add(reservation);
    await db.SaveChangesAsync();

    logger.LogInformation(
        "[CorrelationId: {CorrelationId}] Accepted reservation {ReservationId} (Pending) for Book {BookId} by User {UserId}",
        correlationId, reservation.Id, reservation.BookId, reservation.UserId);

    var queueMessage = new ReservationRequestedMessage
    {
        ReservationId = reservation.Id,
        BookId = reservation.BookId,
        UserId = reservation.UserId,
        RequestedAt = reservation.CreatedAt,
        CorrelationId = correlationId
    };

    bool publishedToQueue = false;
    try
    {
        await publisher.PublishAsync(
            RabbitMqConstants.ExchangeName,
            RabbitMqConstants.ReservationRoutingKey,
            queueMessage,
            correlationId);

        publishedToQueue = true;
    }
    catch (Exception ex)
    {
        logger.LogError(
            ex,
            "[CorrelationId: {CorrelationId}] RabbitMQ temporarily unavailable while publishing {ReservationId}. Worker recovery sweep will process Pending record.",
            correlationId, reservation.Id);
    }

    return Results.Accepted($"/reservations/{reservation.Id}", new
    {
        reservation.Id,
        reservation.BookId,
        BookTitle = book.Title,
        reservation.UserId,
        UserName = user.FullName,
        Status = reservation.Status.ToString(),
        reservation.CreatedAt,
        correlationId,
        queued = publishedToQueue,
        message = publishedToQueue
            ? "Yêu cầu đặt mượn đã được tiếp nhận và đưa vào hàng đợi RabbitMQ xử lý bất đồng bộ."
            : "Yêu cầu đặt mượn đã được lưu trạng thái Pending; hệ thống sẽ tự động xử lý ngay khi hàng đợi phục hồi."
    });
})
.WithName("CreateReservation");

// 4. GET /reservations/{id}: Tra cứu trạng thái yêu cầu đặt mượn theo ID
app.MapGet("/reservations/{id:guid}", async (Guid id, LibraryDbContext db) =>
{
    var reservation = await db.Reservations
        .AsNoTracking()
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
        BookISBN = reservation.Book?.ISBN,
        AvailableCopies = reservation.Book?.AvailableCopies,
        reservation.UserId,
        UserName = reservation.User?.FullName,
        UserEmail = reservation.User?.Email,
        UserPhone = reservation.User?.PhoneNumber,
        Status = reservation.Status.ToString(),
        reservation.ReasonMessage,
        reservation.CorrelationId,
        reservation.CreatedAt,
        reservation.ProcessedAt,
        reservation.BorrowedAt,
        reservation.ReturnedAt
    });
})
.WithName("GetReservationById");

// GET /reservations: Danh sách tất cả đơn đặt mượn trong hệ thống (Quản trị thư viện)
app.MapGet("/reservations", async ([FromQuery] string? status, [FromQuery] string? search, LibraryDbContext db) =>
{
    var query = db.Reservations
        .AsNoTracking()
        .Include(r => r.Book)
        .Include(r => r.User)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ReservationStatus>(status, true, out var parsedStatus))
    {
        query = query.Where(r => r.Status == parsedStatus);
    }

    if (!string.IsNullOrWhiteSpace(search))
    {
        var kw = search.Trim().ToLower();
        query = query.Where(r =>
            (r.Book != null && r.Book.Title.ToLower().Contains(kw)) ||
            (r.User != null && r.User.FullName.ToLower().Contains(kw)) ||
            (r.CorrelationId != null && r.CorrelationId.ToLower().Contains(kw)));
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
            r.CorrelationId,
            r.CreatedAt,
            r.ProcessedAt,
            r.BorrowedAt,
            r.ReturnedAt
        })
        .ToListAsync();

    return Results.Ok(list);
})
.WithName("GetAllReservations");

// 5. GET /users/{userId}/reservations: Danh sách sách đang mượn / đã đặt hoặc toàn bộ lịch sử của bạn đọc
app.MapGet("/users/{userId:guid}/reservations", async (
    Guid userId,
    [FromQuery] bool? activeOnly,
    [FromQuery] string? status,
    LibraryDbContext db) =>
{
    var userExists = await db.Users.AnyAsync(u => u.Id == userId);
    if (!userExists)
    {
        return Results.NotFound(new { message = $"Không tìm thấy bạn đọc có ID {userId}" });
    }

    var query = db.Reservations
        .AsNoTracking()
        .Include(r => r.Book)
        .Where(r => r.UserId == userId);

    if (activeOnly == true)
    {
        query = query.Where(r =>
            r.Status == ReservationStatus.Pending ||
            r.Status == ReservationStatus.Reserved ||
            r.Status == ReservationStatus.Borrowed);
    }
    else if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ReservationStatus>(status, true, out var parsedStatus))
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
            BookAuthor = r.Book != null ? r.Book.Author : string.Empty,
            r.UserId,
            Status = r.Status.ToString(),
            r.ReasonMessage,
            r.CorrelationId,
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
    var users = await db.Users.AsNoTracking().OrderBy(u => u.FullName).ToListAsync();
    return Results.Ok(users);
})
.WithName("GetUsers");

// POST /users: Thêm bạn đọc mới
app.MapPost("/users", async ([FromBody] CreateUserRequest req, LibraryDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.FullName))
    {
        return Results.BadRequest(new { message = "Họ và tên bạn đọc không được để trống." });
    }

    var email = req.Email?.Trim() ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(email))
    {
        var emailExists = await db.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower());
        if (emailExists)
        {
            return Results.BadRequest(new { message = $"Email '{email}' đã được đăng ký cho bạn đọc khác." });
        }
    }

    var user = new User
    {
        Id = Guid.NewGuid(),
        FullName = req.FullName.Trim(),
        Email = email,
        PhoneNumber = req.PhoneNumber?.Trim() ?? string.Empty,
        CreatedAt = DateTime.UtcNow
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Created($"/users/{user.Id}", user);
})
.WithName("CreateUser");

// 6. PUT /reservations/{id}/status: Cập nhật trạng thái nhận sách (Borrowed) hoặc trả sách (Returned)
app.MapPut("/reservations/{id:guid}/status", async (
    Guid id,
    [FromBody] UpdateReservationStatusRequest req,
    IReservationService reservationService) =>
{
    var result = await reservationService.UpdateStatusAsync(id, req.NewStatus);
    if (result.NotFound)
    {
        return Results.NotFound(new { message = result.ErrorMessage });
    }

    if (!result.Success || result.Reservation is null)
    {
        return Results.BadRequest(new { message = result.ErrorMessage });
    }

    return Results.Ok(new
    {
        result.Reservation.Id,
        Status = result.Reservation.Status.ToString(),
        result.Reservation.ReasonMessage,
        result.Reservation.BorrowedAt,
        result.Reservation.ReturnedAt,
        AvailableCopies = result.AvailableCopies
    });
})
.WithName("UpdateReservationStatus");

// POST /reservations/{id}/cancel: Hủy đơn đặt mượn đang Pending hoặc Reserved (tự hoàn lại bản sách nếu đã Reserved)
app.MapPost("/reservations/{id:guid}/cancel", async (
    Guid id,
    [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] CancelReservationRequest? req,
    IReservationService reservationService) =>
{
    var result = await reservationService.CancelReservationAsync(id, req?.Reason);
    if (result.NotFound)
    {
        return Results.NotFound(new { message = result.ErrorMessage });
    }

    if (!result.Success || result.Reservation is null)
    {
        return Results.BadRequest(new { message = result.ErrorMessage });
    }

    return Results.Ok(new
    {
        result.Reservation.Id,
        Status = result.Reservation.Status.ToString(),
        result.Reservation.ReasonMessage,
        AvailableCopies = result.AvailableCopies
    });
})
.WithName("CancelReservation");

app.Run();

public record CreateReservationRequest(Guid BookId, Guid UserId);
public record UpdateReservationStatusRequest(ReservationStatus NewStatus);
public record CancelReservationRequest(string? Reason);
public record CreateBookRequest(string Title, string? Author, string? ISBN, string? Description, int TotalCopies);
public record UpdateBookRequest(string? Title, string? Author, string? ISBN, string? Description, int? TotalCopies);
public record CreateUserRequest(string FullName, string? Email, string? PhoneNumber);
