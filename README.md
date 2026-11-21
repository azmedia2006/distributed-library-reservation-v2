# Hệ Thống Thư Viện và Đặt Mượn Sách Phân Tán (Distributed Library Reservation System)

Dự án bài tập lớn học phần **Phát triển ứng dụng phân tán** - **Đề tài số 20**.

---

## 1. Giới thiệu & Kiến trúc hệ thống

Hệ thống cho phép người dùng tra cứu sách, số bản khả dụng và gửi yêu cầu đặt mượn sách. Các yêu cầu đặt mượn được đưa vào hàng đợi Message Broker (RabbitMQ) và xử lý bất đồng bộ bởi Worker Service, đảm bảo tính toàn vẹn dữ liệu, chống overbooking, nâng cao khả năng mở rộng (scalability) và chịu lỗi (fault-tolerance).

### Mô hình kiến trúc tổng thể:

```text
[Web Client (Port 8088)]    [Console Client (CLI App)]
        │                               │
        └───────────────┬───────────────┘
                        │ (HTTP / JSON REST)
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

## 2. Công nghệ sử dụng & Điểm nổi bật

- **Ngôn ngữ & Nền tảng:** C#, .NET 10 (LTS).
- **Web API:** ASP.NET Core Minimal API.
- **Message Broker:** RabbitMQ 3.x với Direct Exchange, Message Acknowledgement (Ack/Reject), Dead Letter Exchange (DLX) & Dead Letter Queue (DLQ).
- **Cơ sở dữ liệu:** PostgreSQL 16 & Entity Framework Core 10 (Npgsql).
- **Client (Đầy đủ cả 2 nền tảng theo Hướng Dẫn Chung):**
  - **Web Client:** Ứng dụng Web giao diện sạch sẽ, thân thiện, không lạm dụng hiệu ứng gradient/AI, tích hợp Reverse Proxy sang API giúp truy cập mượt mà trên mọi môi trường và tránh hoàn toàn lỗi CORS / chặn cổng.
  - **Console Client:** Ứng dụng dòng lệnh Terminal đa chức năng (8 menu nghiệp vụ).
- **Kiểm thử tự động:** Bộ Unit Test và Integration Test với xUnit và EntityFrameworkCore.InMemory.
- **Containerization:** Docker & Docker Compose chạy độc lập toàn bộ các dịch vụ trên mạng nội bộ Docker.
- **Tính năng nâng cao (Yêu cầu mở rộng theo HUONG-DAN-CHUNG.md):**
  - **Correlation ID:** Gắn xuyên suốt từ API sang Message Broker đến Worker để truy vết phân tán.
  - **Dead-Letter Queue (DLQ):** Định tuyến thông điệp lỗi tự động sang `library.reservations.dlq`.
  - **Idempotent Consumer:** Worker kiểm tra trạng thái trước khi cập nhật số bản để tránh xử lý trùng lặp.
  - **Health Checks:** Cung cấp endpoint `/health` để giám sát trạng thái hệ thống.
  - **Retry & Recovery:** Tự động kết nối lại khi Database hoặc Message Broker chưa sẵn sàng.

---

## 3. Cấu trúc thư mục

```text
├── 20-he-thong-thu-vien-dat-muon-sach.md  # Đề bài chi tiết
├── HUONG-DAN-CHUNG.md                     # Hướng dẫn chung của học phần
├── BAO-CAO-BAI-TAP-LON.md                 # Quyển báo cáo chi tiết đầy đủ 8 phần
├── LibraryDistributedSystem.slnx          # Solution file .NET 10
├── docker-compose.yml                     # Cấu hình khởi chạy toàn bộ hệ thống
├── README.md                              # Hướng dẫn chi tiết
├── src/
│   ├── Library.Domain/                    # Domain Entities, Enums, DTOs, Message Contracts
│   ├── Library.Infrastructure/            # DbContext, RabbitMQ Publisher & Constants
│   ├── Library.API/                       # Minimal API, Swagger UI, Health Check
│   ├── Library.Worker/                    # Background Service lắng nghe & xử lý Message từ RabbitMQ
│   ├── Library.WebClient/                 # Giao diện Web Client & Backend Proxy
│   └── Library.ConsoleClient/             # Giao diện Console Client (CLI App)
└── tests/
    └── Library.Tests/                     # Bộ kiểm thử Unit Test & Nghiệp vụ (xUnit)
