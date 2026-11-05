using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Enums;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

public record DashboardCard(string Key, string Title, int Value, string Url, string Tone, string Hint);
public record StatusCount(WorkStatus Status, int Count);
public record ActivityItem(DateTime AtUtc, string? ActorName, string Summary, int? WorkOrderId, string? WorkNumber);

public class DashboardData
{
    public List<DashboardCard> Cards { get; init; } = [];
    public List<StatusCount> ByStatus { get; init; } = [];
    public List<WorkListItem> RecentWorks { get; init; } = [];
    public List<ActivityItem> Activity { get; init; } = [];
}

/// <summary>Rol bazlı gösterge paneli. Tüm sayılar kullanıcının görme kapsamından hesaplanır.</summary>
public class DashboardService(AppDbContext db, AccessService access, AvailabilityService availability, WorkOrderService works)
{
    public async Task<DashboardData> GetAsync(UserContext u)
    {
        var now = DateTime.UtcNow;
        var visible = access.VisibleWorks(u).AsNoTracking();
        async Task<int> Count(string view, bool overdue = false) =>
            await WorkOrderService.Filter(visible, new WorkListQuery { View = view, Overdue = overdue }, u, now).CountAsync();

        var cards = new List<DashboardCard>
        {
            new("bugun", "Bugün planlanan", await Count("bugun"), "/isler?view=bugun", "primary", "Bugüne denk gelen işler"),
            new("devam", "Devam eden", await Count("devam"), "/isler?view=devam", "primary", "Sahada çalışma sürüyor"),
        };
        if (u.CanAssign || u.CanCreateWork)
            cards.Add(new("atama", "Atama bekleyen", await Count("atama"), "/isler?view=atama", "warning", "Taslak veya ekibi eksik"));
        cards.Add(new("onay", "Şef onayı bekleyen", await Count("onay"), "/isler?view=onay", "warning", "Tamamlanma bildirimi gönderildi"));
        cards.Add(new("iade", "Reddedilen / iade", await Count("iade"), "/isler?view=iade", "danger", "Yeniden planlama veya düzeltme gerekli"));
        cards.Add(new("geciken", "Geciken", await Count("aktif", overdue: true), "/isler?view=aktif&overdue=true", "danger", "Planlanan bitişi geçti"));
        cards.Add(new("tamamlanan", "Tamamlanan", await Count("tamamlanan"), "/isler?view=tamamlanan", "success", "Şef onayıyla kapanan"));
        if (u.CanSeeOrganization)
        {
            var (available, busy, leave) = await availability.CountNowAsync(u);
            cards.Add(new("havuz", "Havuzda müsait", available, "/havuz?durum=Available", "success", "Şu an boşta olan aktif personel"));
            cards.Add(new("atanmis", "İşe atanmış", busy, "/havuz?durum=Busy", "secondary", $"Şu an bir işte · {leave} kişi izinli"));
        }

        var byStatus = await visible.GroupBy(w => w.Status).Select(g => new StatusCount(g.Key, g.Count())).ToListAsync();
        var recent = await works.ListAsync(u, new WorkListQuery { PageSize = 10 });

        var visibleIds = visible.Select(w => w.Id);
        var activity = await db.AuditLogs.AsNoTracking()
            .Where(a => a.WorkOrderId != null && visibleIds.Contains(a.WorkOrderId.Value))
            .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).Take(12)
            .Select(a => new
            {
                a.OccurredAtUtc, a.Summary, a.WorkOrderId, a.ActorUserId,
                WorkNumber = db.WorkOrders.Where(w => w.Id == a.WorkOrderId).Select(w => w.WorkNumber).FirstOrDefault()
            })
            .ToListAsync();
        var actorIds = activity.Select(a => a.ActorUserId).Where(x => x != null).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(x => actorIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName);

        return new DashboardData
        {
            Cards = cards,
            ByStatus = byStatus.OrderBy(s => s.Status).ToList(),
            RecentWorks = recent.Items,
            Activity = activity.Select(a => new ActivityItem(a.OccurredAtUtc, a.ActorUserId is not null && names.TryGetValue(a.ActorUserId, out var n) ? n : "Sistem",
                a.Summary, a.WorkOrderId, a.WorkNumber)).ToList()
        };
    }
}
