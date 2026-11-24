using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var baseUrl = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:5005";
using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("==================================================================");
Console.WriteLine("    HỆ THỐNG THƯ VIỆN & ĐẶT MƯỢN SÁCH PHÂN TÁN (CONSOLE CLIENT)    ");
Console.WriteLine($"    Kết nối API tại: {baseUrl}");
Console.WriteLine("==================================================================");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== MENU CHỨC NĂNG ===");
    Console.WriteLine("1. Xem danh sách sách và số bản khả dụng (GET /books)");
    Console.WriteLine("2. Xem chi tiết sách theo ID (GET /books/{id})");
    Console.WriteLine("3. Xem danh sách người dùng mẫu (GET /users)");
    Console.WriteLine("4. Gửi yêu cầu đặt mượn sách (POST /reservations)");
    Console.WriteLine("5. Tra cứu trạng thái đơn đặt mượn (GET /reservations/{id})");
    Console.WriteLine("6. Tra cứu lịch sử đặt mượn của bạn đọc (GET /users/{userId}/reservations)");
    Console.WriteLine("7. Cập nhật trạng thái mượn/trả sách (PUT /reservations/{id}/status)");
    Console.WriteLine("8. Kiểm tra trạng thái hệ thống (GET /health)");
    Console.WriteLine("0. Thoát");
    Console.Write("Chọn thao tác [0-8]: ");

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
                await ListUsersAsync();
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
                await UpdateReservationStatusAsync();
                break;
            case "8":
                await CheckHealthAsync();
                break;
            default:
                Console.WriteLine("Lựa chọn không hợp lệ, vui lòng chọn lại!");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Lỗi khi thực hiện thao tác: {ex.Message}");
        Console.ResetColor();
    }
}

async Task ListBooksAsync()
{
    var books = await httpClient.GetFromJsonAsync<List<BookDto>>("/books");
    if (books == null || books.Count == 0)
    {
        Console.WriteLine("Thư viện hiện chưa có đầu sách nào.");
        return;
    }

    Console.WriteLine("\n--- DANH SÁCH SÁCH TRONG THƯ VIỆN ---");
    Console.WriteLine($"{"ID",-38} | {"Tiêu đề",-35} | {"Tác giả",-20} | {"Khả dụng",-10}");
    Console.WriteLine(new string('-', 110));
    foreach (var b in books)
    {
        Console.WriteLine($"{b.Id,-38} | {Truncate(b.Title, 35),-35} | {Truncate(b.Author, 20),-20} | {b.AvailableCopies}/{b.TotalCopies,-8}");
    }
}

async Task GetBookDetailAsync()
{
    Console.Write("Nhập Book ID (GUID): ");
    var idStr = Console.ReadLine()?.Trim();
    if (!Guid.TryParse(idStr, out var id))
    {
        Console.WriteLine("GUID không hợp lệ!");
        return;
    }

    var response = await httpClient.GetAsync($"/books/{id}");
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"Không tìm thấy sách: {response.StatusCode}");
        return;
    }

    var book = await response.Content.ReadFromJsonAsync<BookDto>();
    if (book != null)
    {
        Console.WriteLine("\n--- CHI TIẾT SÁCH ---");
        Console.WriteLine($"ID:           {book.Id}");
        Console.WriteLine($"Tiêu đề:      {book.Title}");
        Console.WriteLine($"Tác giả:      {book.Author}");
        Console.WriteLine($"ISBN:         {book.ISBN}");
        Console.WriteLine($"Tổng số bản:  {book.TotalCopies}");
        Console.WriteLine($"Khả dụng:     {book.AvailableCopies}");
        Console.WriteLine($"Mô tả:        {book.Description}");
    }
}

async Task ListUsersAsync()
{
    var users = await httpClient.GetFromJsonAsync<List<UserDto>>("/users");
    if (users == null || users.Count == 0)
    {
        Console.WriteLine("Chưa có bạn đọc nào.");
        return;
    }

    Console.WriteLine("\n--- DANH SÁCH BẠN ĐỌC ---");
    Console.WriteLine($"{"User ID",-38} | {"Họ và tên",-25} | {"Email",-30}");
    Console.WriteLine(new string('-', 100));
    foreach (var u in users)
    {
        Console.WriteLine($"{u.Id,-38} | {u.FullName,-25} | {u.Email,-30}");
    }
}

