@echo off
chcp 65001 >nul
title Console Client - Hệ Thống Thư Viện Phân Tán

echo ==============================================================================
echo             KHỞI ĐỘNG CONSOLE CLIENT (ỨNG DỤNG DÒNG LỆNH CLI)
echo ==============================================================================
echo.

where dotnet >nul 2>nul
if %ERRORLEVEL% equ 0 (
    dotnet run --project src\Library.ConsoleClient
    pause
    exit /b 0
)

where docker >nul 2>nul
if %ERRORLEVEL% equ 0 (
    echo [THÔNG BÁO] Máy chưa cài .NET SDK — tự động khởi chạy Console Client qua Docker Container...
    echo.
    docker compose --profile cli run --rm console-client
    pause
    exit /b 0
)

echo [LỖI] Không tìm thấy .NET 10 SDK hoặc Docker Desktop trên máy tính!
pause
exit /b 1
