@echo off
chcp 65001 >nul
title Khởi Chạy Hệ Thống Thư Viện Phân Tán (Đề tài 20)

echo ==============================================================================
echo       HỆ THỐNG THƯ VIỆN & ĐẶT MƯỢN SÁCH PHÂN TÁN (DISTRIBUTED SYSTEM)
echo                          Học phần: Ứng dụng phân tán - Đề tài 20
echo ==============================================================================
echo.

where docker >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [LỖI] Không tìm thấy Docker trên máy tính của bạn!
    echo Vui lòng cài đặt và khởi động Docker Desktop for Windows trước khi chạy.
    echo.
    pause
    exit /b 1
)

echo [1/3] Đang kiểm tra Docker Desktop...
docker info >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [LỖI] Docker Desktop chưa được khởi động!
    echo Hãy mở Docker Desktop và chờ biểu tượng cá voi chuyển sang trạng thái xanh (Running).
    echo.
    pause
    exit /b 1
)

echo [2/3] Đang build và khởi chạy 5 dịch vụ (PostgreSQL, RabbitMQ, API, Worker, Web Client)...
docker compose up -d --build

if %ERRORLEVEL% neq 0 (
    echo.
    echo [LỖI] Khởi chạy dịch vụ thất bại! Vui lòng kiểm tra lại log ở trên.
    echo.
    pause
    exit /b 1
)

echo.
echo [3/3] KHỞI CHẠY HỆ THỐNG THÀNH CÔNG!
echo ==============================================================================
echo  - Giao diện Web Client:       http://localhost:8088
echo  - Swagger API Documentation:  http://localhost:5005/swagger
echo  - RabbitMQ Management:        http://localhost:15672 (guest / guest)
echo  - Health Check Endpoint:      http://localhost:5005/health
echo ==============================================================================
echo.
echo Đang mở trình duyệt truy cập Web Client...
timeout /t 2 /nobreak >nul
start http://localhost:8088

echo.
echo Khi muốn dừng hệ thống, hãy chạy file 'stop-windows.bat' hoặc lệnh: docker compose down
echo.
pause
