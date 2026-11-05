using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Şartname 14.1 — personel atama ve havuz.</summary>
public class AssignmentTests : IDisposable
{
    private readonly TestHost h = new();
    public void Dispose() => h.Dispose();

    private async Task<PoolState> StateAsync(UserContext who, WorkOrder w, int? exclude = null)
    {
        var eval = await h.Get<AvailabilityService>().EvaluateAsync([who.EmployeeId!.Value], w.PlannedStartAtUtc, w.PlannedEndAtUtc, exclude);
        return eval[who.EmployeeId!.Value].State;
    }

    [Fact]
    public async Task Saved_assignment_removes_employee_from_pool_for_that_interval_only()
    {
        var w = await h.CreateAsync("Sabah işi", startHoursFromNow: 24, durationHours: 4);
        Assert.Equal(PoolState.Available, await StateAsync(h.Ali, w));

        await h.AssignAsync(w, false, h.A(h.Ali));
        Assert.Equal(PoolState.Busy, await StateAsync(h.Ali, w));

        // Aynı gün, iş bittikten sonraki saatler serbest.
        var later = await h.CreateAsync("Öğleden sonra", startHoursFromNow: 28, durationHours: 3);
        Assert.Equal(PoolState.Available, await StateAsync(h.Ali, later));
    }

    [Fact]
    public async Task Pool_count_is_derived_from_assignments_not_a_counter()
    {
        var pool = h.Get<AvailabilityService>();
        var w = await h.CreateAsync("Şimdi", startHoursFromNow: -1, durationHours: 4);
        var (before, _, _) = await pool.CountNowAsync(h.Planner);
        await h.AssignAsync(w, false, h.A(h.Ali), h.A(h.Fatma));
        var (during, busy, _) = await pool.CountNowAsync(h.Planner);
        Assert.Equal(before - 2, during);
        Assert.Equal(2, busy);

        await h.AssignAsync(w, false, h.A(h.Ali)); // Fatma çıkarıldı
        var (after, _, _) = await pool.CountNowAsync(h.Planner);
        Assert.Equal(before - 1, after);
    }

    [Fact]
    public async Task Same_employee_cannot_be_added_twice()
    {
        var w = await h.CreateAsync();
        await Assert.ThrowsAsync<DomainException>(() => h.AssignAsync(w, false, h.A(h.Ali), h.A(h.Ali, h.LeadRole)));
    }

    [Fact]
    public async Task Overlapping_work_assignment_is_blocked_with_reason()
    {
        var w1 = await h.CreateAsync("Birinci", 24, 8);
        var w2 = await h.CreateAsync("İkinci", 26, 8);
        await h.AssignAsync(w1, false, h.A(h.Ali));
        var ex = await Assert.ThrowsAsync<DomainException>(() => h.AssignAsync(w2, false, h.A(h.Ali)));
        Assert.Contains(h.Reload(w1.Id).WorkNumber, ex.Message);
        Assert.Empty(h.Reload(w2.Id).Assignments);
    }

    [Fact]
    public async Task Removed_assignment_frees_employee_and_keeps_history()
    {
        var w1 = await h.CreateAsync("Birinci", 24, 8);
        var w2 = await h.CreateAsync("İkinci", 26, 8);
        await h.AssignAsync(w1, false, h.A(h.Ali));
        await h.AssignAsync(w1, false); // ekip boşaltıldı
        await h.AssignAsync(w2, false, h.A(h.Ali));

        var row = h.Reload(w1.Id).Assignments.Single();
        Assert.Equal(AssignmentStatus.Released, row.AssignmentStatus);
        Assert.NotNull(row.ReleasedAtUtc);
    }

    [Fact]
    public async Task Employee_on_leave_cannot_be_assigned()
    {
        var w = await h.CreateAsync("İzin günü", 24, 8);
        await h.Get<EmployeeService>().AddAvailabilityAsync(h.Manager, h.Fatma.EmployeeId!.Value, AvailabilityType.Leave,
            TrTime.NowLocal.AddHours(20), TrTime.NowLocal.AddHours(40), "Yıllık izin");
        var ex = await Assert.ThrowsAsync<DomainException>(() => h.AssignAsync(w, false, h.A(h.Fatma)));
        Assert.Contains("İzinli", ex.Message);
    }

    [Fact]
    public async Task Stale_page_version_is_rejected()
    {
        var w = await h.CreateAsync();
        var oldVersion = h.Reload(w.Id).UpdatedAtUtc.Ticks;
        await h.AssignAsync(w, false, h.A(h.Ali)); // başka bir planlamacı kaydetti
        await Assert.ThrowsAsync<DomainException>(() =>
            h.Get<AssignmentService>().SaveAsync(h.Planner, w.Id, [h.A(h.Fatma)], false, oldVersion));
    }

    [Fact]
    public async Task Planner_of_other_unit_cannot_plan()
    {
        var w = await h.CreateAsync();
        var foreign = await h.UserAsync("baska-planlama", Domain.Security.SystemRoles.Planner, null,
            await h.Db.OrganizationUnits.Where(u => u.Name == "Diğer Birim").Select(u => u.Id).SingleAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => h.Get<AssignmentService>().SaveAsync(foreign, w.Id, [h.A(h.Ali)], false, null));
    }
}
