@echo off
chcp 65001 >nul
title Console Client - Hệ Thống Thư Viện Phân Tán

echo ==============================================================================
echo             KHỞI ĐỘNG CONSOLE CLIENT (ỨNG DỤNG DÒNG LỆNH CLI)
echo ==============================================================================
echo.

where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [LỖI] Chưa cài đặt .NET SDK trên máy tính!
    pause
    exit /b 1
)

dotnet run --project src\Library.ConsoleClient
pause
