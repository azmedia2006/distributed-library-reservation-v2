using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var apiUrl = builder.Configuration["ApiUrl"] ?? "http://library-api:5000";
if (!apiUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
{
    apiUrl = "http://" + apiUrl;
}

using var handler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
};

using var httpClient = new HttpClient(handler)
{
    BaseAddress = new Uri(apiUrl),
    Timeout = TimeSpan.FromSeconds(15)
};

static async Task<IResult> ProxyGetAsync(HttpClient client, HttpContext ctx, string path)
{
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (ctx.Request.Headers.TryGetValue("X-Correlation-ID", out var cid) && !string.IsNullOrWhiteSpace(cid))
        {
            req.Headers.TryAddWithoutValidation("X-Correlation-ID", cid.ToString());
        }

        using var res = await client.SendAsync(req, ctx.RequestAborted);
        if (res.Headers.TryGetValues("X-Correlation-ID", out var resCid))
        {
            ctx.Response.Headers["X-Correlation-ID"] = resCid.FirstOrDefault() ?? string.Empty;
        }

        var content = await res.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Content(content, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Json(new { message = $"Không thể kết nối tới Library API: {ex.Message}" }, statusCode: 502);
    }
}

static async Task<IResult> ProxyBodyAsync(HttpClient client, HttpContext ctx, HttpMethod method, string path)
{
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync(ctx.RequestAborted);

        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(string.IsNullOrWhiteSpace(body) ? "{}" : body, Encoding.UTF8, "application/json")
        };

        if (ctx.Request.Headers.TryGetValue("X-Correlation-ID", out var cid) && !string.IsNullOrWhiteSpace(cid))
        {
            req.Headers.TryAddWithoutValidation("X-Correlation-ID", cid.ToString());
        }

        using var res = await client.SendAsync(req, ctx.RequestAborted);
        if (res.Headers.TryGetValues("X-Correlation-ID", out var resCid))
        {
            ctx.Response.Headers["X-Correlation-ID"] = resCid.FirstOrDefault() ?? string.Empty;
        }

        var resContent = await res.Content.ReadAsStringAsync(ctx.RequestAborted);
        return Results.Content(resContent, "application/json", statusCode: (int)res.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Json(new { message = $"Không thể kết nối tới Library API: {ex.Message}" }, statusCode: 502);
    }
}

app.MapGet("/health", () => Results.Ok(new { service = "Library.WebClient", status = "Healthy", timestamp = DateTime.UtcNow }));
app.MapGet("/api/health", (HttpContext ctx) => ProxyGetAsync(httpClient, ctx, "/health"));
app.MapGet("/api/stats", (HttpContext ctx) => ProxyGetAsync(httpClient, ctx, "/stats"));

app.MapGet("/api/books", (HttpContext ctx, string? search, bool? availableOnly) =>
{
    var qs = new List<string>();
    if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
    if (availableOnly == true) qs.Add("availableOnly=true");
    var path = qs.Count == 0 ? "/books" : $"/books?{string.Join("&", qs)}";
    return ProxyGetAsync(httpClient, ctx, path);
});

app.MapGet("/api/books/{id:guid}", (Guid id, HttpContext ctx) => ProxyGetAsync(httpClient, ctx, $"/books/{id}"));
app.MapPost("/api/books", (HttpContext ctx) => ProxyBodyAsync(httpClient, ctx, HttpMethod.Post, "/books"));
app.MapPut("/api/books/{id:guid}", (Guid id, HttpContext ctx) => ProxyBodyAsync(httpClient, ctx, HttpMethod.Put, $"/books/{id}"));

app.MapGet("/api/users", (HttpContext ctx) => ProxyGetAsync(httpClient, ctx, "/users"));
app.MapPost("/api/users", (HttpContext ctx) => ProxyBodyAsync(httpClient, ctx, HttpMethod.Post, "/users"));

app.MapGet("/api/reservations", (HttpContext ctx, string? status, string? search) =>
{
    var qs = new List<string>();
    if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
    if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
    var path = qs.Count == 0 ? "/reservations" : $"/reservations?{string.Join("&", qs)}";
    return ProxyGetAsync(httpClient, ctx, path);
});

app.MapPost("/api/reservations", (HttpContext ctx) => ProxyBodyAsync(httpClient, ctx, HttpMethod.Post, "/reservations"));
app.MapGet("/api/reservations/{id:guid}", (Guid id, HttpContext ctx) => ProxyGetAsync(httpClient, ctx, $"/reservations/{id}"));

app.MapGet("/api/users/{userId:guid}/reservations", (Guid userId, HttpContext ctx, bool? activeOnly, string? status) =>
{
    var qs = new List<string>();
    if (activeOnly == true) qs.Add("activeOnly=true");
    if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
    var path = qs.Count == 0 ? $"/users/{userId}/reservations" : $"/users/{userId}/reservations?{string.Join("&", qs)}";
    return ProxyGetAsync(httpClient, ctx, path);
});

app.MapPut("/api/reservations/{id:guid}/status", (Guid id, HttpContext ctx) =>
    ProxyBodyAsync(httpClient, ctx, HttpMethod.Put, $"/reservations/{id}/status"));

app.MapPost("/api/reservations/{id:guid}/cancel", (Guid id, HttpContext ctx) =>
    ProxyBodyAsync(httpClient, ctx, HttpMethod.Post, $"/reservations/{id}/cancel"));

app.Run();
