using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

/// <summary>Denetim kaydı. Kaydı yalnızca bağlama ekler; çağıran aynı transaction içinde kaydeder.</summary>
public class AuditService(AppDbContext db)
{
    public void Add(UserContext? u, string action, string entityType, object entityId, string summary, int? workOrderId = null,
        object? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = u?.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString() ?? "",
            Summary = summary.Length > 1000 ? summary[..1000] : summary,
            WorkOrderId = workOrderId,
            IpAddress = u?.IpAddress,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details)
        });
    }
}

public record NotificationItem(long Id, int? WorkOrderId, string Title, string Message, NotificationType Type, DateTime CreatedAtUtc,
    bool IsRead);

/// <summary>
/// Uygulama içi bildirimler. İş kaydıyla aynı transaction'da oluşturulur.
/// (alıcı, tekilleştirme anahtarı) benzersiz olduğu için aynı olay ikinci kez bildirim üretmez.
/// </summary>
public class NotificationService(AppDbContext db)
{
    public async Task AddAsync(IEnumerable<string?> recipients, WorkOrder work, NotificationType type, string title, string message,
        string deduplicationKey, string? exceptUserId = null)
    {
        var ids = recipients.Where(r => !string.IsNullOrEmpty(r) && r != exceptUserId).Select(r => r!).Distinct().ToList();
        if (ids.Count == 0) return;

        var existing = await db.Notifications.Where(n => ids.Contains(n.RecipientUserId) && n.DeduplicationKey == deduplicationKey)
            .Select(n => n.RecipientUserId).ToListAsync();
        var pending = db.Notifications.Local.Where(n => n.DeduplicationKey == deduplicationKey).Select(n => n.RecipientUserId);
        var skip = existing.Concat(pending).ToHashSet();

        foreach (var id in ids.Where(i => !skip.Contains(i)))
        {
            db.Notifications.Add(new Notification
            {
                RecipientUserId = id,
                WorkOrderId = work.Id,
                NotificationType = type,
                Title = Trim($"{work.WorkNumber} · {title}", 200),
                Message = Trim(message, 1000),
                DeduplicationKey = Trim(deduplicationKey, 150)
            });
        }
    }

    /// <summary>Personel kayıtlarının uygulama hesapları (hesabı olmayanlar atlanır).</summary>
    public Task<List<string>> UserIdsOfEmployeesAsync(IEnumerable<int?> employeeIds)
    {
        var ids = employeeIds.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return db.Employees.Where(e => ids.Contains(e.Id) && e.ApplicationUserId != null)
            .Select(e => e.ApplicationUserId!).ToListAsync();
    }

    public Task<int> UnreadCountAsync(string userId) =>
        db.Notifications.CountAsync(n => n.RecipientUserId == userId && n.ReadAtUtc == null);

    public async Task<PagedResult<NotificationItem>> ListAsync(string userId, bool onlyUnread, int page, int pageSize = 30)
    {
        var q = db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == userId);
        if (onlyUnread) q = q.Where(n => n.ReadAtUtc == null);
        page = Math.Max(1, page);
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(n => new NotificationItem(n.Id, n.WorkOrderId, n.Title, n.Message, n.NotificationType, n.CreatedAtUtc, n.ReadAtUtc != null))
            .ToListAsync();
        return new PagedResult<NotificationItem> { Items = items, Page = page, PageSize = pageSize, Total = total };
    }

    public Task<List<NotificationItem>> LatestAsync(string userId, int take = 8) =>
        db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == userId)
            .OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).Take(take)
            .Select(n => new NotificationItem(n.Id, n.WorkOrderId, n.Title, n.Message, n.NotificationType, n.CreatedAtUtc, n.ReadAtUtc != null))
            .ToListAsync();

    /// <summary>Okundu işaretler ve bağlı işin numarasını döner. Başkasının bildirimine dokunulamaz.</summary>
    public async Task<int?> MarkReadAsync(string userId, long id)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.RecipientUserId == userId);
        if (n is null) return null;
        n.ReadAtUtc ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        return n.WorkOrderId;
    }

    public Task<int> MarkAllReadAsync(string userId) =>
        db.Notifications.Where(n => n.RecipientUserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, DateTime.UtcNow));

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
