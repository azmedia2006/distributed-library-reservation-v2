using System.Data;
using Library.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Library.Infrastructure;

public enum ReservationProcessOutcome
{
    Approved,
    Rejected,
    AlreadyProcessed,
    NotFound
}

public record ReservationProcessResult(
    ReservationProcessOutcome Outcome,
    Reservation? Reservation,
    int? AvailableCopies);

public record ReservationStatusUpdateResult(
    bool Success,
    bool NotFound,
    string? ErrorMessage,
    Reservation? Reservation,
    int? AvailableCopies);

public interface IReservationService
{
    Task<ReservationProcessResult> ProcessReservationAsync(
        ReservationRequestedMessage message,
        string correlationId,
        CancellationToken ct = default);

    Task<ReservationStatusUpdateResult> UpdateStatusAsync(
        Guid reservationId,
        ReservationStatus newStatus,
        CancellationToken ct = default);

    Task<ReservationStatusUpdateResult> CancelReservationAsync(
        Guid reservationId,
        string? reason = null,
        CancellationToken ct = default);
}

public class ReservationService : IReservationService
{
    private readonly LibraryDbContext _db;
    private readonly ILogger<ReservationService> _logger;

    public ReservationService(LibraryDbContext db, ILogger<ReservationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReservationProcessResult> ProcessReservationAsync(
        ReservationRequestedMessage message,
        string correlationId,
        CancellationToken ct = default)
    {
        IDbContextTransaction? transaction = null;
        if (_db.Database.IsRelational())
        {
            transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        }

        try
        {
            var reservation = await _db.Reservations
                .Include(r => r.Book)
                .FirstOrDefaultAsync(r => r.Id == message.ReservationId, ct);

            if (reservation is null)
            {
                _logger.LogWarning(
                    "[CorrelationId: {CorrelationId}] Reservation {ReservationId} not found in database.",
                    correlationId, message.ReservationId);
                return new ReservationProcessResult(ReservationProcessOutcome.NotFound, null, null);
            }

            if (string.IsNullOrWhiteSpace(reservation.CorrelationId) && !string.IsNullOrWhiteSpace(correlationId))
            {
                reservation.CorrelationId = correlationId;
            }

            var effectiveCid = reservation.CorrelationId ?? correlationId ?? string.Empty;

            // Idempotent consumer check: skip duplicate message safely
            if (reservation.Status != ReservationStatus.Pending)
            {
                _logger.LogInformation(
                    "[CorrelationId: {CorrelationId}] Reservation {ReservationId} already processed with status {Status}. Skipping duplicate delivery.",
                    effectiveCid, reservation.Id, reservation.Status);

                _db.AuditLogs.Add(new AuditLog
                {
                    ReservationId = reservation.Id,
                    CorrelationId = effectiveCid,
                    ServiceName = "Library.Worker",
                    EventType = "IdempotentSkipped",
                    Message = $"Bỏ qua bản tin lặp lại của phiếu #{reservation.Id.ToString()[..8]} (trạng thái hiện tại: {reservation.Status}), giữ nguyên tồn kho."
                });

                await _db.SaveChangesAsync(ct);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(ct);
                }
                return new ReservationProcessResult(
                    ReservationProcessOutcome.AlreadyProcessed,
                    reservation,
                    reservation.Book?.AvailableCopies);
            }

            var book = reservation.Book ?? await _db.Books.FirstOrDefaultAsync(b => b.Id == message.BookId, ct);
            if (book is null)
            {
                reservation.Status = ReservationStatus.Rejected;
                reservation.ReasonMessage = "Không tìm thấy thông tin sách trong hệ thống.";
                reservation.ProcessedAt = DateTime.UtcNow;

                _db.AuditLogs.Add(new AuditLog
                {
                    ReservationId = reservation.Id,
                    CorrelationId = effectiveCid,
                    ServiceName = "Library.Worker",
                    EventType = "WorkerRejected",
                    Message = $"Từ chối phiếu #{reservation.Id.ToString()[..8]} do không tìm thấy đầu sách tương ứng."
                });

                await _db.SaveChangesAsync(ct);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(ct);
                }
                return new ReservationProcessResult(ReservationProcessOutcome.Rejected, reservation, null);
            }

            _logger.LogInformation(
                "[CorrelationId: {CorrelationId}] Checking inventory for book '{Title}'. Available: {Available}/{Total}",
                effectiveCid, book.Title, book.AvailableCopies, book.TotalCopies);

            ReservationProcessOutcome outcome;
            if (book.AvailableCopies > 0)
            {
                book.AvailableCopies -= 1;
                reservation.Status = ReservationStatus.Reserved;
                reservation.ReasonMessage = "Đã xác nhận giữ sách thành công. Vui lòng đến quầy thư viện để nhận sách.";
                reservation.ProcessedAt = DateTime.UtcNow;
                outcome = ReservationProcessOutcome.Approved;

                _db.AuditLogs.Add(new AuditLog
                {
                    ReservationId = reservation.Id,
                    CorrelationId = effectiveCid,
                    ServiceName = "Library.Worker",
                    EventType = "WorkerApproved",
                    Message = $"Duyệt giữ sách '{book.Title}' cho phiếu #{reservation.Id.ToString()[..8]}. Tồn kho còn {book.AvailableCopies}/{book.TotalCopies} quyển."
                });

                _logger.LogInformation(
                    "[CorrelationId: {CorrelationId}] APPROVED Reservation {ReservationId}. Remaining copies: {Remaining}/{Total}",
                    effectiveCid, reservation.Id, book.AvailableCopies, book.TotalCopies);
            }
            else
            {
                reservation.Status = ReservationStatus.Rejected;
                reservation.ReasonMessage = "Hết sách khả dụng tại thời điểm xử lý. Yêu cầu đặt mượn đã bị từ chối.";
                reservation.ProcessedAt = DateTime.UtcNow;
                outcome = ReservationProcessOutcome.Rejected;

                _db.AuditLogs.Add(new AuditLog
                {
                    ReservationId = reservation.Id,
                    CorrelationId = effectiveCid,
                    ServiceName = "Library.Worker",
                    EventType = "WorkerRejected",
                    Message = $"Từ chối phiếu #{reservation.Id.ToString()[..8]} ('{book.Title}') do hết bản khả dụng (0/{book.TotalCopies})."
                });

                _logger.LogWarning(
                    "[CorrelationId: {CorrelationId}] REJECTED Reservation {ReservationId} (Out of stock).",
                    effectiveCid, reservation.Id);
            }

            await _db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return new ReservationProcessResult(outcome, reservation, book.AvailableCopies);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    public async Task<ReservationStatusUpdateResult> UpdateStatusAsync(
        Guid reservationId,
        ReservationStatus newStatus,
        CancellationToken ct = default)
    {
        var reservation = await _db.Reservations
            .Include(r => r.Book)
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

        if (reservation is null)
        {
            return new ReservationStatusUpdateResult(
                false, true, "Không tìm thấy yêu cầu đặt mượn.", null, null);
        }

        var cid = reservation.CorrelationId ?? string.Empty;

        if (newStatus == ReservationStatus.Borrowed)
        {
            if (reservation.Status != ReservationStatus.Reserved)
            {
                return new ReservationStatusUpdateResult(
                    false,
                    false,
                    $"Chỉ đơn ở trạng thái 'Reserved' (Đã duyệt giữ sách) mới có thể nhận sách. Trạng thái hiện tại: '{reservation.Status}'.",
                    reservation,
                    reservation.Book?.AvailableCopies);
            }

            reservation.Status = ReservationStatus.Borrowed;
            reservation.BorrowedAt = DateTime.UtcNow;
            reservation.ReasonMessage = "Bạn đọc đã nhận sách và đang trong thời gian mượn.";

            _db.AuditLogs.Add(new AuditLog
            {
                ReservationId = reservation.Id,
                CorrelationId = cid,
                ServiceName = "Library.API",
                EventType = "BookBorrowed",
                Message = $"Bàn giao sách '{reservation.Book?.Title}' cho phiếu #{reservation.Id.ToString()[..8]} (Borrowed)."
            });
        }
        else if (newStatus == ReservationStatus.Returned)
        {
            if (reservation.Status != ReservationStatus.Borrowed)
            {
                return new ReservationStatusUpdateResult(
                    false,
                    false,
                    $"Chỉ đơn ở trạng thái 'Borrowed' (Đang mượn) mới có thể trả sách. Trạng thái hiện tại: '{reservation.Status}'.",
                    reservation,
                    reservation.Book?.AvailableCopies);
            }

            reservation.Status = ReservationStatus.Returned;
            reservation.ReturnedAt = DateTime.UtcNow;
            reservation.ReasonMessage = "Bạn đọc đã hoàn trả sách về kho thư viện.";

            if (reservation.Book is not null)
            {
                reservation.Book.AvailableCopies = Math.Min(
                    reservation.Book.TotalCopies,
                    reservation.Book.AvailableCopies + 1);
            }

            _db.AuditLogs.Add(new AuditLog
            {
                ReservationId = reservation.Id,
                CorrelationId = cid,
                ServiceName = "Library.API",
                EventType = "BookReturned",
                Message = $"Thu hồi sách '{reservation.Book?.Title}' từ phiếu #{reservation.Id.ToString()[..8]}. Tồn kho phục hồi lên {reservation.Book?.AvailableCopies}/{reservation.Book?.TotalCopies}."
            });
        }
        else
        {
            return new ReservationStatusUpdateResult(
                false,
                false,
                "Trạng thái cập nhật hợp lệ là 'Borrowed' (3) hoặc 'Returned' (4).",
                reservation,
                reservation.Book?.AvailableCopies);
        }

        await _db.SaveChangesAsync(ct);

        return new ReservationStatusUpdateResult(
            true, false, null, reservation, reservation.Book?.AvailableCopies);
    }

    public async Task<ReservationStatusUpdateResult> CancelReservationAsync(
        Guid reservationId,
        string? reason = null,
        CancellationToken ct = default)
    {
        var reservation = await _db.Reservations
            .Include(r => r.Book)
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

        if (reservation is null)
        {
            return new ReservationStatusUpdateResult(
                false, true, "Không tìm thấy yêu cầu đặt mượn.", null, null);
        }

        if (reservation.Status != ReservationStatus.Pending && reservation.Status != ReservationStatus.Reserved)
        {
            return new ReservationStatusUpdateResult(
                false,
                false,
                $"Chỉ có thể hủy đơn đang 'Pending' hoặc 'Reserved'. Trạng thái hiện tại: '{reservation.Status}'.",
                reservation,
                reservation.Book?.AvailableCopies);
        }

        var wasReserved = reservation.Status == ReservationStatus.Reserved;
        reservation.Status = ReservationStatus.Rejected;
        reservation.ProcessedAt ??= DateTime.UtcNow;
        reservation.ReasonMessage = string.IsNullOrWhiteSpace(reason)
            ? "Đơn đặt trước đã được hủy và hoàn trả bản sách vào kho khả dụng."
            : reason.Trim();

        if (wasReserved && reservation.Book is not null)
        {
            reservation.Book.AvailableCopies = Math.Min(
                reservation.Book.TotalCopies,
                reservation.Book.AvailableCopies + 1);
        }

        _db.AuditLogs.Add(new AuditLog
        {
            ReservationId = reservation.Id,
            CorrelationId = reservation.CorrelationId ?? string.Empty,
            ServiceName = "Library.API",
            EventType = "ReservationCancelled",
            Message = wasReserved
                ? $"Hủy phiếu giữ chỗ #{reservation.Id.ToString()[..8]} và hoàn +1 bản '{reservation.Book?.Title}' vào kho."
                : $"Hủy phiếu chờ #{reservation.Id.ToString()[..8]} trước khi trừ kho."
        });

        await _db.SaveChangesAsync(ct);

        return new ReservationStatusUpdateResult(
            true, false, null, reservation, reservation.Book?.AvailableCopies);
    }
}
