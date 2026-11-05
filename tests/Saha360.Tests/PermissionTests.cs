using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Yetki tablosu, birim kapsamı ve yetki yükseltme koruması.</summary>
public class PermissionTests : IDisposable
{
    private readonly TestHost h = new();
    public void Dispose() => h.Dispose();

    private EmployeeService Employees => h.Get<EmployeeService>();
    private EmployeeInput NewPerson(int unitId) => new() { FirstName = "Yeni", LastName = "Kişi", OrganizationUnitId = unitId, IsActive = true };
    private int OtherUnitId => h.Db.OrganizationUnits.Single(u => u.Name == "Diğer Birim").Id;

    [Fact]
    public void Default_table_gives_personnel_and_leave_only_to_unit_manager()
    {
        Assert.True(h.Manager.CanManagePersonnel);
        Assert.True(h.Manager.CanManageLeave);
        Assert.True(h.Manager.CanManageTeams);
        Assert.False(h.Planner.CanManagePersonnel);
        Assert.False(h.Planner.CanManageLeave);
        Assert.False(h.Planner.CanManageTeams);
        Assert.True(h.Planner.CanViewPersonnel);
        Assert.True(h.Planner.CanCreateWork);
        Assert.False(h.Ali.CanSeeOrganization);
        Assert.False(h.Manager.CanManageUsers);
        Assert.False(h.Manager.AllUnits);
        var admin = new UserContext("x", "Yönetici", new HashSet<string> { SystemRoles.SystemAdmin }, null, null);
        Assert.True(Permissions.Keys.All(admin.Has));
        Assert.True(admin.AllUnits);
    }

