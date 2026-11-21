using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var apiUrl = builder.Configuration["ApiUrl"] ?? "http://library-api:5000";
if (!apiUrl.StartsWith("http")) apiUrl = "http://" + apiUrl;
using var httpClient = new HttpClient { BaseAddress = new Uri(apiUrl) };

// Proxy GET /api/stats
app.MapGet("/api/stats", async () =>
{
    try
    {
        var res = await httpClient.GetAsync("/stats");
        var content = await res.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy GET /api/books (searchable)
app.MapGet("/api/books", async (string? search) =>
{
    try
    {
        var url = string.IsNullOrWhiteSpace(search) ? "/books" : $"/books?search={Uri.EscapeDataString(search)}";
        var res = await httpClient.GetAsync(url);
        var content = await res.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Lỗi kết nối tới Library API ({apiUrl}): {ex.Message}");
    }
});

// Proxy POST /api/books (Thêm sách mới)
app.MapPost("/api/books", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var res = await httpClient.PostAsync("/books", content);
        var resContent = await res.Content.ReadAsStringAsync();
        return Results.Content(resContent, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy GET /api/books/{id}
app.MapGet("/api/books/{id:guid}", async (Guid id) =>
{
    try
    {
        var res = await httpClient.GetAsync($"/books/{id}");
        var content = await res.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy GET /api/users
app.MapGet("/api/users", async () =>
{
    try
    {
        var res = await httpClient.GetAsync("/users");
        var content = await res.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Lỗi kết nối tới Library API ({apiUrl}): {ex.Message}");
    }
});

// Proxy POST /api/users (Thêm bạn đọc mới)
app.MapPost("/api/users", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var res = await httpClient.PostAsync("/users", content);
        var resContent = await res.Content.ReadAsStringAsync();
        return Results.Content(resContent, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy GET /api/reservations (Tất cả đơn)
app.MapGet("/api/reservations", async (string? status) =>
{
    try
    {
        var url = string.IsNullOrWhiteSpace(status) ? "/reservations" : $"/reservations?status={Uri.EscapeDataString(status)}";
        var res = await httpClient.GetAsync(url);
        var content = await res.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy POST /api/reservations
app.MapPost("/api/reservations", async (HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var res = await httpClient.PostAsync("/reservations", content);
        var resContent = await res.Content.ReadAsStringAsync();
        return Results.Content(resContent, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy GET /api/reservations/{id}
app.MapGet("/api/reservations/{id:guid}", async (Guid id) =>
{
    try
    {
        var res = await httpClient.GetAsync($"/reservations/{id}");
        var content = await res.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy GET /api/users/{userId}/reservations
app.MapGet("/api/users/{userId:guid}/reservations", async (Guid userId) =>
{
    try
    {
        var res = await httpClient.GetAsync($"/users/{userId}/reservations");
        var content = await res.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy PUT /api/reservations/{id}/status
app.MapPut("/api/reservations/{id:guid}/status", async (Guid id, HttpContext ctx) =>
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var res = await httpClient.PutAsync($"/reservations/{id}/status", content);
        var resContent = await res.Content.ReadAsStringAsync();
        return Results.Content(resContent, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// Proxy GET /api/health
app.MapGet("/api/health", async () =>
{
    try
    {
        var res = await httpClient.GetAsync("/health");
        var content = await res.Content.ReadAsStringAsync();
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();
