using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Ekip üyelikleri ve kullanıcı ↔ personel birlikte oluşturma.</summary>
public class TeamAndAccountTests : IDisposable
{
    private readonly TestHost h = new();
    public void Dispose() => h.Dispose();
    private DefinitionService Defs => h.Get<DefinitionService>();

    private async Task<UserContext> AdminAsync() => await h.UserAsync("yonetici", SystemRoles.SystemAdmin, null);

    private Employee Emp(string first) { h.Db.ChangeTracker.Clear(); return h.Db.Employees.Single(e => e.FirstName == first); }

    [Fact]
    public async Task Members_are_added_in_bulk_with_team_supervisor_and_can_be_removed()
    {
        var admin = await AdminAsync();
        var team = await Defs.SaveTeamAsync(admin, null, "Saha Ekibi", h.Unit.Id, h.Supervisor.EmployeeId, null, true);
        h.Db.ChangeTracker.Clear();
        Assert.Equal(team.Id, h.Db.Employees.Single(e => e.Id == h.Supervisor.EmployeeId).TeamId);   // şef ekibe alındı

        var n = await Defs.AddTeamMembersAsync(h.Manager, team.Id, [h.Ali.EmployeeId!.Value, h.Fatma.EmployeeId!.Value], setSupervisor: true);
        Assert.Equal(2, n);
        var ali = Emp("Ali");
        Assert.Equal(team.Id, ali.TeamId);
        Assert.Equal(h.Supervisor.EmployeeId, ali.SupervisorEmployeeId);

        var detail = await Defs.TeamDetailAsync(admin, team.Id);
        Assert.Equal(3, detail.Members.Count);                       // şef + 2 üye
        Assert.True(detail.Members.First().IsTeamSupervisor);         // şef en üstte
        Assert.DoesNotContain(detail.Candidates, c => c.Id == ali.Id);

        await Defs.RemoveTeamMemberAsync(h.Manager, team.Id, ali.Id);
        ali = Emp("Ali");
        Assert.Null(ali.TeamId);
        Assert.Null(ali.SupervisorEmployeeId);
        await Assert.ThrowsAsync<DomainException>(() => Defs.RemoveTeamMemberAsync(admin, team.Id, h.Supervisor.EmployeeId!.Value));
    }

    [Fact]
    public async Task Changing_team_supervisor_moves_members_to_new_supervisor()
    {
        var admin = await AdminAsync();
        var team = await Defs.SaveTeamAsync(admin, null, "Saha Ekibi", h.Unit.Id, h.Supervisor.EmployeeId, null, true);
        await Defs.AddTeamMembersAsync(admin, team.Id, [h.Ali.EmployeeId!.Value], setSupervisor: true);
        await Defs.SaveTeamAsync(admin, team.Id, "Saha Ekibi", h.Unit.Id, h.OtherSupervisor.EmployeeId, null, true);
        Assert.Equal(h.OtherSupervisor.EmployeeId, Emp("Ali").SupervisorEmployeeId);
    }

    [Fact]
    public async Task Team_members_are_managed_only_by_unit_manager_within_unit()
    {
        var admin = await AdminAsync();
        var team = await Defs.SaveTeamAsync(admin, null, "Saha Ekibi", h.Unit.Id, null, null, true);
        // Başka birimdeki personel bu birimin yöneticisine görünmez.
        await Assert.ThrowsAsync<DomainException>(() => Defs.AddTeamMembersAsync(h.Manager, team.Id, [h.Outsider.EmployeeId!.Value], false));
        // Planlamacı ekip üyelerini değiştiremez (yalnız görür).
        await Assert.ThrowsAsync<ForbiddenException>(() => Defs.AddTeamMembersAsync(h.Planner, team.Id, [h.Fatma.EmployeeId!.Value], false));
        await Assert.ThrowsAsync<ForbiddenException>(() => Defs.AddTeamMembersAsync(h.Ali, team.Id, [h.Fatma.EmployeeId!.Value], false));
    }

    [Fact]
    public async Task User_can_be_created_together_with_a_new_employee()
    {
        var admin = await AdminAsync();
        var id = await h.Get<UserAdminService>().SaveAsync(admin, null, new UserInput
        {
            UserName = "anil.yildirim", FullName = "Anıl Can Yıldırım", NewPassword = "Gecici1234", IsActive = true,
            Roles = [SystemRoles.Personnel], EmployeeId = UserInput.NewEmployee,
            NewEmployeeUnitId = h.Unit.Id, NewEmployeeJobRoleId = h.EngineerRole.Id, NewEmployeeNumber = "77"
        });
        h.Db.ChangeTracker.Clear();
        var e = h.Db.Employees.Single(x => x.ApplicationUserId == id);
        Assert.Equal("Anıl Can", e.FirstName);
        Assert.Equal("Yıldırım", e.LastName);
        Assert.Equal("77", e.EmployeeNumber);
        Assert.Equal(h.EngineerRole.Id, e.JobRoleId);
        Assert.True(e.IsActive);
        Assert.True((await h.Get<UserManager<ApplicationUser>>().FindByIdAsync(id))!.IsActive);
    }

    [Fact]
    public async Task New_employee_option_requires_unit()
    {
        await Assert.ThrowsAsync<DomainException>(async () => await h.Get<UserAdminService>().SaveAsync(await AdminAsync(), null, new UserInput
        {
            UserName = "eksik", FullName = "Eksik Kişi", NewPassword = "Gecici1234", Roles = [SystemRoles.Personnel],
            EmployeeId = UserInput.NewEmployee
        }));
        Assert.Null(await h.Get<UserManager<ApplicationUser>>().FindByNameAsync("eksik"));
    }
}
