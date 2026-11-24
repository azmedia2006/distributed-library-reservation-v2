# BÁO CÁO BÀI TẬP LỚN
## HỌC PHẦN: PHÁT TRIỂN ỨNG DỤNG PHÂN TÁN
### ĐỀ TÀI SỐ 20: HỆ THỐNG THƯ VIỆN VÀ ĐẶT MƯỢN SÁCH PHÂN TÁN

---

## MỤC LỤC
1. [GIỚI THIỆU VÀ MÔ TẢ BÀI TOÁN](#1-giới-thiệu-và-mô-tả-bài-toán)
2. [PHÂN TÍCH YÊU CẦU HỆ THỐNG](#2-phân-tích-yêu-cầu-hệ-thống)
3. [KIẾN TRÚC VÀ CƠ CHẾ GIAO TIẾP PHÂN TÁN](#3-kiến-trúc-và-cơ-chế-giao-tiếp-phân-tán)
4. [THIẾT KẾ CHI TIẾT CÁC THÀNH PHẦN](#4-thiết-kế-chi-tiết-các-thành-phần)
5. [CÁC TÍNH NĂNG NÂNG CAO VÀ KHẢ NĂNG CHỊU LỖI](#5-các-tính-năng-nâng-cao-và-khả-năng-chịu-lỗi)
6. [HƯỚNG DẪN TRIỂN KHAI VÀ VẬN HÀNH](#6-hướng-dẫn-triển-khai-và-vận-hành)
7. [KẾT QUẢ THỰC HIỆN VÀ KỊCH BẢN KIỂM THỬ](#7-kết-quả-thực-hiện-và-kịch-bản-kiểm-thử)
8. [ĐÁNH GIÁ VÀ KẾT LUẬN](#8-đánh-giá-và-kết-luận)

---

## 1. GIỚI THIỆU VÀ MÔ TẢ BÀI TOÁN

Trong các hệ thống quản lý thư viện truyền thống, khi số lượng bạn đọc truy cập đồng thời tăng đột biến (chẳng hạn vào mùa ôn thi hoặc đợt phát hành giáo trình mới), mô hình kiểm tra tồn kho và ghi nhận phiếu mượn đồng bộ trực tiếp trên cơ sở dữ liệu thường gặp ba hạn chế lớn:
- **Nghẽn cổ chai cơ sở dữ liệu (Database Bottleneck):** Các giao dịch giữ khóa dòng (`row-level lock`) làm chậm cả tác vụ đọc/tra cứu danh mục sách.
- **Hiện tượng Overbooking do Race Condition:** Nhiều tiến trình cùng đọc thấy `AvailableCopies = 1` tại một thời điểm và cùng duyệt thành công, làm âm số lượng sách thực tế.
- **Giảm độ sẵn sàng:** Khi dịch vụ xử lý nghiệp vụ hoặc kết nối hạ tầng chập chờn, yêu cầu của người dùng bị từ chối ngay lập tức.

**Giải pháp đề xuất:** Xây dựng hệ thống theo **kiến trúc phân tán hướng sự kiện bất đồng bộ (Asynchronous Event-Driven Architecture)**:
- Tách rời dịch vụ tiếp nhận HTTP (`Library.API`) khỏi dịch vụ kiểm kê và xét duyệt tồn kho (`Library.Worker`).
- Sử dụng **RabbitMQ Message Broker** để điều tiết lưu lượng (load leveling), kết hợp giao dịch cô lập `Serializable` trên **PostgreSQL 16**, cơ chế **Retry lùi mũ + Dead Letter Queue** và tiến trình **tự động phục hồi đơn `Pending`** để đảm bảo độ tin cậy cao.

---

## 2. PHÂN TÍCH YÊU CẦU HỆ THỐNG

### 2.1. Yêu cầu chức năng (Theo Đề tài 20)
1. **Tra cứu danh sách và chi tiết sách:** Tìm kiếm theo tên sách, tác giả, ISBN; lọc sách còn bản khả dụng; xem chi tiết tồn kho và số lượng bản đang được giữ/mượn.
2. **Tra cứu số bản đang khả dụng:** Theo dõi thời gian thực `AvailableCopies / TotalCopies` cho từng đầu sách và tổng hợp trên Dashboard.
3. **Gửi yêu cầu đặt mượn sách bất đồng bộ:** API tạo bản ghi `Pending`, gắn mã truy vết `CorrelationId` và trả về `202 Accepted` ngay cho người dùng.
4. **Đưa yêu cầu vào RabbitMQ:** Xuất bản thông điệp bền vững (`Persistent`) vào Direct Exchange `library.exchange` với khóa định tuyến `reservation.created`.
5. **Worker kiểm tra tồn kho và quyết định:**
   - Nếu `AvailableCopies > 0`: Giảm 1 bản khả dụng và xác nhận trạng thái `Reserved`.
   - Nếu `AvailableCopies == 0`: Từ chối yêu cầu với trạng thái `Rejected` kèm lý do cụ thể.
6. **Quản lý đầy đủ 5 trạng thái vòng đời:** `Pending` -> `Reserved` / `Rejected` -> `Borrowed` -> `Returned` (kèm thao tác chủ động hủy đặt trước hoàn trả kho).
7. **Tra cứu trạng thái yêu cầu đặt mượn:** Theo dõi chi tiết mốc thời gian tạo, duyệt, nhận sách, trả sách và `CorrelationId` qua `GET /reservations/{id}`.
8. **Tra cứu sách đang đặt/mượn của người dùng:** Lọc danh sách sách đang đặt hoặc đang mượn (`?activeOnly=true`) cũng như toàn bộ lịch sử của từng bạn đọc qua `GET /users/{userId}/reservations`.

### 2.2. Yêu cầu phi chức năng & kỹ thuật chung
- **Nền tảng:** C# trên **.NET 10**, sử dụng **ASP.NET Core Minimal API**.
- **Đóng gói độc lập:** Mỗi dịch vụ .NET (`API`, `Worker`, `WebClient`, `ConsoleClient`) có `Dockerfile` riêng; toàn bộ hệ thống vận hành qua `docker-compose.yml` trên mạng nội bộ `library_network`.
- **Tách biệt cấu hình:** Mọi thông số kết nối PostgreSQL, RabbitMQ, URL dịch vụ và cổng lắng nghe đều được cấu hình qua biến môi trường và `appsettings.json`.
- **Khả năng phục hồi khi thành phần tạm dừng:** Kể cả khi RabbitMQ hoặc Worker tạm dừng rồi khởi động lại, hệ thống tự kết nối lại và quét xử lý bù các đơn `Pending` chưa được duyệt.

---

## 3. KIẾN TRÚC VÀ CƠ CHẾ GIAO TIẾP PHÂN TÁN

### 3.1. Sơ đồ kiến trúc tổng thể

```text
  ┌────────────────────────────┐      ┌─────────────────────────────┐
  │  Web Client & Proxy (8088) │      │ Console Client CLI (.NET 10)│
  └─────────────┬──────────────┘      └──────────────┬──────────────┘
                │ HTTP / JSON (X-Correlation-ID)     │ HTTP / JSON
                ▼                                    ▼
         ┌──────────────────────────────────────────────────┐
         │       Library API (.NET 10 Minimal API)          │
         │       Port: 5005 (Container: 5000)               │
         └──────────────┬────────────────────┬──────────────┘
                        │                    │
       (1) Lưu Pending  │                    │ (2) Publish Persistent Msg
                        ▼                    ▼
  ┌───────────────────────────┐    ┌────────────────────────────────┐
  │ PostgreSQL 16 (5432)      │    │ RabbitMQ Broker (5672 / 15672) │
  │ Database: library_db      │    │ Exchange: library.exchange     │
  │ - Books                   │    │ Queue: library.reservations.q  │
  │ - Users                   │    │ DLX: library.exchange.dlx      │
  │ - Reservations            │    │ DLQ: library.reservations.dlq  │
  └─────────────▲─────────────┘    └───────────────┬────────────────┘
                │                                  │
                │ (4) Serializable Tx:             │ (3) Async Consume
                │     Approve / Reject             ▼
                │                   ┌────────────────────────────────┐
                └───────────────────┤ Reservation Worker Service     │
                  (5) Pending Sweep │ - Idempotent Consumer          │
                                    │ - Exponential Retry (max 3)    │
                                    └────────────────────────────────┘
```

### 3.2. Cơ chế giao tiếp giữa các thành phần
1. **Web/Console Client <-> Library API:** Giao tiếp đồng bộ qua RESTful HTTP/JSON. Web Client sử dụng Internal Reverse Proxy (`/api/*` -> `http://library-api:5000`) qua tên dịch vụ nội bộ Docker, loại bỏ hoàn toàn vấn đề CORS và lộ cổng nội bộ.
2. **Library API <-> RabbitMQ:** Giao thức AMQP 0-9-1 qua `RabbitMQ.Client`. `RabbitMqPublisher` đồng bộ khóa đa luồng (`lock`), đánh dấu `Persistent = true`, đính kèm `CorrelationId` và header `x-retry-count`.
3. **RabbitMQ <-> Reservation Worker:** Cơ chế `AsyncEventingBasicConsumer` với `BasicQos(prefetchCount: 1)` và xác nhận thủ công (`BasicAck` / `BasicReject`).
4. **API & Worker <-> PostgreSQL:** Thông qua Entity Framework Core 10 (`Npgsql`), tự động khởi tạo schema, nâng cấp cột tương thích ngược và nạp dữ liệu mẫu khi khởi động.

---

## 4. THIẾT KẾ CHI TIẾT CÁC THÀNH PHẦN

### 4.1. Thiết kế dữ liệu (`Library.Domain` & `Library.Infrastructure`)
- **`Book`**: `Id` (UUID), `Title`, `Author`, `ISBN`, `Description`, `TotalCopies`, `AvailableCopies`, `CreatedAt`.
- **`User`**: `Id` (UUID), `FullName`, `Email`, `PhoneNumber`, `CreatedAt`.
- **`Reservation`**: `Id` (UUID), `BookId`, `UserId`, `Status` (`Pending = 0`, `Reserved = 1`, `Rejected = 2`, `Borrowed = 3`, `Returned = 4`), `ReasonMessage`, `CorrelationId`, `CreatedAt`, `ProcessedAt`, `BorrowedAt`, `ReturnedAt`.
- **`ReservationService`**: Dịch vụ nghiệp vụ dùng chung cho cả API, Worker và Unit Tests, đảm bảo quy tắc chuyển trạng thái chặt chẽ:
  - `Pending -> Reserved` (khi `AvailableCopies > 0`, giảm `AvailableCopies` đi 1).
  - `Pending -> Rejected` (khi `AvailableCopies == 0` hoặc hủy đơn chờ).
  - `Reserved -> Borrowed` (khi bạn đọc nhận sách tại quầy).
  - `Borrowed -> Returned` (khi bạn đọc trả sách, cộng trả `+1` vào `AvailableCopies`, không vượt quá `TotalCopies`).
  - `Reserved -> Rejected` (khi hủy đơn giữ chỗ trước khi nhận, cộng trả `+1` vào `AvailableCopies`).

### 4.2. Dịch vụ `Library.API`
Cung cấp đầy đủ nhóm API tối thiểu của đề bài cùng các API quản trị mở rộng:
- Nhóm bắt buộc: `GET /books`, `GET /books/{id}`, `POST /reservations`, `GET /reservations/{id}`, `GET /users/{userId}/reservations`.
- Nhóm vòng đời & quản trị: `PUT /reservations/{id}/status`, `POST /reservations/{id}/cancel`, `GET /reservations`, `POST /books`, `PUT /books/{id}`, `GET /users`, `POST /users`, `GET /stats`, `GET /health`.

### 4.3. Dịch vụ `Library.Worker`
- Tiêu thụ hàng đợi `library.reservations.queue` với `prefetchCount = 1`.
- Xử lý trong giao dịch cơ sở dữ liệu `IsolationLevel.Serializable` để ngăn chặn Race Condition ngay cả khi mở rộng nhiều bản sao (replicas) của Worker.
- Kiểm tra `reservation.Status != Pending` trước khi trừ kho để đảm bảo tính **Idempotent**.

### 4.4. Giao diện người dùng (`Library.WebClient` & `Library.ConsoleClient`)
- **Web Client (Port `8088`):** Giao diện SPA gồm thanh giám sát Health Check trực tiếp, dải thẻ thống kê KPI và 2 phân hệ: *Cổng Bạn Đọc* (tìm kiếm, xem chi tiết tồn kho, đặt mượn đơn lẻ, bắn thử nghiệm đồng thời 3 yêu cầu chống Overbooking, tự động polling cập nhật trạng thái đơn) và *Cổng Quản Trị Thủ Thư* (lọc/điều phối toàn bộ đơn mượn, thêm sách, điều chỉnh số lượng bản).
- **Console Client:** Ứng dụng CLI 9 chức năng có sẵn công cụ **Burst Concurrency Test** bắn hàng loạt yêu cầu song song và tổng hợp bảng đối chiếu kết quả ngay trên màn hình dòng lệnh.

---

## 5. CÁC TÍNH NĂNG NÂNG CAO VÀ KHẢ NĂNG CHỊU LỖI

1. **Retry lùi mũ kết hợp Dead Letter Queue (`DLX` / `DLQ`):**
   Khi xảy ra lỗi tạm thời (ví dụ xung đột khóa giao dịch hoặc kết nối DB gián đoạn ngắn), Worker đọc header `x-retry-count`, chờ theo cấp số nhân (`1s`, `2s`, `4s`) rồi phát lại thông điệp tối đa 3 lần. Nếu vượt quá số lần thử hoặc gặp gói tin JSON hỏng (poison message), Worker gọi `BasicReject(requeue: false)` để RabbitMQ chuyển thẳng sang `library.exchange.dlx` -> `library.reservations.dlq`.
2. **Tự động phục hồi đơn treo (`Pending Recovery Sweep`):**
   Định kỳ mỗi 15 giây, Worker kiểm tra các bản ghi `Pending` tồn tại quá 15 giây trong PostgreSQL (do RabbitMQ tạm ngắt kết nối đúng thời điểm API lưu đơn) và tự động đưa vào quy trình xét duyệt.
3. **Truy vết phân tán với `Correlation ID`:**
   Middleware tự động sinh hoặc kế thừa header `X-Correlation-ID`, lưu vào bảng `Reservations`, truyền qua `IBasicProperties.CorrelationId` của RabbitMQ và đưa vào Logging Scope của cả API lẫn Worker.
4. **Health Check đa thành phần:**
   Endpoint `GET /health` thực thi `CanConnectAsync()` tới PostgreSQL và `CheckHealth()` trên kênh RabbitMQ để phản ánh chính xác trạng thái sống còn của toàn hệ thống.

---

## 6. HƯỚNG DẪN TRIỂN KHAI VÀ VẬN HÀNH

### 6.1. Triển khai trọn gói bằng Docker Compose
```bash
git clone https://github.com/azmedia2006/distributed-library-reservation-v2.git
cd distributed-library-reservation-v2
docker compose up -d --build
```
Thông tin truy cập dịch vụ:
- **Web Client:** http://localhost:8088
- **Library API & Swagger:** http://localhost:5005/swagger
- **Health Check:** http://localhost:5005/health
- **RabbitMQ Management:** http://localhost:15672 (`guest` / `guest`)
- **PostgreSQL 16:** `localhost:5432` (User: `postgres`, Password: `postgrespassword`, Database: `library_db`)

### 6.2. Thực thi kiểm thử tự động
```bash
dotnet test
```

---

## 7. KẾT QUẢ THỰC HIỆN VÀ KỊCH BẢN KIỂM THỬ

### 7.1. Kết quả kiểm thử tự động (`xUnit`)
8 bài kiểm thử tự động trong project `Library.Tests` kiểm chứng trực tiếp `ReservationService`:
- Duyệt giữ sách thành công và trừ chính xác `AvailableCopies` khi còn sách.
- Từ chối đơn (`Rejected`) khi `AvailableCopies == 0`.
- Bỏ qua thông điệp trùng lặp (Idempotent Consumer), không trừ kho hai lần.
- Chuyển trạng thái hợp lệ `Reserved -> Borrowed` và `Borrowed -> Returned` (hoàn trả số bản khả dụng, không vượt `TotalCopies`).
- Chặn chuyển trạng thái sai quy trình (`Pending -> Borrowed`).
- Hủy phiếu `Reserved` hoàn trả lại bản sách vào kho khả dụng.
- Xử lý dồn dập 5 yêu cầu đồng thời cho đầu sách chỉ còn 1 bản: duyệt đúng 1 đơn `Reserved`, từ chối 4 đơn `Rejected`, tồn kho cuối bằng `0`.

### 7.2. Kịch bản kiểm thử chịu lỗi thực tế
1. Tạm dừng container `reservation-worker` (`docker compose stop reservation-worker`).
2. Trên Web Client, bạn đọc vẫn tiếp tục gửi yêu cầu đặt sách bình thường; API trả về `202 Accepted` và lưu đơn ở trạng thái `Pending`, tin nhắn được giữ an toàn trong hàng đợi bền vững của RabbitMQ.
3. Khởi động lại Worker (`docker compose start reservation-worker`): Worker tự động kết nối lại, tiêu thụ toàn bộ các thông điệp đang chờ trong hàng đợi và cập nhật kết quả `Reserved` / `Rejected` đầy đủ.

---

## 8. ĐÁNH GIÁ VÀ KẾT LUẬN

- **Mức độ hoàn thiện:** Hệ thống hoàn thành đầy đủ 100% các chức năng và API bắt buộc của Đề tài 20, đồng thời hiện thực hóa trọn vẹn các kỹ thuật nâng cao gồm Retry + Dead Letter Queue, Idempotent Consumer, Correlation ID, Component Health Check, Serializable Transaction và cơ chế tự phục hồi đơn `Pending`.
- **Khả năng triển khai:** Mã nguồn được tổ chức phân tầng rõ ràng trên .NET 10, đóng gói Docker độc lập cho từng dịch vụ và sẵn sàng trình diễn trực tiếp trên cả Web Client lẫn Console Client.
