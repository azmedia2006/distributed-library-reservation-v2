using Library.Infrastructure;
using Library.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("LibraryDb")
    ?? "Host=localhost;Port=5432;Database=library_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<LibraryDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