async Task CreateReservationAsync()
{
    Console.Write("Nhập User ID (GUID): ");
    var userStr = Console.ReadLine()?.Trim();
    if (!Guid.TryParse(userStr, out var userId)) { Console.WriteLine("User ID không hợp lệ!"); return; }

    Console.Write("Nhập Book ID (GUID): ");
    var bookStr = Console.ReadLine()?.Trim();
    if (!Guid.TryParse(bookStr, out var bookId)) { Console.WriteLine("Book ID không hợp lệ!"); return; }

    var payload = new { BookId = bookId, UserId = userId };
    var res = await httpClient.PostAsJsonAsync("/reservations", payload);
    var content = await res.Content.ReadAsStringAsync();

    if (res.IsSuccessStatusCode)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n[THÀNH CÔNG] Yêu cầu đặt mượn đã được tiếp nhận và đưa vào RabbitMQ!");
        Console.WriteLine($"Phản hồi từ server: {content}");
        Console.ResetColor();
        Console.WriteLine("-> Reservation Worker đang xử lý bất đồng bộ để kiểm tra số bản...");
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
    Console.Write("Nhập Mã đơn đặt mượn Reservation ID (GUID): ");
    var resStr = Console.ReadLine()?.Trim();
    if (!Guid.TryParse(resStr, out var resId)) { Console.WriteLine("Mã đơn không hợp lệ!"); return; }

    var res = await httpClient.GetAsync($"/reservations/{resId}");
    if (!res.IsSuccessStatusCode)
    {
        Console.WriteLine($"Không tìm thấy đơn đặt mượn: {res.StatusCode}");
        return;
    }

    var detail = await res.Content.ReadFromJsonAsync<ReservationDetailDto>();
    if (detail != null)
    {
        Console.WriteLine("\n--- THÔNG TIN ĐƠN ĐẶT MƯỢN ---");
        Console.WriteLine($"Mã đơn:       {detail.Id}");
        Console.WriteLine($"Sách:         {detail.BookTitle} (ID: {detail.BookId})");
        Console.WriteLine($"Bạn đọc:      {detail.UserName} (ID: {detail.UserId})");
        Console.WriteLine($"Trạng thái:   {detail.Status}");
        Console.WriteLine($"Thông báo:    {detail.ReasonMessage}");
        Console.WriteLine($"Thời gian tạo:{detail.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"Đã xử lý lúc: {detail.ProcessedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"Nhận sách:    {detail.BorrowedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"Trả sách:     {detail.ReturnedAt:yyyy-MM-dd HH:mm:ss} UTC");
    }
}

async Task ListUserReservationsAsync()
{
    Console.Write("Nhập User ID (GUID): ");
    var userStr = Console.ReadLine()?.Trim();
    if (!Guid.TryParse(userStr, out var userId)) { Console.WriteLine("User ID không hợp lệ!"); return; }

    var res = await httpClient.GetAsync($"/users/{userId}/reservations");
    if (!res.IsSuccessStatusCode)
    {
        Console.WriteLine($"Không lấy được lịch sử: {res.StatusCode}");
        return;
    }

    var list = await res.Content.ReadFromJsonAsync<List<ReservationDetailDto>>();
    if (list == null || list.Count == 0)
    {
        Console.WriteLine("Bạn đọc này chưa có yêu cầu đặt mượn nào.");
        return;
    }

    Console.WriteLine($"\n--- LỊCH SỬ ĐẶT MƯỢN CỦA USER ({list.Count} đơn) ---");
    foreach (var r in list)
    {
        Console.WriteLine($"- [{r.Status}] Mã: {r.Id} | Sách: {r.BookTitle}");
        Console.WriteLine($"  Ghi chú: {r.ReasonMessage} | Lúc: {r.CreatedAt:yyyy-MM-dd HH:mm:ss}");
    }
}

async Task UpdateReservationStatusAsync()
{
    Console.Write("Nhập Reservation ID (GUID): ");
    var resStr = Console.ReadLine()?.Trim();
    if (!Guid.TryParse(resStr, out var resId)) { Console.WriteLine("Mã đơn không hợp lệ!"); return; }

    Console.WriteLine("Chọn trạng thái mới:");
    Console.WriteLine("  1. Borrowed (Bạn đọc đã đến nhận sách)");
    Console.WriteLine("  2. Returned (Bạn đọc đã trả lại sách)");
    Console.Write("Chọn [1 hoặc 2]: ");
    var statusChoice = Console.ReadLine()?.Trim();

    int newStatus = statusChoice == "1" ? 3 : (statusChoice == "2" ? 4 : 0);
    if (newStatus == 0) { Console.WriteLine("Lựa chọn không hợp lệ!"); return; }

    var res = await httpClient.PutAsJsonAsync($"/reservations/{resId}/status", new { NewStatus = newStatus });
    var body = await res.Content.ReadAsStringAsync();
    if (res.IsSuccessStatusCode)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[CẬP NHẬT THÀNH CÔNG] Phản hồi: {body}");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[LỖI] {body}");
        Console.ResetColor();
    }
}

async Task CheckHealthAsync()
{
    var res = await httpClient.GetAsync("/health");
    var body = await res.Content.ReadAsStringAsync();
    Console.WriteLine($"[HEALTH CHECK] Status: {res.StatusCode} | Body: {body}");
}

string Truncate(string val, int max) => string.IsNullOrEmpty(val) ? "" : (val.Length <= max ? val : val.Substring(0, max - 3) + "...");

public record BookDto(Guid Id, string Title, string Author, string ISBN, int TotalCopies, int AvailableCopies, string Description);
public record UserDto(Guid Id, string FullName, string Email, string PhoneNumber);
public record ReservationDetailDto(Guid Id, Guid BookId, string? BookTitle, Guid UserId, string? UserName, string Status, string? ReasonMessage, DateTime CreatedAt, DateTime? ProcessedAt, DateTime? BorrowedAt, DateTime? ReturnedAt);
