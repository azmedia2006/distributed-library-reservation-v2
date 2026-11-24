using Library.Domain;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure;

public class LibraryDbContext : DbContext
{
    public LibraryDbContext(DbContextOptions<LibraryDbContext> options) : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Book>(entity =>
        {
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Title).IsRequired().HasMaxLength(250);
            entity.Property(b => b.Author).IsRequired().HasMaxLength(150);
            entity.Property(b => b.ISBN).IsRequired().HasMaxLength(50);
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.FullName).IsRequired().HasMaxLength(150);
            entity.Property(u => u.Email).IsRequired().HasMaxLength(150);
        });

        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasOne(r => r.Book)
                  .WithMany()
                  .HasForeignKey(r => r.BookId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(r => r.User)
                  .WithMany()
                  .HasForeignKey(r => r.UserId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static async Task SeedDataAsync(LibraryDbContext context)
    {
        await context.Database.EnsureCreatedAsync();

        if (!await context.Users.AnyAsync())
        {
            var user1 = new User
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                FullName = "Nguyễn Văn An",
                Email = "an.nguyen@example.com",
                PhoneNumber = "0901234567"
            };
            var user2 = new User
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                FullName = "Trần Thị Bình",
                Email = "binh.tran@example.com",
                PhoneNumber = "0912345678"
            };
            await context.Users.AddRangeAsync(user1, user2);
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
                    Description = "The big ideas behind reliable, scalable, and maintainable systems.",
                    TotalCopies = 3,
                    AvailableCopies = 3
                },
                new Book
                {
                    Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    Title = "Distributed Systems: Principles and Paradigms",
                    Author = "Andrew S. Tanenbaum, Maarten van Steen",
                    ISBN = "978-1543057386",
                    Description = "Comprehensive introduction to concepts and principles of distributed systems.",
                    TotalCopies = 2,
                    AvailableCopies = 2
                },
                new Book
                {
                    Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                    Title = "Building Microservices: Designing Fine-Grained Systems",
                    Author = "Sam Newman",
                    ISBN = "978-1492034025",
                    Description = "Key concepts in modeling, integrating, testing, deploying, and monitoring microservices.",
                    TotalCopies = 1,
                    AvailableCopies = 1
                },
                new Book
                {
                    Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                    Title = "Clean Architecture: A Craftsman's Guide",
                    Author = "Robert C. Martin",
                    ISBN = "978-0134494166",
                    Description = "A timeless guide on software structure and design.",
                    TotalCopies = 4,
                    AvailableCopies = 4
                }
            };
            await context.Books.AddRangeAsync(books);
        }

        await context.SaveChangesAsync();
    }
}
