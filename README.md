# Hệ Thống Thư Viện và Đặt Mượn Sách Phân Tán (Distributed Library Reservation System)

Dự án bài tập lớn học phần **Phát triển ứng dụng phân tán** - **Đề tài số 20**.

---

## 1. Giới thiệu & Kiến trúc hệ thống

Hệ thống cho phép người dùng tra cứu sách, số bản khả dụng và gửi yêu cầu đặt mượn sách. Các yêu cầu đặt mượn được đưa vào hàng đợi Message Broker (RabbitMQ) và xử lý bất đồng bộ bởi Worker Service, đảm bảo tính toàn vẹn dữ liệu, khả năng mở rộng (scalability) và chịu lỗi (fault-tolerance).

### Mô hình kiến trúc tổng thể:

```text
[Web Client (Port 8088)]
        │ (HTTP / JSON)
        ▼
[Library API (Minimal API - Port 5005)]
   │                               │
   │ (Publish message)             │ (Direct Query / Create Pending)
   ▼                               ▼
[RabbitMQ Message Broker]     [PostgreSQL Database (Port 5432)]
   │                               ▲
   │ (Consume & Acknowledge)       │ (Check copies & update status)
   ▼                               │
[Reservation Worker Service] ───────┘
```

---

## 2. Công nghệ sử dụng

- **Ngôn ngữ & Nền tảng:** C#, .NET 10 (LTS).
- **Web API:** ASP.NET Core Minimal API.
- **Message Broker:** RabbitMQ 3.x với mô hình Direct Exchange, Message Acknowledgement (Ack/Reject), Dead Letter Exchange (DLX) & Dead Letter Queue (DLQ).
- **Cơ sở dữ liệu:** PostgreSQL 16 & Entity Framework Core 10.
- **Client:** Web App tương tác trực quan (HTML5, Bootstrap 5, Vanilla JS, Responsive).
- **Containerization:** Docker & Docker Compose chạy đa dịch vụ độc lập trên Docker network.
- **Tính năng nâng cao (Yêu cầu mở rộng):**
  - **Correlation ID:** Gắn xuyên suốt từ API sang Message Broker đến Worker để trace logs hệ thống.
  - **Dead-Letter Queue (DLQ):** Định tuyến thông điệp lỗi tự động sang `library.reservations.dlq`.
  - **Idempotent Consumer:** Worker kiểm tra trạng thái trước khi cập nhật số bản để tránh xử lý trùng lặp.
  - **Health Checks:** Cung cấp endpoint `/health` để giám sát trạng thái hệ thống.
  - **Retry & Recovery:** Tự động kết nối lại khi Database hoặc Message Broker chưa sẵn sàng.

---

## 3. Cấu trúc thư mục

```text
├── 20-he-thong-thu-vien-dat-muon-sach.md  # Yêu cầu đề tài
├── HUONG-DAN-CHUNG.md                     # Hướng dẫn chung
├── LibraryDistributedSystem.slnx          # Solution file .NET 10
├── docker-compose.yml                     # File khởi chạy toàn bộ hệ thống
├── README.md                              # Hướng dẫn cài đặt và sử dụng
└── src/
    ├── Library.Domain/                    # Domain Entities, Enums, DTOs
    ├── Library.Infrastructure/            # DbContext, RabbitMQ Publisher & Config
    ├── Library.API/                       # Minimal API, Swagger, HealthCheck
    ├── Library.Worker/                    # Background Service xử lý Message từ RabbitMQ
    └── Library.WebClient/                 # Giao diện Web Client
```

---

## 4. Danh sách API endpoints

| Phương thức | Endpoint | Mô tả |
|-------------|----------|-------|
| `GET` | `/books` | Lấy danh sách tất cả các sách trong thư viện |
| `GET` | `/books/{id}` | Lấy chi tiết sách và số bản còn khả dụng |
| `POST` | `/reservations` | Gửi yêu cầu đặt mượn sách (đưa vào RabbitMQ) |
| `GET` | `/reservations/{id}` | Tra cứu trạng thái yêu cầu đặt mượn theo ID |
| `GET` | `/users/{userId}/reservations` | Tra cứu lịch sử đặt mượn của một người dùng |
| `PUT` | `/reservations/{id}/status` | Cập nhật trạng thái sách: `Borrowed` (nhận sách) hoặc `Returned` (trả sách) |
| `GET` | `/users` | Lấy danh sách người dùng mẫu |
| `GET` | `/health` | Kiểm tra tình trạng hoạt động của service |

---

## 5. Hướng dẫn cài đặt và khởi chạy

### Cách 1: Khởi chạy toàn bộ hệ thống qua Docker Compose (Khuyến nghị)

Chỉ cần cài đặt Docker và Docker Compose trên máy tính:

```bash
# Di chuyển vào thư mục dự án
cd "ứng dụng phân tán"

# Khởi chạy toàn bộ hệ thống (Database, RabbitMQ, API, Worker, Web Client)
docker compose up -d --build
```

Sau khi khởi chạy thành công:
- **Giao diện Web Client:** [http://localhost:8088](http://localhost:8088)
- **Library API Swagger UI:** [http://localhost:5005/swagger](http://localhost:5005/swagger)
- **RabbitMQ Management Dashboard:** [http://localhost:15672](http://localhost:15672) (Tài khoản: `guest` / Mật khẩu: `guest`)

Để dừng hệ thống:
```bash
docker compose down
```

---

### Cách 2: Chạy trực tiếp từ mã nguồn (.NET SDK)

1. Khởi động PostgreSQL và RabbitMQ:
```bash
docker compose up -d postgres-db rabbitmq
```

2. Chạy Library API:
```bash
dotnet run --project src/Library.API
```

3. Chạy Reservation Worker:
```bash
dotnet run --project src/Library.Worker
```

4. Chạy Web Client:
```bash
dotnet run --project src/Library.WebClient
```

---

## 6. Luồng kiểm thử nghiệp vụ (Demo Flow)

1. Truy cập Web Client tại `http://localhost:8088`.
2. Chọn người dùng (ví dụ: *Nguyễn Văn An*).
3. Nhấn **Đặt Sách** cho một tựa sách có sẵn số bản khả dụng.
4. Hệ thống sẽ:
   - Gửi request đến `POST /reservations`.
   - Tạo đơn ở trạng thái `Pending` trong Database.
   - Bắn message kèm `CorrelationId` vào exchange `library.exchange` của RabbitMQ.
   - Worker nhận thông điệp, kiểm tra số bản còn lại > 0:
     - Giảm `AvailableCopies` của sách đi 1.
     - Cập nhật trạng thái đơn thành `Reserved`.
5. Người dùng đến thư viện nhận sách: bấm **Nhận sách** -> Chuyển thành `Borrowed`.
6. Người dùng trả sách: bấm **Trả sách** -> Chuyển thành `Returned`, số bản khả dụng tự động tăng lại 1.
7. Khi sách có số bản = 0: các yêu cầu đặt mượn tiếp theo sẽ tự động bị từ chối với trạng thái `Rejected`.
