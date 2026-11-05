using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Enums;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

/// <summary>
/// Personel uygunluğu ve havuz. "Havuzda mı?" hiçbir yerde saklanmaz; her seferinde
/// aktiflik + izin dönemleri + çakışan aktif atamalardan hesaplanır. Bu yüzden sayaç kayması olmaz.
/// </summary>
public class AvailabilityService(AppDbContext db, AccessService access)
{
    private static readonly WorkStatus[] StartedStatuses =
        [WorkStatus.InProgress, WorkStatus.CompletionApprovalPending, WorkStatus.ReturnedBySupervisor];

    public record Evaluation(PoolState State, string? Reason);

    /// <summary>
    /// Verilen personelin [start, end) aralığındaki uygunluğu. <paramref name="excludeWorkId"/> işindeki atamalar sayılmaz
    /// (kişi zaten o işte). Başlamış ama süresi geçmiş işler bitişi "şimdi"ye kadar uzamış sayılır: iş devam ederken
    /// personel kendiliğinden serbest kalmaz.
    /// </summary>
    public async Task<Dictionary<int, Evaluation>> EvaluateAsync(IReadOnlyCollection<int> employeeIds, DateTime startUtc, DateTime endUtc,
        int? excludeWorkId = null)
    {
        var result = new Dictionary<int, Evaluation>();
        if (employeeIds.Count == 0) return result;
        var ids = employeeIds.Distinct().ToList();
        var now = DateTime.UtcNow;

        var inactive = await db.Employees.Where(e => ids.Contains(e.Id) && !e.IsActive).Select(e => e.Id).ToListAsync();
        foreach (var id in inactive) result[id] = new Evaluation(PoolState.Inactive, "Pasif personel");

        var leaves = await db.EmployeeAvailabilities
            .Where(a => ids.Contains(a.EmployeeId) && a.StartAtUtc < endUtc && startUtc < a.EndAtUtc)
            .OrderBy(a => a.StartAtUtc)
            .Select(a => new { a.EmployeeId, a.Type, a.StartAtUtc, a.EndAtUtc })
            .ToListAsync();
        foreach (var l in leaves)
        {
            if (result.ContainsKey(l.EmployeeId)) continue;
            result[l.EmployeeId] = new Evaluation(PoolState.OnLeave,
                $"{TrNames.Of(l.Type)} ({TrTime.Format(l.StartAtUtc, "dd.MM HH:mm")} – {TrTime.Format(l.EndAtUtc, "dd.MM HH:mm")})");
        }

        var exclude = excludeWorkId ?? 0;
        var busy = await db.WorkAssignments
            .Where(a => ids.Contains(a.EmployeeId) && a.ReleasedAtUtc == null && a.WorkOrderId != exclude
                        && a.WorkOrder.Status != WorkStatus.Completed && a.WorkOrder.Status != WorkStatus.Cancelled
                        && a.WorkOrder.PlannedStartAtUtc < endUtc
                        && (a.WorkOrder.PlannedEndAtUtc > startUtc || (StartedStatuses.Contains(a.WorkOrder.Status) && now > startUtc)))
            .OrderBy(a => a.WorkOrder.PlannedStartAtUtc)
            .Select(a => new { a.EmployeeId, a.WorkOrder.WorkNumber, a.WorkOrder.PlannedStartAtUtc, a.WorkOrder.PlannedEndAtUtc })
            .ToListAsync();
        foreach (var b in busy)
        {
            if (result.ContainsKey(b.EmployeeId)) continue;
            // Yalnızca iş numarası ve zaman aralığı gösterilir; başka kişisel bilgi açığa çıkmaz.
            result[b.EmployeeId] = new Evaluation(PoolState.Busy,
                $"{b.WorkNumber} işinde ({TrTime.Range(b.PlannedStartAtUtc, b.PlannedEndAtUtc)})");
        }

        foreach (var id in ids.Where(i => !result.ContainsKey(i)))
            result[id] = new Evaluation(PoolState.Available, null);
        return result;
    }

    /// <summary>Personel havuzu ekranı: filtrelenmiş personel ve her birinin seçili aralıktaki durumu.</summary>
    public async Task<List<PoolItem>> GetPoolAsync(UserContext u, PoolQuery query, DateTime startUtc, DateTime endUtc, int? excludeWorkId)
    {
        var q = access.ScopedEmployees(u).AsNoTracking();
        if (!query.IncludeInactive) q = q.Where(e => e.IsActive);
        if (query.UnitId is int unit) q = q.Where(e => e.OrganizationUnitId == unit);
        if (query.TeamId is int team) q = q.Where(e => e.TeamId == team);
        if (query.JobRoleId is int role) q = q.Where(e => e.JobRoleId == role);
        if (query.SupervisorId is int sup) q = q.Where(e => e.SupervisorEmployeeId == sup || e.Team!.SupervisorEmployeeId == sup);
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim();
            q = u.CanSeeSensitive
                ? q.Where(e => (e.FirstName + " " + e.LastName).Contains(term) || e.EmployeeNumber == term)
                : q.Where(e => (e.FirstName + " " + e.LastName).Contains(term));
        }

        var rows = await q.OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new
            {
                e.Id, e.FirstName, e.LastName, e.EmployeeNumber, e.JobRoleId, JobRole = e.JobRole!.Name, e.TeamId, Team = e.Team!.Name,
                Supervisor = e.SupervisorEmployee != null ? e.SupervisorEmployee.FirstName + " " + e.SupervisorEmployee.LastName : null,
                HasAccount = e.ApplicationUserId != null
            })
            .Take(500)
            .ToListAsync();

        var eval = await EvaluateAsync(rows.Select(r => r.Id).ToList(), startUtc, endUtc, excludeWorkId);
        var items = rows.Select(r =>
        {
            var ev = eval[r.Id];
            return new PoolItem(r.Id, $"{r.FirstName} {r.LastName}", u.CanSeeSensitive ? r.EmployeeNumber : null, r.JobRoleId, r.JobRole,
                r.TeamId, r.Team, r.Supervisor, r.HasAccount, ev.State, ev.Reason);
        });
        if (query.State is PoolState st) items = items.Where(i => i.State == st);
        return items.ToList();
    }

    /// <summary>Şu an (bu saat dilimi için) müsait ve atanmış personel sayıları. Sayaçtan değil, kayıtlardan türetilir.</summary>
    public async Task<(int Available, int Busy, int OnLeave)> CountNowAsync(UserContext u)
    {
        var ids = await access.ScopedEmployees(u).Where(e => e.IsActive).Select(e => e.Id).ToListAsync();
        var now = DateTime.UtcNow;
        var eval = await EvaluateAsync(ids, now, now.AddMinutes(1));
        return (eval.Values.Count(v => v.State == PoolState.Available), eval.Values.Count(v => v.State == PoolState.Busy),
            eval.Values.Count(v => v.State == PoolState.OnLeave));
    }
}
