using Library.Infrastructure;
using Library.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LibraryDb")
    ?? "Host=localhost;Port=5432;Database=library_db;Username=postgres;Password=postgrespassword";

builder.Services.AddDbContext<LibraryDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
builder.Services.AddScoped<IReservationService, ReservationService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LibraryDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (int retry = 0; retry < 10; retry++)
    {
        try
        {
            await LibraryDbContext.SeedDataAsync(db);
            logger.LogInformation("Worker verified PostgreSQL connection and database schema.");
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Worker waiting for PostgreSQL (Attempt {Attempt}/10): {Message}", retry + 1, ex.Message);
            await Task.Delay(2000);
        }
    }
}

host.Run();
