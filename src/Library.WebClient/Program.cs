var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Proxy API URL config endpoint
var apiBaseUrl = builder.Configuration["ApiUrl"] ?? "http://localhost:5000";
app.MapGet("/config", () => Results.Ok(new { apiBaseUrl }));

app.Run();
