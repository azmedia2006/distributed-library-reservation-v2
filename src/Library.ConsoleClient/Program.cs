using System.Net.Http.Json;

var baseUrl = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:5005";
using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("==========================================================================");
Console.WriteLine("   HỆ THỐNG THƯ VIỆN & ĐẶT MƯỢN SÁCH PHÂN TÁN — CONSOLE CLIENT (.NET 10)  ");
Console.WriteLine($"   Kết nối Library API tại: {baseUrl}");
Console.WriteLine("==========================================================================");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== MENU CHỨC NĂNG ===");
    Console.WriteLine("1. Tra cứu danh sách sách & lọc theo từ khóa / còn bản (GET /books)");
    Console.WriteLine("2. Xem chi tiết sách & thống kê giữ/mượn (GET /books/{id})");
    Console.WriteLine("3. Danh sách bạn đọc / Đăng ký bạn đọc mới (GET, POST /users)");
    Console.WriteLine("4. Gửi yêu cầu đặt mượn sách bất đồng bộ qua RabbitMQ (POST /reservations)");
    Console.WriteLine("5. Tra cứu trạng thái & Correlation ID của đơn đặt mượn (GET /reservations/{id})");
    Console.WriteLine("6. Tra cứu sách đang đặt/mượn hoặc toàn bộ lịch sử bạn đọc (GET /users/{id}/reservations)");
    Console.WriteLine("7. Cập nhật Nhận sách (Borrowed) / Trả sách (Returned) / Hủy đơn");
    Console.WriteLine("8. Kiểm tra Health Check (PostgreSQL + RabbitMQ) & Thống kê tổng quan");
    Console.WriteLine("9. Mô phỏng Đặt mượn Đồng thời — Kiểm thử chống Overbooking (Burst Test)");
    Console.WriteLine("0. Thoát");
    Console.Write("Chọn thao tác [0-9]: ");

    var choice = Console.ReadLine()?.Trim();
    if (choice == "0") break;

    try
    {
        switch (choice)
        {
            case "1":
                await ListBooksAsync();
                break;
            case "2":
                await GetBookDetailAsync();
                break;
            case "3":
                await ManageUsersAsync();
                break;
            case "4":
                await CreateReservationAsync();
                break;
            case "5":
                await LookupReservationAsync();
                break;
            case "6":
                await ListUserReservationsAsync();
                break;
            case "7":
                await UpdateOrCancelReservationAsync();
                break;
            case "8":
                await CheckHealthAndStatsAsync();
                break;
            case "9":
                await RunBurstConcurrencyTestAsync();
                break;
            default:
                Console.WriteLine("Lựa chọn không hợp lệ, vui lòng nhập từ 0 đến 9.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Lỗi thực thi: {ex.Message}");
        Console.ResetColor();
    }
}

async Task ListBooksAsync()
{
    Console.Write("Từ khóa tìm kiếm (Enter để bỏ qua): ");
    var search = Console.ReadLine()?.Trim();
    Console.Write("Chỉ hiện sách còn bản khả dụng? [y/N]: ");
    var onlyAvail = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

    var qs = new List<string>();
    if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
    if (onlyAvail) qs.Add("availableOnly=true");
    var url = qs.Count == 0 ? "/books" : $"/books?{string.Join("&", qs)}";

    var books = await httpClient.GetFromJsonAsync<List<BookDto>>(url);
    if (books == null || books.Count == 0)
    {
        Console.WriteLine("Không có đầu sách nào khớp điều kiện.");
        return;
    }

    Console.WriteLine("\n--- DANH MỤC SÁCH THƯ VIỆN ---");
    Console.WriteLine($"{"ID",-38} | {"Tiêu đề",-36} | {"Tác giả",-20} | {"Khả dụng",-10}");
    Console.WriteLine(new string('-', 112));
    foreach (var b in books)
    {
        Console.WriteLine($"{b.Id,-38} | {Truncate(b.Title, 36),-36} | {Truncate(b.Author, 20),-20} | {b.AvailableCopies}/{b.TotalCopies,-8}");
    }
}

