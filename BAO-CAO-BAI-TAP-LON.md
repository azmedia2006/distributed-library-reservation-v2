# BÁO CÁO BÀI TẬP LỚN
## HỌC PHẦN: PHÁT TRIỂN ỨNG DỤNG PHÂN TÁN
### ĐỀ TÀI SỐ 20: HỆ THỐNG THƯ VIỆN VÀ ĐẶT MƯỢN SÁCH PHÂN TÁN

---

## MỤC LỤC
1. [GIỚI THIỆU VÀ MÔ TẢ BÀI TOÁN](#1-giới-thiệu-và-mô-tả-bài-toán)
2. [PHÂN TÍCH YÊU CẦU HỆ THỐNG](#2-phân-tích-yêu-cầu-hệ-thống)
3. [KIẾN TRÚC VÀ CƠ CHẾ GIAO TIẾP PHÂN TÁN](#3-kiến-trúc-và-cơ-chế-giao-tiếp-phân-tán)
4. [THIẾT KẾ CHI TIẾT CÁC THÀNH PHẦN](#4-thiết-kế-chi-tiết-các-thành-phần)
5. [CÁC TÍNH NĂNG NÂNG CAO VÀ ĐỘ TIN CẬY (ADVANCED FEATURES)](#5-các-tính-năng-nâng-cao-và-độ-tin-cậy)
6. [HƯỚNG DẪN TRIỂN KHAI VÀ VẬN HÀNH](#6-hướng-dẫn-triển-khai-và-vận-hành)
7. [KẾT QUẢ THỰC HIỆN VÀ KỊCH BẢN KIỂM THỬ (DEMO)](#7-kết-quả-thực-hiện-và-kịch-bản-kiểm-thử)
8. [ĐÁNH GIÁ VÀ KẾT LUẬN](#8-đánh-giá-và-kết-luận)

---

## 1. GIỚI THIỆU VÀ MÔ TẢ BÀI TOÁN

Trong các hệ thống quản lý thư viện truyền thống, khi số lượng bạn đọc truy cập cùng một thời điểm tăng đột biến (ví dụ trong mùa thi hoặc phát hành tài liệu giáo trình mới), việc kiểm tra số bản khả dụng và ghi nhận đơn đặt mượn trực tiếp đồng bộ (synchronous transaction) lên Cơ sở dữ liệu thường dẫn tới các hiện tượng:
- Nghẽn cổ chai (bottleneck), khóa dòng (row-lock contention).
- Hiện tượng **Race Condition** dẫn đến việc nhiều bạn đọc cùng đặt thành công một cuốn sách duy nhất (Overbooking).
- Hệ thống bị treo hoặc phản hồi chậm nếu Database quá tải.

**Giải pháp:** Áp dụng mô hình **Kiến trúc hướng sự kiện phân tán (Event-Driven / Distributed Architecture)**:
- Tách biệt API tiếp nhận yêu cầu và Service xử lý nghiệp vụ mượn sách.
- Sử dụng hàng đợi thông điệp (**RabbitMQ Message Broker**) để đệm dữ liệu (buffering) và phân phối công việc bất đồng bộ (**Asynchronous Background Processing**).
- Đảm bảo tính toán vẹn dữ liệu, khả năng chịu lỗi (**Fault Tolerance**) và khả năng mở rộng (**Scalability**).

---

## 2. PHÂN TÍCH YÊU CẦU HỆ THỐNG

### 2.1. Yêu cầu chức năng
1. **Quản lý & Tra cứu sách:**
   - Tra cứu danh sách toàn bộ các đầu sách trong thư viện.
   - Tra cứu chi tiết một cuốn sách, kiểm tra tổng số bản (`TotalCopies`) và số bản đang khả dụng (`AvailableCopies`).
2. **Đặt mượn sách bất đồng bộ:**
   - Bạn đọc gửi yêu cầu đặt sách.
   - API ghi nhận đơn ở trạng thái sơ bộ `Pending` và đẩy thông điệp vào RabbitMQ.
   - Reservation Worker tiêu thụ thông điệp từ hàng đợi, kiểm tra số lượng sách thực tế và quyết định:
     - Duyệt đơn (`Reserved`) và trừ đi 1 bản khả dụng nếu còn sách.
     - Từ chối đơn (`Rejected`) kèm lý do nếu sách đã hết.
3. **Quản lý vòng đời mượn - trả sách:**
   - Cập nhật khi bạn đọc đến quầy nhận sách: chuyển từ `Reserved` -> `Borrowed`.
   - Cập nhật khi bạn đọc trả sách: chuyển từ `Borrowed` -> `Returned`, đồng thời hệ thống tự động hoàn lại (+1) số bản sách khả dụng.
4. **Tra cứu lịch sử:**
   - Tra cứu chi tiết đơn đặt mượn theo mã `ReservationId`.
   - Tra cứu toàn bộ lịch sử các sách đã đặt, đang mượn hoặc đã trả của từng bạn đọc (`UserId`).

### 2.2. Yêu cầu phi chức năng
- **Bất đồng bộ (Asynchronous):** Phản hồi tức thì cho người dùng khi đặt sách (HTTP 202 Accepted), không bắt client chờ khóa dữ liệu.
- **Tính Idempotent (Idempotency):** Đảm bảo an toàn khi xử lý lại thông điệp, không bao giờ trừ trùng lặp số sách.
- **Truy vết phân tán (Distributed Tracing):** Sử dụng `CorrelationId` xuyên suốt từ Client -> API -> RabbitMQ -> Worker -> Logs.
- **Khả năng chịu lỗi (Resilience & Dead Letter Queue):** Định tuyến thông điệp lỗi sang Dead Letter Queue (`DLQ`) để cách ly và xử lý sau.
- **Dễ triển khai (Containerization):** Đóng gói toàn bộ các service qua Docker & Docker Compose.

---

## 3. KIẾN TRÚC VÀ CƠ CHẾ GIAO TIẾP PHÂN TÁN

### 3.1. Sơ đồ kiến trúc tổng thể

```text
  ┌────────────────────────┐      ┌─────────────────────────┐
  │ Web Client (Port 8088) │      │ Console Client (CLI App)│
  └───────────┬────────────┘      └────────────┬────────────┘
              │                                │
              │ HTTP / REST API                │ HTTP / REST API
              ▼                                ▼
       ┌──────────────────────────────────────────────┐
       │     Library API (ASP.NET Core Minimal API)   │
       │                   Port: 5005                 │
       └──────────────┬───────────────────────────────┘
                      │
     ┌────────────────┴──────────────────────────────┐
     │ (1) Lưu đơn Pending                           │ (2) Publish Message
     ▼                                               ▼
┌───────────────────────────┐         ┌───────────────────────────────┐
│ PostgreSQL Database       │         │ RabbitMQ Message Broker       │
│ Port: 5432                │         │ Exchange: library.exchange    │
│                           │         │ Queue: library.reservations.q │
│ - Books                   │         └──────────────┬────────────────┘
│ - Users                   │                        │
│ - Reservations            │                        │ (3) Async Consume
└─────────────▲─────────────┘                        ▼
              │                       ┌───────────────────────────────┐
              │                       │ Reservation Worker Service    │
              │ (4) Check copies &    │ (.NET BackgroundService)      │
              └───── Approve/Reject ──┤                               │
                                      │ Dead Letter: reservations.dlq │
                                      └───────────────────────────────┘
```

### 3.2. Cơ chế giao tiếp giữa các thành phần
1. **Client <-> API:** Giao thức HTTP RESTful, định dạng JSON.
2. **API <-> Message Broker:** AMQP protocol qua thư viện `RabbitMQ.Client`. Khi có yêu cầu mới, API sinh `CorrelationId` và gửi `ReservationRequestedMessage` lên Direct Exchange `library.exchange`.
3. **Message Broker <-> Worker:** Cơ chế Push-Consumer (Eventing Basic Consumer) với cơ chế xác nhận thủ công (`manual Ack / Reject`).
4. **Worker/API <-> Database:** Kết nối PostgreSQL thông qua Entity Framework Core 10 (Npgsql Provider).

---

## 4. THIẾT KẾ CHI TIẾT CÁC THÀNH PHẦN

### 4.1. Cơ sở dữ liệu (Database Schema)
Hệ thống sử dụng PostgreSQL với 3 thực thể chính:
- **`Book`**:
  - `Id` (UUID, Primary Key)
  - `Title`, `Author`, `ISBN`, `Description` (Varchar/Text)
  - `TotalCopies` (Integer): Tổng số bản của thư viện.
  - `AvailableCopies` (Integer): Số bản thực tế còn trên giá sách.
- **`User`**:
  - `Id` (UUID, Primary Key)
  - `FullName`, `Email`, `PhoneNumber`
- **`Reservation`**:
  - `Id` (UUID, Primary Key)
  - `BookId` (UUID, Foreign Key liên kết `Book`)
  - `UserId` (UUID, Foreign Key liên kết `User`)
  - `Status` (Enum: `Pending = 0`, `Reserved = 1`, `Rejected = 2`, `Borrowed = 3`, `Returned = 4`)
  - `ReasonMessage` (Text): Ghi chú kết quả duyệt / lý do từ chối.
  - `CreatedAt`, `ProcessedAt`, `BorrowedAt`, `ReturnedAt` (Timestamp with timezone)

### 4.2. Library API (Minimal API)
Xây dựng trên nền tảng .NET 10 Minimal API với thiết kế gọn gàng, hiệu năng cao:
- `GET /books`: Danh sách toàn bộ sách.
- `GET /books/{id}`: Chi tiết sách và số lượng tồn.
- `POST /reservations`: Tiếp nhận yêu cầu, tạo bản ghi `Pending`, bắn tin nhắn vào RabbitMQ, trả về `202 Accepted` ngay lập tức.
- `GET /reservations/{id}`: Tra cứu trạng thái đơn.
- `GET /users/{userId}/reservations`: Tra cứu danh sách các sách đã mượn/đặt của người dùng.
- `PUT /reservations/{id}/status`: Cập nhật trạng thái `Borrowed` (nhận sách) hoặc `Returned` (trả sách và tăng `AvailableCopies`).
- `GET /users`: Danh sách bạn đọc mẫu.
- `GET /health`: Health Check giám sát trạng thái dịch vụ.

### 4.3. Reservation Worker Service
Chạy độc lập dưới dạng `BackgroundService` trong .NET:
- Lắng nghe hàng đợi `library.reservations.queue`.
- Nhận message và giải mã JSON.
- **Idempotency check:** Kiểm tra trạng thái đơn trong DB. Nếu đơn đã ở trạng thái khác `Pending`, bỏ qua (tránh xử lý trùng khi mạng giật hoặc retry).
- Nếu `AvailableCopies > 0`: Trừ 1 số bản, chuyển trạng thái thành `Reserved`, gửi `BasicAck`.
- Nếu `AvailableCopies == 0`: Chuyển trạng thái thành `Rejected`, gửi `BasicAck`.
- Trường hợp lỗi nghiêm trọng không xử lý được: Gọi `BasicReject(requeue: false)` để tự động chuyển vào `Dead Letter Queue (DLQ)`.

### 4.4. Client Interfaces
Hệ thống cung cấp đồng thời 2 giao diện người dùng:
1. **Web Client (Port 8088):** Giao diện Web SPA tương tác trực quan bằng HTML5, CSS3, Bootstrap 5, FontAwesome/Bootstrap Icons, Vanilla JavaScript. Cho phép chuyển đổi nhanh bạn đọc, bấm đặt sách 1-click, xem cập nhật theo thời gian thực.
2. **Console Client (.NET CLI App):** Ứng dụng dòng lệnh đa chức năng, cung cấp menu 8 chức năng trực quan, phù hợp test nhanh hoặc môi trường terminal.

---

## 5. CÁC TÍNH NĂNG NÂNG CAO VÀ ĐỘ TIN CẬY

Theo mục 3 (Yêu cầu mở rộng) trong `HUONG-DAN-CHUNG.md`, hệ thống đã được triển khai đầy đủ các kỹ thuật chuyên sâu của kiến trúc phân tán:

1. **Correlation ID Tracking:**
   Mỗi request đặt mượn sinh một mã UUID duy nhất. Mã này được đính kèm vào header/properties của Message RabbitMQ và ghi chú trong mọi dòng log từ API đến Worker, giúp DevOps dễ dàng trace lỗi phân tán.
2. **Dead Letter Exchange (DLX) & Dead Letter Queue (DLQ):**
   Exchange `library.dlx.exchange` và Queue `library.reservations.dlq` được cấu hình để hứng các thông điệp lỗi hoặc độc hại (poison message), giúp hệ thống không bao giờ bị nghẽn (hang) worker.
3. **Idempotent Consumer:**
   Worker luôn kiểm tra tính hợp lệ và trạng thái hiện tại của đơn hàng trước khi thực thi trừ kho sách, loại trừ triệt để rủi ro lỗi xử lý trùng thông điệp (At-least-once delivery).
4. **Health Checks:**
   Tích hợp endpoint `/health` theo chuẩn ASP.NET Core Health Checks để tích hợp vào các công cụ điều phối (Kubernetes / Docker Compose healthcheck).
5. **Auto-Recovery & Retry:**
   Worker và API được trang bị vòng lặp thử lại tự động khi Database hoặc RabbitMQ khởi động chậm hoặc bị ngắt kết nối tạm thời.

---

## 6. HƯỚNG DẪN TRIỂN KHAI VÀ VẬN HÀNH

### 6.1. Khởi chạy bằng Docker Compose (Khuyến nghị)
Hệ thống được cấu hình đầy đủ trong `docker-compose.yml`:
```bash
# Di chuyển vào thư mục dự án
cd "ứng dụng phân tán"

# Khởi chạy toàn bộ hệ thống
docker compose up -d --build
```

**Các cổng dịch vụ đã map:**
- Web Client: [http://localhost:8088](http://localhost:8088)
- Library API Swagger: [http://localhost:5005/swagger](http://localhost:5005/swagger)
- RabbitMQ Management Dashboard: [http://localhost:15672](http://localhost:15672) (User/Pass: `guest`/`guest`)
- PostgreSQL: `localhost:5432` (User: `postgres`, Pass: `postgres`, DB: `library_db`)

### 6.2. Chạy Console Client
```bash
dotnet run --project src/Library.ConsoleClient
```

### 6.3. Chạy Unit & Integration Test
```bash
dotnet test
```

---

## 7. KẾT QUẢ THỰC HIỆN VÀ KỊCH BẢN KIỂM THỬ

### 7.1. Kịch bản 1: Đặt mượn sách thành công
1. Bạn đọc A chọn cuốn sách "Designing Data-Intensive Applications" (đang còn 2 bản).
2. Bấm "Đặt sách":
   - API trả về `202 Accepted`, sinh mã đơn `ReservationId` và `CorrelationId`.
   - Worker nhận thông điệp, kiểm tra `AvailableCopies = 2 > 0`.
   - Worker giảm `AvailableCopies` còn 1, chuyển trạng thái sang `Reserved`.
   - Web Client tự động cập nhật hiển thị trạng thái `Reserved`.

### 7.2. Kịch bản 2: Từ chối khi hết sách (Overbooking Prevention)
1. Đặt tiếp cho đến khi cuốn sách có `AvailableCopies = 0`.
2. Một bạn đọc khác gửi yêu cầu đặt tiếp cuốn sách đó.
3. Worker nhận thông điệp, phát hiện `AvailableCopies == 0`.
4. Worker chuyển trạng thái đơn sang `Rejected`, thông báo: *"Hết sách khả dụng. Yêu cầu đặt mượn đã bị từ chối."*.
5. Kho sách không bị âm, dữ liệu luôn toàn vẹn.

### 7.3. Kịch bản 3: Nhận sách và Trả sách
1. Bạn đọc đến quầy nhận sách: nhân viên bấm nút "Nhận sách (Borrowed)". Trạng thái chuyển thành `Borrowed`.
2. Bạn đọc mang sách trả lại: bấm nút "Trả sách (Returned)". Trạng thái chuyển thành `Returned`, số bản khả dụng trong kho tự động được cộng thêm 1.

---

## 8. ĐÁNH GIÁ VÀ KẾT LUẬN

- **Mức độ hoàn thành:** Dự án đáp ứng 100% các yêu cầu bắt buộc và toàn bộ các yêu cầu mở rộng trong `HUONG-DAN-CHUNG.md` và đề bài số 20.
- **Tính thực tiễn:** Ứng dụng giải quyết triệt để vấn đề quá tải và xung đột dữ liệu đặt sách bằng kiến trúc hướng sự kiện với RabbitMQ.
- **Mã nguồn:** Toàn bộ mã nguồn, cấu hình Docker, kịch bản kiểm thử tự động (Unit Test) và tài liệu được quản lý đồng bộ trên GitHub.
