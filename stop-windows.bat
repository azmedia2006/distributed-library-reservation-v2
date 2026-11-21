@echo off
chcp 65001 >nul
title Dừng Hệ Thống Thư Viện Phân Tán

echo ==============================================================================
echo                 ĐANG DỪNG HỆ THỐNG THƯ VIỆN PHÂN TÁN...
echo ==============================================================================
echo.

docker compose down

echo.
echo Đã dừng toàn bộ dịch vụ và giải phóng tài nguyên thành công!
echo.
pause
