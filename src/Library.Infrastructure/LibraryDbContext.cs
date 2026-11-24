using Library.Domain;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure;

public class LibraryDbContext : DbContext
{
    public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Book>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Title).IsRequired().HasMaxLength(250);
            entity.Property(b => b.Author).IsRequired().HasMaxLength(150);
            entity.Property(b => b.ISBN).IsRequired().HasMaxLength(50);
            entity.HasIndex(b => b.ISBN);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.FullName).IsRequired().HasMaxLength(150);
            entity.Property(u => u.Email).IsRequired().HasMaxLength(150);
            entity.Property(u => u.PhoneNumber).HasMaxLength(30);
        });

        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.CorrelationId).HasMaxLength(100);
            entity.Property(r => r.ReasonMessage).HasMaxLength(500);
            entity.HasIndex(r => new { r.UserId, r.Status });
            entity.HasIndex(r => new { r.Status, r.CreatedAt });

            entity.HasOne(r => r.Book)
                  .WithMany()
                  .HasForeignKey(r => r.BookId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.User)
                  .WithMany()
                  .HasForeignKey(r => r.UserId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.CorrelationId).HasMaxLength(100);
            entity.Property(a => a.ServiceName).IsRequired().HasMaxLength(80);
            entity.Property(a => a.EventType).IsRequired().HasMaxLength(80);
            entity.Property(a => a.Message).IsRequired().HasMaxLength(600);
            entity.HasIndex(a => a.CreatedAt);
            entity.HasIndex(a => a.CorrelationId);
        });
    }

    public static async Task SeedDataAsync(LibraryDbContext context)
    {
        await context.Database.EnsureCreatedAsync();

        if (context.Database.IsRelational())
        {
            await context.Database.ExecuteSqlRawAsync(
                @"ALTER TABLE ""Reservations"" ADD COLUMN IF NOT EXISTS ""CorrelationId"" character varying(100);");

            await context.Database.ExecuteSqlRawAsync(
                @"CREATE TABLE IF NOT EXISTS ""AuditLogs"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ReservationId"" uuid NULL,
                    ""CorrelationId"" character varying(100) NOT NULL DEFAULT '',
                    ""ServiceName"" character varying(80) NOT NULL,
                    ""EventType"" character varying(80) NOT NULL,
                    ""Message"" character varying(600) NOT NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL
                );");

            await context.Database.ExecuteSqlRawAsync(
                @"CREATE INDEX IF NOT EXISTS ""IX_AuditLogs_CreatedAt"" ON ""AuditLogs"" (""CreatedAt"");");
        }

        if (!await context.Users.AnyAsync())
        {
            var users = new List<User>
            {
                new User
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    FullName = "Nguyễn Văn An",
                    Email = "an.nguyen@library.edu.vn",
                    PhoneNumber = "0901234567"
                },
                new User
                {
                    Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    FullName = "Trần Thị Bình",
                    Email = "binh.tran@library.edu.vn",
                    PhoneNumber = "0912345678"
                },
                new User
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    FullName = "Lê Hoàng Cường",
                    Email = "cuong.le@library.edu.vn",
                    PhoneNumber = "0987654321"
                }
            };
            await context.Users.AddRangeAsync(users);
        }

        if (!await context.Books.AnyAsync())
        {
            var books = new List<Book>
            {
                new Book
                {
                    Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    Title = "Designing Data-Intensive Applications",
                    Author = "Martin Kleppmann",
                    ISBN = "978-1449373320",
                    Description = "Kiến trúc cốt lõi của các hệ thống phân tán tin cậy, mở rộng cao và dễ bảo trì.",
                    TotalCopies = 3,
                    AvailableCopies = 3
                },
                new Book
                {
                    Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    Title = "Distributed Systems: Principles and Paradigms",
                    Author = "Andrew S. Tanenbaum, Maarten van Steen",
                    ISBN = "978-1543057386",
                    Description = "Giáo trình nền tảng về nguyên lý, đồng thuận phân tán, giao tiếp tiến trình và chịu lỗi.",
                    TotalCopies = 2,
                    AvailableCopies = 2
                },
                new Book
                {
                    Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    Title = "Building Microservices: Designing Fine-Grained Systems",
                    Author = "Sam Newman",
                    ISBN = "978-1492034025",
                    Description = "Thiết kế, tích hợp bất đồng bộ qua Message Broker và triển khai hệ thống vi dịch vụ (1 bản để kiểm thử chống Overbooking).",
                    TotalCopies = 1,
                    AvailableCopies = 1
                },
                new Book
                {
                    Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                    Title = "Clean Architecture: A Craftsman's Guide",
                    Author = "Robert C. Martin",
                    ISBN = "978-0134494166",
                    Description = "Nguyên tắc thiết kế kiến trúc phần mềm phân tầng độc lập hạ tầng.",
                    TotalCopies = 4,
                    AvailableCopies = 4
                },
                new Book
                {
                    Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    Title = "Enterprise Integration Patterns",
                    Author = "Gregor Hohpe, Bobby Woolf",
                    ISBN = "978-0321200686",
                    Description = "Các mẫu tích hợp thông điệp chuẩn: Message Queue, Correlation Identifier, Dead Letter Channel và Idempotent Receiver.",
                    TotalCopies = 2,
                    AvailableCopies = 2
                }
            };
            await context.Books.AddRangeAsync(books);
        }

        await context.SaveChangesAsync();
    }
}