```

---

## 4. HƯỚNG DẪN CÀI ĐẶT VÀ KHỞI CHẠY CHI TIẾT

Hệ thống có thể chạy dễ dàng trên cả hai hệ điều hành **Windows** và **Ubuntu / Linux**.

### ─────────────────────────────────────────────────────────────
### 💻 HƯỚNG DẪN DÀNH CHO WINDOWS
### ─────────────────────────────────────────────────────────────

#### Cách 1: Chạy tự động trọn gói bằng Docker Desktop (Khuyến nghị số 1)
1. **Yêu cầu trước:**
   - Cài đặt và mở **Docker Desktop for Windows** (đảm bảo Docker Engine đã báo running).
2. **Các bước thực hiện:**
   - Mở **PowerShell** hoặc **Command Prompt (cmd)** hoặc **Windows Terminal**.
   - Di chuyển vào thư mục dự án:
     ```powershell
     cd "đường_dẫn_tới_thư_mục\ứng dụng phân tán"
     ```
   - Chạy lệnh khởi động Docker Compose:
     ```powershell
     docker compose up -d --build
     ```
   - Mở trình duyệt web truy cập:
     - **Giao diện Web:** [http://localhost:8088](http://localhost:8088)
     - **Swagger API Documentation:** [http://localhost:5005/swagger](http://localhost:5005/swagger)
     - **RabbitMQ Dashboard:** [http://localhost:15672](http://localhost:15672) (User: `guest`, Pass: `guest`)
   - Khi cần dừng hệ thống:
     ```powershell
     docker compose down
     ```

#### Cách 2: Chạy trực tiếp từ mã nguồn (.NET SDK trên Windows)
1. Cài đặt **.NET 10 SDK** từ trang chủ Microsoft.
2. Dùng Docker để bật database PostgreSQL và RabbitMQ:
   ```powershell
   docker compose up -d postgres-db rabbitmq
   ```
3. Mở 3 cửa sổ PowerShell / Terminal riêng biệt để chạy từng service:
   - **Terminal 1 (Library API):**
     ```powershell
     dotnet run --project src\Library.API
     ```
   - **Terminal 2 (Reservation Worker):**
     ```powershell
     dotnet run --project src\Library.Worker
     ```
   - **Terminal 3 (Web Client hoặc Console Client):**
     ```powershell
     # Chạy Web Client:
     dotnet run --project src\Library.WebClient
     
     # HOẶC Chạy Console Client:
     dotnet run --project src\Library.ConsoleClient
     ```
4. Chạy Unit Test:
   ```powershell
   dotnet test
   ```

---

### ─────────────────────────────────────────────────────────────
### 🐧 HƯỚNG DẪN DÀNH CHO UBUNTU / LINUX
### ─────────────────────────────────────────────────────────────

#### Cách 1: Chạy tự động trọn gói bằng Docker Compose (Khuyến nghị số 1)
1. **Yêu cầu trước:** Đã cài đặt Docker và Docker Compose plugin (`docker compose version`).
2. **Các bước thực hiện:**
   - Mở terminal và di chuyển vào thư mục dự án:
     ```bash
     cd "/đường_dẫn_tới_thư_mục/ứng dụng phân tán"
     ```
   - Build và khởi chạy tất cả 5 service dưới chế độ chạy nền:
     ```bash
     docker compose up -d --build
     ```
   - Kiểm tra trạng thái các container:
     ```bash
     docker compose ps
     ```
   - Truy cập các dịch vụ:
     - **Giao diện Web Client:** [http://localhost:8088](http://localhost:8088)
     - **API Swagger Documentation:** [http://localhost:5005/swagger](http://localhost:5005/swagger)
     - **RabbitMQ Management Dashboard:** [http://localhost:15672](http://localhost:15672) (Tài khoản: `guest` / `guest`)
   - Xem log của hệ thống khi cần debug:
     ```bash
     docker compose logs -f library-api reservation-worker
     ```
   - Tắt và giải phóng tài nguyên:
     ```bash
     docker compose down
     ```

#### Cách 2: Chạy trực tiếp từ mã nguồn (.NET SDK trên Ubuntu)
1. Cài đặt **.NET 10 SDK** trên Ubuntu:
   ```bash
   sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0
   ```
2. Khởi chạy 2 service hạ tầng PostgreSQL và RabbitMQ qua Docker:
   ```bash
   docker compose up -d postgres-db rabbitmq
   ```
3. Mở các tab terminal riêng biệt và chạy từng dịch vụ:
   - **Cửa sổ 1 (Chạy API):**
     ```bash
     dotnet run --project src/Library.API
     ```
   - **Cửa sổ 2 (Chạy Worker Service):**
     ```bash
     dotnet run --project src/Library.Worker
     ```
   - **Cửa sổ 3 (Chạy Web Client):**
     ```bash
     dotnet run --project src/Library.WebClient
     ```
   - **Cửa sổ 4 (Chạy Console Client để tương tác CLI):**
     ```bash
     dotnet run --project src/Library.ConsoleClient
     ```
4. Thực thi bộ kiểm thử tự động:
   ```bash
   dotnet test
   ```

---

## 5. Danh sách API endpoints

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

## 6. Luồng kiểm thử nghiệp vụ minh họa (Demo Flow)

1. Mở Web Client tại `http://localhost:8088` (hoặc mở Console Client qua `dotnet run --project src/Library.ConsoleClient`).
2. Chọn bạn đọc (ví dụ: *Nguyễn Văn An*).
3. Nhấn **Đặt mượn** cho một cuốn sách đang còn bản khả dụng:
   - Request gửi đến `POST /reservations`.
   - Lưu đơn ở trạng thái `Pending` trong Database.
   - Bắn thông điệp kèm `CorrelationId` vào exchange `library.exchange` của RabbitMQ.
   - Worker nhận thông điệp, kiểm tra số bản còn lại > 0:
     - Giảm `AvailableCopies` của sách đi 1.
     - Cập nhật trạng thái đơn thành `Reserved`.
4. Khi bạn đọc đến thư viện lấy sách: bấm **Nhận sách (Borrowed)** -> Trạng thái chuyển thành `Borrowed`.
5. Khi bạn đọc hoàn trả sách: bấm **Trả sách (Returned)** -> Trạng thái chuyển thành `Returned`, số bản khả dụng trong kho tự động được cộng lại (+1).
6. Khi sách hết bản khả dụng (`AvailableCopies = 0`): Các yêu cầu đặt mượn tiếp theo sẽ tự động bị Worker từ chối với trạng thái `Rejected`, đảm bảo không bao giờ xảy ra lỗi đặt lạm (Overbooking).
