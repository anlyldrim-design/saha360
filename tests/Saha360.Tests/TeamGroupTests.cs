using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Unvan kısıtı ve işe özel ekip alanları.</summary>
public class TeamGroupTests : IDisposable
{
    private readonly TestHost h = new();
    public void Dispose() => h.Dispose();

    private List<int> GroupsOf(int workId)
    {
        h.Db.ChangeTracker.Clear();
        return h.Db.WorkTeamGroups.Where(g => g.WorkOrderId == workId).OrderBy(g => g.SortOrder).Select(g => g.JobRoleId).ToList();
    }

    [Fact]
    public void Rule_allows_own_title_and_open_roles_only()
    {
        Assert.True(AssignmentRules.CanTakeRole(employeeTitleId: 2, roleId: 2, roleIsOpenToAll: false));
        Assert.False(AssignmentRules.CanTakeRole(employeeTitleId: 2, roleId: 1, roleIsOpenToAll: false));
        Assert.True(AssignmentRules.CanTakeRole(employeeTitleId: 2, roleId: 5, roleIsOpenToAll: true));
        Assert.True(AssignmentRules.CanTakeRole(employeeTitleId: null, roleId: 5, roleIsOpenToAll: true));
        Assert.False(AssignmentRules.CanTakeRole(employeeTitleId: null, roleId: 1, roleIsOpenToAll: false));
    }

    [Fact]
    public async Task Worker_cannot_be_placed_in_lead_role()
    {
        var w = await h.CreateAsync();
        var ex = await Assert.ThrowsAsync<DomainException>(() => h.AssignAsync(w, false, h.A(h.Ali, h.LeadRole)));
        Assert.Contains("Şef", ex.Message);
        Assert.Contains("atanamaz", ex.Message);
        Assert.Empty(h.Reload(w.Id).Assignments);
    }

    [Fact]
    public async Task Anyone_can_be_placed_in_open_role()
    {
        var w = await h.CreateAsync();
        await h.AssignAsync(w, false, h.A(h.Ali, h.DriverRole), h.A(h.Fatma));
        Assert.Equal(h.DriverRole.Id, h.Reload(w.Id).Assignments.Single(a => a.EmployeeId == h.Ali.EmployeeId).JobRoleId);
    }

    [Fact]
    public async Task Moving_existing_member_into_restricted_role_is_rejected()
    {
        var w = await h.CreateAsync();
        await h.AssignAsync(w, false, h.A(h.Ali));
        await Assert.ThrowsAsync<DomainException>(() => h.AssignAsync(w, false, h.A(h.Ali, h.EngineerRole)));
        Assert.Equal(h.WorkerRole.Id, h.Reload(w.Id).Assignments.Single().JobRoleId);
    }

    [Fact]
    public async Task Empty_groups_are_kept_per_work_and_assigned_roles_are_added()
    {
        var w = await h.CreateAsync();
        var svc = h.Get<AssignmentService>();
        await svc.SaveAsync(h.Planner, w.Id, [], false, null, [h.LeadRole.Id, h.EngineerRole.Id]);
        Assert.Equal([h.LeadRole.Id, h.EngineerRole.Id], GroupsOf(w.Id));

        // Atanan kişinin alanı listede yoksa eklenir; grup listesi verilmezse mevcutlar korunur.
        await svc.SaveAsync(h.Planner, w.Id, [h.A(h.Ali)], false, null);
        Assert.Equal([h.LeadRole.Id, h.EngineerRole.Id, h.WorkerRole.Id], GroupsOf(w.Id));

        // Planlamacı boş alanı kaldırabilir.
        await svc.SaveAsync(h.Planner, w.Id, [h.A(h.Ali)], false, null, [h.WorkerRole.Id]);
        Assert.Equal([h.WorkerRole.Id], GroupsOf(w.Id));

        // Dolu alan kaldırılmak istense de kişinin alanı korunur.
        await svc.SaveAsync(h.Planner, w.Id, [h.A(h.Ali)], false, null, []);
        Assert.Equal([h.WorkerRole.Id], GroupsOf(w.Id));
        Assert.Contains(h.Db.AuditLogs, a => a.WorkOrderId == w.Id && a.Action == "TeamGroupsChanged");
    }
}