async Task GetBookDetailAsync()
{
    Console.Write("Nhập Book ID (GUID): ");
    if (!Guid.TryParse(Console.ReadLine()?.Trim(), out var id))
    {
        Console.WriteLine("GUID không hợp lệ!");
        return;
    }

    var response = await httpClient.GetAsync($"/books/{id}");
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"Không tìm thấy sách ({response.StatusCode}).");
        return;
    }

    var book = await response.Content.ReadFromJsonAsync<BookDetailDto>();
    if (book != null)
    {
        Console.WriteLine("\n--- CHI TIẾT ĐẦU SÁCH ---");
        Console.WriteLine($"ID:             {book.Id}");
        Console.WriteLine($"Tiêu đề:        {book.Title}");
        Console.WriteLine($"Tác giả:        {book.Author}");
        Console.WriteLine($"ISBN:           {book.ISBN}");
        Console.WriteLine($"Tổng số bản:    {book.TotalCopies}");
        Console.WriteLine($"Bản khả dụng:   {book.AvailableCopies}");
        Console.WriteLine($"Đang giữ/mượn:  {book.ReservedCount} Reserved | {book.BorrowedCount} Borrowed | {book.PendingCount} Pending");
        Console.WriteLine($"Mô tả:          {book.Description}");
    }
}

async Task ManageUsersAsync()
{
    var users = await httpClient.GetFromJsonAsync<List<UserDto>>("/users") ?? new List<UserDto>();
    Console.WriteLine("\n--- DANH SÁCH BẠN ĐỌC ---");
    Console.WriteLine($"{"User ID",-38} | {"Họ và tên",-25} | {"Email",-30}");
    Console.WriteLine(new string('-', 100));
    foreach (var u in users)
    {
        Console.WriteLine($"{u.Id,-38} | {Truncate(u.FullName, 25),-25} | {u.Email,-30}");
    }

    Console.Write("\nBạn có muốn đăng ký thêm bạn đọc mới không? [y/N]: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return;

    Console.Write("Họ và tên: ");
    var name = Console.ReadLine()?.Trim() ?? "";
    Console.Write("Email: ");
    var email = Console.ReadLine()?.Trim() ?? "";
    Console.Write("Số điện thoại: ");
    var phone = Console.ReadLine()?.Trim() ?? "";

    var res = await httpClient.PostAsJsonAsync("/users", new { FullName = name, Email = email, PhoneNumber = phone });
    var body = await res.Content.ReadAsStringAsync();
    Console.WriteLine(res.IsSuccessStatusCode ? $"[THÀNH CÔNG] {body}" : $"[LỖI] {body}");
}

async Task CreateReservationAsync()
{
    Console.Write("Nhập User ID (GUID): ");
    if (!Guid.TryParse(Console.ReadLine()?.Trim(), out var userId)) { Console.WriteLine("User ID không hợp lệ!"); return; }

    Console.Write("Nhập Book ID (GUID): ");
    if (!Guid.TryParse(Console.ReadLine()?.Trim(), out var bookId)) { Console.WriteLine("Book ID không hợp lệ!"); return; }

    var res = await httpClient.PostAsJsonAsync("/reservations", new { BookId = bookId, UserId = userId });
    var content = await res.Content.ReadAsStringAsync();

    if (res.IsSuccessStatusCode)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[202 ACCEPTED] Đã tiếp nhận yêu cầu vào RabbitMQ: {content}");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[LỖI] {content}");
        Console.ResetColor();
    }
}