    [Fact]
    public async Task Planner_cannot_add_personnel_or_leave()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Employees.SaveAsync(h.Planner, null, NewPerson(h.Unit.Id)));
        await Assert.ThrowsAsync<ForbiddenException>(() => Employees.AddAvailabilityAsync(h.Planner, h.Ali.EmployeeId!.Value,
            AvailabilityType.Leave, TrTime.NowLocal, TrTime.NowLocal.AddHours(8), null));
        await Employees.SaveAsync(h.Manager, null, NewPerson(h.Unit.Id));
        await Employees.AddAvailabilityAsync(h.Manager, h.Ali.EmployeeId!.Value, AvailabilityType.Leave, TrTime.NowLocal, TrTime.NowLocal.AddHours(8), null);
    }

    [Fact]
    public async Task Unit_manager_works_only_in_own_unit()
    {
        await Assert.ThrowsAsync<DomainException>(() => Employees.SaveAsync(h.Manager, null, NewPerson(OtherUnitId)));
        await Assert.ThrowsAsync<NotFoundException>(() => Employees.AddAvailabilityAsync(h.Manager, h.Outsider.EmployeeId!.Value,
            AvailabilityType.Leave, TrTime.NowLocal, TrTime.NowLocal.AddHours(8), null));
        var list = await Employees.ListAsync(h.Manager, null, null, null, null, 1);
        Assert.DoesNotContain(list.Items, e => e.FullName.Contains("Yabancı"));
        var chart = await h.Get<DefinitionService>().OrgChartAsync(h.Manager, null, false);
        Assert.Equal([h.Unit.Id], chart.Select(c => c.Id));
    }

    [Fact]
    public async Task Manager_without_unit_sees_nothing_until_all_units_is_given()
    {
        var noUnit = await h.UserAsync("kapsamsiz", SystemRoles.UnitManager, null);
        Assert.Equal(0, await h.Get<AccessService>().ScopedEmployees(noUnit).CountAsync());
        Assert.Equal(0, await h.Get<AccessService>().VisibleWorks(noUnit).CountAsync(w => true));
        await h.CreateAsync();
        Assert.Equal(0, await h.Get<AccessService>().VisibleWorks(noUnit).CountAsync());

        var wide = noUnit with { AllUnitsGranted = true };
        Assert.True(await h.Get<AccessService>().ScopedEmployees(wide).AnyAsync(e => e.Id == h.Outsider.EmployeeId));
        Assert.Equal(1, await h.Get<AccessService>().VisibleWorks(wide).CountAsync());
    }

    [Fact]
    public async Task Granted_permissions_replace_role_defaults()
    {
        var planner = h.Planner with { Granted = new HashSet<string> { Permissions.PersonnelManage } };
        Assert.True(planner.CanManagePersonnel);
        Assert.False(planner.CanCreateWork);
        await Employees.SaveAsync(planner, null, NewPerson(h.Unit.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Get<WorkOrderService>().CreateAsync(planner, h.Input("İzinsiz iş")));
    }

    [Fact]
    public async Task Permission_table_is_saved_only_by_admin_and_audited()
    {
        var perms = h.Get<PermissionService>();
        await perms.EnsureDefaultsAsync();
        Assert.DoesNotContain(Permissions.PersonnelManage, await perms.ForRolesAsync([SystemRoles.Planner]));

        await Assert.ThrowsAsync<ForbiddenException>(() => perms.SaveAsync(h.Manager, [$"{SystemRoles.Planner}|{Permissions.PersonnelManage}"]));

        var admin = await h.UserAsync("yonetici", SystemRoles.SystemAdmin, null);
        var keep = (await perms.MatrixAsync()).SelectMany(kv => kv.Value.Select(p => kv.Key + "|" + p)).ToList();
        await perms.SaveAsync(admin, [.. keep, $"{SystemRoles.Planner}|{Permissions.PersonnelManage}"]);
        Assert.Contains(Permissions.PersonnelManage, await perms.ForRolesAsync([SystemRoles.Planner]));
        Assert.Contains(Permissions.WorksCreate, await perms.ForRolesAsync([SystemRoles.Planner]));   // dokunulmayan izin korunur
        Assert.True(h.Db.AuditLogs.Any(a => a.Action == "PermissionsChanged"));

        // Sistem yöneticisi her zaman her şeye sahip; tablodan kaldırılamaz.
        Assert.Equal(Permissions.Keys.Length, (await perms.ForRolesAsync([SystemRoles.SystemAdmin])).Count);
    }

    [Fact]
    public async Task User_manager_cannot_escalate()
    {
        var hr = h.Manager with { Granted = new HashSet<string> { Permissions.UsersManage } };
        var users = h.Get<UserAdminService>();
        await Assert.ThrowsAsync<ForbiddenException>(() => users.SaveAsync(hr, null, new UserInput
        {
            UserName = "kotu", FullName = "Kötü Niyet", NewPassword = "Parola1234", IsActive = true, Roles = [SystemRoles.SystemAdmin]
        }));
        await Assert.ThrowsAsync<ForbiddenException>(() => users.SaveAsync(hr, null, new UserInput
        {
            UserName = "genis", FullName = "Geniş Kapsam", NewPassword = "Parola1234", IsActive = true, Roles = [SystemRoles.Planner], AllUnits = true
        }));
        await Assert.ThrowsAsync<ForbiddenException>(() => users.SaveAsync(hr, null, new UserInput
        {
            UserName = "baska", FullName = "Başka Birim", NewPassword = "Parola1234", IsActive = true, Roles = [SystemRoles.Planner], OrganizationUnitId = OtherUnitId
        }));
        await Assert.ThrowsAsync<ForbiddenException>(() => users.SaveAsync(hr, null, new UserInput
        {
            UserName = "yabanci2", FullName = "Yabancı Bağ", NewPassword = "Parola1234", IsActive = true, Roles = [SystemRoles.Planner], EmployeeId = h.Outsider.EmployeeId
        }));
        await users.SaveAsync(hr, null, new UserInput
        {
            UserName = "normal", FullName = "Normal Kullanıcı", NewPassword = "Parola1234", IsActive = true, Roles = [SystemRoles.Planner], OrganizationUnitId = h.Unit.Id
        });
    }
}