async Task LookupReservationAsync()
{
    Console.Write("Nhập Reservation ID (GUID): ");
    if (!Guid.TryParse(Console.ReadLine()?.Trim(), out var resId)) { Console.WriteLine("GUID không hợp lệ!"); return; }

    var res = await httpClient.GetAsync($"/reservations/{resId}");
    if (!res.IsSuccessStatusCode)
    {
        Console.WriteLine($"Không tìm thấy đơn đặt mượn ({res.StatusCode}).");
        return;
    }

    var detail = await res.Content.ReadFromJsonAsync<ReservationDetailDto>();
    if (detail != null)
    {
        Console.WriteLine("\n--- THÔNG TIN ĐƠN ĐẶT MƯỢN ---");
        Console.WriteLine($"Mã đơn:        {detail.Id}");
        Console.WriteLine($"CorrelationId: {detail.CorrelationId}");
        Console.WriteLine($"Sách:          {detail.BookTitle} (ID: {detail.BookId})");
        Console.WriteLine($"Bạn đọc:       {detail.UserName} (ID: {detail.UserId})");
        Console.WriteLine($"Trạng thái:    {detail.Status}");
        Console.WriteLine($"Ghi chú:       {detail.ReasonMessage}");
        Console.WriteLine($"Tạo lúc:       {detail.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"Worker duyệt:  {detail.ProcessedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"Nhận sách:     {detail.BorrowedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"Trả sách:      {detail.ReturnedAt:yyyy-MM-dd HH:mm:ss} UTC");
    }
}

async Task ListUserReservationsAsync()
{
    Console.Write("Nhập User ID (GUID): ");
    if (!Guid.TryParse(Console.ReadLine()?.Trim(), out var userId)) { Console.WriteLine("User ID không hợp lệ!"); return; }

    Console.Write("Chỉ xem các sách ĐANG ĐẶT / ĐANG MƯỢN (activeOnly)? [y/N]: ");
    var activeOnly = string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);

    var url = activeOnly ? $"/users/{userId}/reservations?activeOnly=true" : $"/users/{userId}/reservations";
    var res = await httpClient.GetAsync(url);
    if (!res.IsSuccessStatusCode)
    {
        Console.WriteLine($"Không lấy được danh sách ({res.StatusCode}).");
        return;
    }

    var list = await res.Content.ReadFromJsonAsync<List<ReservationDetailDto>>();
    if (list == null || list.Count == 0)
    {
        Console.WriteLine("Không có đơn đặt mượn nào.");
        return;
    }

    Console.WriteLine($"\n--- DANH SÁCH ĐƠN CỦA BẠN ĐỌC ({list.Count} đơn) ---");
    foreach (var r in list)
    {
        Console.WriteLine($"- [{r.Status,-8}] Mã: {r.Id} | Sách: {r.BookTitle}");
        Console.WriteLine($"  Trace: {r.CorrelationId} | Ghi chú: {r.ReasonMessage}");
    }
}

async Task UpdateOrCancelReservationAsync()
{
    Console.Write("Nhập Reservation ID (GUID): ");
    if (!Guid.TryParse(Console.ReadLine()?.Trim(), out var resId)) { Console.WriteLine("GUID không hợp lệ!"); return; }

    Console.WriteLine("Chọn thao tác:");
    Console.WriteLine("  1. Borrowed (Bạn đọc nhận sách)");
    Console.WriteLine("  2. Returned (Bạn đọc trả sách — hoàn lại số bản khả dụng)");
    Console.WriteLine("  3. Cancel   (Hủy đơn đang Pending/Reserved)");
    Console.Write("Chọn [1-3]: ");
    var op = Console.ReadLine()?.Trim();

    HttpResponseMessage res;
    if (op == "1" || op == "2")
    {
        var statusName = op == "1" ? "Borrowed" : "Returned";
        res = await httpClient.PutAsJsonAsync($"/reservations/{resId}/status", new { NewStatus = statusName });
    }
    else if (op == "3")
    {
        res = await httpClient.PostAsJsonAsync($"/reservations/{resId}/cancel", new { Reason = "Hủy từ Console Client." });
    }
    else
    {
        Console.WriteLine("Lựa chọn không hợp lệ.");
        return;
    }

    var body = await res.Content.ReadAsStringAsync();
    Console.WriteLine(res.IsSuccessStatusCode ? $"[THÀNH CÔNG] {body}" : $"[LỖI] {body}");
}

async Task CheckHealthAndStatsAsync()
{
    var healthBody = await httpClient.GetStringAsync("/health");
    var statsBody = await httpClient.GetStringAsync("/stats");
    Console.WriteLine($"\n[HEALTH CHECK] {healthBody}");
    Console.WriteLine($"[THỐNG KÊ]     {statsBody}");
}

async Task RunBurstConcurrencyTestAsync()
{
    Console.Write("Nhập User ID (GUID, Enter để dùng mặc định 11111111-...): ");
    var uInput = Console.ReadLine()?.Trim();
    var userId = string.IsNullOrWhiteSpace(uInput) ? Guid.Parse("11111111-1111-1111-1111-111111111111") : Guid.Parse(uInput);

    Console.Write("Nhập Book ID (GUID, Enter để dùng sách 1 bản cccccccc-...): ");
    var bInput = Console.ReadLine()?.Trim();
    var bookId = string.IsNullOrWhiteSpace(bInput) ? Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc") : Guid.Parse(bInput);

    Console.Write("Số lượng yêu cầu bắn song song [mặc định 5]: ");
    var nInput = Console.ReadLine()?.Trim();
    var count = int.TryParse(nInput, out var parsedCount) && parsedCount > 0 ? Math.Min(parsedCount, 20) : 5;

    Console.WriteLine($"\nĐang gửi đồng thời {count} yêu cầu POST /reservations lên API...");
    var tasks = Enumerable.Range(1, count).Select(async _ =>
    {
        var r = await httpClient.PostAsJsonAsync("/reservations", new { BookId = bookId, UserId = userId });
        return await r.Content.ReadFromJsonAsync<AcceptedReservationDto>();
    });

    var accepted = (await Task.WhenAll(tasks)).Where(x => x != null).ToList();
    Console.WriteLine($"Đã xếp hàng {accepted.Count}/{count} yêu cầu (202 Accepted). Đang chờ Worker xử lý qua RabbitMQ (2.5s)...");
    await Task.Delay(2500);

    int reserved = 0, rejected = 0, pending = 0;
    foreach (var item in accepted)
    {
        var detail = await httpClient.GetFromJsonAsync<ReservationDetailDto>($"/reservations/{item!.Id}");
        if (detail == null) continue;
        if (detail.Status == "Reserved") reserved++;
        else if (detail.Status == "Rejected") rejected++;
        else pending++;
        Console.WriteLine($" - Đơn {detail.Id} | Trạng thái: {detail.Status,-8} | {detail.ReasonMessage}");
    }

    var bookAfter = await httpClient.GetFromJsonAsync<BookDetailDto>($"/books/{bookId}");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"\n[KẾT QUẢ CHỐNG OVERBOOKING] Duyệt giữ chỗ: {reserved} | Từ chối hết sách: {rejected} | Còn chờ: {pending}");
    Console.WriteLine($"[TỒN KHO SAU XỬ LÝ]         AvailableCopies = {bookAfter?.AvailableCopies}/{bookAfter?.TotalCopies} (Không bao giờ âm!)");
    Console.ResetColor();
}

static string Truncate(string val, int max) =>
    string.IsNullOrEmpty(val) ? "" : (val.Length <= max ? val : val.Substring(0, max - 3) + "...");

public record BookDto(Guid Id, string Title, string Author, string ISBN, int TotalCopies, int AvailableCopies, string Description);
public record BookDetailDto(Guid Id, string Title, string Author, string ISBN, int TotalCopies, int AvailableCopies, string Description, int PendingCount, int ReservedCount, int BorrowedCount);
public record UserDto(Guid Id, string FullName, string Email, string PhoneNumber);
public record AcceptedReservationDto(Guid Id, string Status, string CorrelationId);
public record ReservationDetailDto(Guid Id, Guid BookId, string? BookTitle, Guid UserId, string? UserName, string Status, string? ReasonMessage, string? CorrelationId, DateTime CreatedAt, DateTime? ProcessedAt, DateTime? BorrowedAt, DateTime? ReturnedAt);
