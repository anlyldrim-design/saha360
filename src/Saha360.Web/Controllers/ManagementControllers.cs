using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Persistence;
using Saha360.Infrastructure.Services;
using Saha360.Web.Infrastructure;
using Saha360.Web.ViewModels;

namespace Saha360.Web.Controllers;

[Authorize(Policy = Policies.OrgView)]
public class EmployeesController(CurrentUser current, EmployeeService employees, DefinitionService definitions, WorkOrderService works,
    UserAdminService accounts, AccessService access, AppDbContext db) : Controller
{
    /// <summary>
    /// Personelle birlikte uygulama hesabı açar (kullanıcı yönetimi izni gerekir). Personel önce kaydedilir; hesap açılamazsa
    /// personel kaydı kalır ve hata personel sayfasında gösterilir.
    /// </summary>
    private async Task<string?> CreateAccountAsync(UserContext u, Employee e, bool create, string? userName, string? password, string? role)
    {
        if (!create || e.ApplicationUserId is not null) return null;
        if (!u.CanManageUsers) throw new ForbiddenException("Kullanıcı hesabı açma yetkiniz yok.");
        var r = role is not null && SystemRoles.All.Contains(role) ? role : SystemRoles.Personnel;
        try
        {
            await accounts.SaveAsync(u, null, new UserInput
            {
                UserName = userName?.Trim() ?? "", FullName = e.FullName, IsActive = true, EmployeeId = e.Id,
                Roles = [r], NewPassword = password, OrganizationUnitId = null // birim personel kaydından gelir
            });
            return null;
        }
        catch (DomainException ex) { return ex.Message; }
    }

    private static string? AccountPrecheck(bool create, string? userName, string? password) =>
        !create ? null
        : string.IsNullOrWhiteSpace(userName) ? "Hesap için kullanıcı adı girin."
        : string.IsNullOrWhiteSpace(password) ? "Hesap için ilk parolayı girin."
        : null;

    [HttpGet("/personel")]
    public async Task<IActionResult> Index(string? q, int? unitId, int? teamId, int? roleId, string? durum, int page = 1)
    {
        var u = await current.GetAsync();
        bool? active = durum switch { "aktif" => true, "pasif" => false, _ => null };
        ViewData["Lookups"] = await definitions.LookupsAsync(u);
        ViewData["CanManage"] = u.CanManagePersonnel;
        return View(await employees.ListAsync(u, q, teamId, roleId, active, page, unitId));
    }

    /// <summary>Organizasyon şeması: birim → ekip (şef) → personel. Yalnız kapsamdaki birimler.</summary>
    [HttpGet("/organizasyon")]
    public async Task<IActionResult> OrgChart(int? unitId, bool pasif = false)
    {
        var u = await current.GetAsync();
        ViewData["Lookups"] = await definitions.LookupsAsync(u);
        ViewData["UnitId"] = unitId;
        ViewData["Me"] = u;
        return View("OrgChart", await definitions.OrgChartAsync(u, unitId, pasif));
    }

    [HttpGet("/personel/yeni"), Authorize(Policy = Permissions.PersonnelManage)]
    public async Task<IActionResult> Create()
    {
        var u = await current.GetAsync();
        return View("Form", await FormVm(u, null, new EmployeeInput { OrganizationUnitId = u.UnitId ?? 0 }));
    }

    [HttpPost("/personel/yeni"), Authorize(Policy = Permissions.PersonnelManage)]
    public async Task<IActionResult> Create(EmployeeInput input, bool createAccount, string? accountUserName, string? accountPassword, string? accountRole)
    {
        var u = await current.GetAsync();
        var account = new AccountRequest(createAccount, accountUserName, accountRole ?? SystemRoles.Personnel);
        if (AccountPrecheck(createAccount, accountUserName, accountPassword) is string pre)
        {
            ModelState.AddModelError("", pre);
            return View("Form", await FormVm(u, null, input, account));
        }
        try
        {
            var e = await employees.SaveAsync(u, null, input);
            var accountError = await CreateAccountAsync(u, e, createAccount, accountUserName, accountPassword, accountRole);
            if (accountError is not null) TempData["Error"] = $"{e.FullName} eklendi, ancak hesap açılamadı: {accountError} Aşağıdan tekrar deneyin.";
            else TempData["Ok"] = createAccount ? $"{e.FullName} eklendi ve uygulama hesabı açıldı ({accountUserName})." : $"{e.FullName} eklendi.";
            return Redirect($"/personel/{e.Id}");
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View("Form", await FormVm(u, null, input, account));
        }
    }

    [HttpGet("/personel/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var u = await current.GetAsync();
        var e = await employees.GetAsync(u, id);
        var input = new EmployeeInput
        {
            EmployeeNumber = u.CanSeeSensitive ? e.EmployeeNumber : null, FirstName = e.FirstName, LastName = e.LastName, IsActive = e.IsActive,
            OrganizationUnitId = e.OrganizationUnitId, TeamId = e.TeamId, JobRoleId = e.JobRoleId, SupervisorEmployeeId = e.SupervisorEmployeeId,
            Phone = u.CanSeeSensitive ? e.Phone : null
        };
        return View("Form", await FormVm(u, e, input));
    }

    [HttpPost("/personel/{id:int}"), Authorize(Policy = Permissions.PersonnelManage)]
    public async Task<IActionResult> Edit(int id, EmployeeInput input, bool createAccount, string? accountUserName, string? accountPassword,
        string? accountRole)
    {
        var u = await current.GetAsync();
        var account = new AccountRequest(createAccount, accountUserName, accountRole ?? SystemRoles.Personnel);
        if (AccountPrecheck(createAccount, accountUserName, accountPassword) is string pre)
        {
            ModelState.AddModelError("", pre);
            return View("Form", await FormVm(u, await employees.GetAsync(u, id), input, account));
        }
        try
        {
            var e = await employees.SaveAsync(u, id, input);
            var accountError = await CreateAccountAsync(u, e, createAccount, accountUserName, accountPassword, accountRole);
            if (accountError is not null) TempData["Error"] = $"Personel güncellendi, ancak hesap açılamadı: {accountError}";
            else TempData["Ok"] = createAccount ? $"Personel güncellendi ve uygulama hesabı açıldı ({accountUserName})." : "Personel kaydı güncellendi.";
            return Redirect($"/personel/{id}");
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View("Form", await FormVm(u, await employees.GetAsync(u, id), input, account));
        }
    }

    [HttpPost("/personel/{id:int}/izin"), Authorize(Policy = Permissions.LeaveManage)]
    public async Task<IActionResult> AddLeave(int id, AvailabilityType type, DateTime? start, DateTime? end, string? note)
    {
        var u = await current.GetAsync();
        await employees.AddAvailabilityAsync(u, id, type, start, end, note);
        TempData["Ok"] = "İzin / müsaitlik kaydı eklendi.";
        return Redirect($"/personel/{id}#izinler");
    }

    [HttpPost("/personel/{id:int}/izin/{aid:int}/sil"), Authorize(Policy = Permissions.LeaveManage)]
    public async Task<IActionResult> RemoveLeave(int id, int aid)
    {
        var u = await current.GetAsync();
        await employees.RemoveAvailabilityAsync(u, id, aid);
        TempData["Ok"] = "İzin kaydı kaldırıldı.";
        return Redirect($"/personel/{id}#izinler");
    }

    /// <summary>Şu an ve ileride izinli / müsait olmayan personel.</summary>
    [HttpGet("/personel/izinler")]
    public async Task<IActionResult> Leaves()
    {
        var u = await current.GetAsync();
        var now = DateTime.UtcNow;
        var all = u.AllUnits;
        var unit = u.UnitId ?? -1;
        ViewData["CanManage"] = u.CanManageLeave;
        var list = await db.EmployeeAvailabilities.AsNoTracking().Include(a => a.Employee)
            .Where(a => a.EndAtUtc > now && (all || a.Employee.OrganizationUnitId == unit))
            .OrderBy(a => a.StartAtUtc).Take(500).ToListAsync();
        return View(list);
    }

    private async Task<EmployeeFormViewModel> FormVm(UserContext u, Employee? e, EmployeeInput input, AccountRequest? accountRequest = null)
    {
        (string? Name, bool Active)? account = null;
        List<WorkListItem> recent = [];
        if (e is not null)
        {
            if (e.ApplicationUserId is not null)
            {
                var acc = await db.Users.Where(x => x.Id == e.ApplicationUserId).Select(x => new { x.UserName, x.IsActive }).FirstOrDefaultAsync();
                if (acc is not null) account = (acc.UserName, acc.IsActive);
            }
            recent = (await works.ListAsync(u, new WorkListQuery { EmployeeId = e.Id, PageSize = 10, Sort = "baslangic_desc" })).Items;
        }
        var lookups = await definitions.LookupsAsync(u);
        return new EmployeeFormViewModel
        {
            Id = e?.Id,
            Input = input,
            Lookups = lookups,
            Supervisors = await access.ScopedEmployees(u).AsNoTracking().Where(x => x.IsActive && x.Id != (e == null ? 0 : e.Id))
                .OrderBy(x => x.FirstName).Select(x => new Option(x.Id, x.FirstName + " " + x.LastName)).ToListAsync(),
            Availabilities = e?.Availabilities ?? [],
            AccountUserName = account?.Name,
            AccountUserId = e?.ApplicationUserId,
            AccountIsActive = account?.Active ?? false,
            CanManageAccounts = u.CanManageUsers,
            CanEdit = e is null ? u.CanManagePersonnel : u.CanManagePersonnel && u.InScope(e.OrganizationUnitId),
            CanManageLeave = e is not null && u.CanManageLeave && u.InScope(e.OrganizationUnitId),
            Account = accountRequest ?? new AccountRequest(),
            RecentWorks = recent
        };
    }
}

/// <summary>Tanımlar: birim, görev rolü, kategori, bölge ve ekipler. Görüntüleme planlamacıya açık, değişiklik yöneticiye.</summary>
public class DefinitionsController(CurrentUser current, DefinitionService definitions, AccessService access, AppDbContext db) : Controller
{
    [HttpGet("/tanimlar"), Authorize(Policy = Policies.DefinitionsView)]
    public async Task<IActionResult> Index(string tab = "birimler")
    {
        ViewData["Tab"] = tab;
        ViewData["Units"] = await definitions.ListAsync<OrganizationUnit>();
        // Unvanlar kendi sırasıyla listelenir; ↑ ↓ düğmeleri bu sırayı değiştirir.
        ViewData["Roles"] = await db.JobRoles.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync();
        ViewData["Categories"] = await definitions.ListAsync<WorkCategory>();
        ViewData["Locations"] = await definitions.ListAsync<WorkLocation>();
        var u = await current.GetAsync();
        ViewData["CanEdit"] = u.CanManageDefinitions;
        return View();
    }

    [HttpPost("/tanimlar/{type}"), Authorize(Policy = Permissions.DefinitionsManage)]
    public async Task<IActionResult> Save(string type, int? id, string? name, bool isActive, string? code, bool isLeadRole, bool isOpenToAll)
    {
        var u = await current.GetAsync();
        var tab = type;
        switch (type)
        {
            case "birimler": await definitions.SaveAsync<OrganizationUnit>(u, id, name, isActive, x => x.Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim()); break;
            case "roller":
                // Sıra elle girilmez: yeni unvan listenin sonuna eklenir, mevcut olanın sırası korunur.
                var next = id is null ? await definitions.NextRoleOrderAsync() : 0;
                await definitions.SaveAsync<JobRole>(u, id, name, isActive, x =>
                {
                    x.IsLeadRole = isLeadRole;
                    x.IsOpenToAll = isOpenToAll;
                    if (id is null) x.SortOrder = next;
                });
                break;
            case "kategoriler": await definitions.SaveAsync<WorkCategory>(u, id, name, isActive); break;
            case "bolgeler": await definitions.SaveAsync<WorkLocation>(u, id, name, isActive); break;
            default: return NotFound();
        }
        TempData["Ok"] = id is null ? "Kayıt eklendi." : "Kayıt güncellendi.";
        return Redirect($"/tanimlar?tab={tab}");
    }

    [HttpPost("/tanimlar/roller/{id:int}/tasi"), Authorize(Policy = Permissions.DefinitionsManage)]
    public async Task<IActionResult> MoveRole(int id, string yon)
    {
        await definitions.MoveRoleAsync(await current.GetAsync(), id, up: yon == "yukari");
        return Redirect("/tanimlar?tab=roller");
    }

    [HttpGet("/tanimlar/ekipler"), Authorize(Policy = Policies.OrgView)]
    public async Task<IActionResult> Teams()
    {
        var u = await current.GetAsync();
        ViewData["Lookups"] = await definitions.LookupsAsync(u);
        ViewData["CanEdit"] = u.CanManageTeams;
        ViewData["Members"] = await db.Employees.AsNoTracking().Where(e => e.IsActive && e.TeamId != null)
            .GroupBy(e => e.TeamId!.Value).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C);
        ViewData["AllEmployees"] = await access.ScopedEmployees(u).AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FirstName)
            .Select(e => new Option(e.Id, e.FirstName + " " + e.LastName)).ToListAsync();
        return View(await definitions.TeamsAsync(u));
    }

    [HttpPost("/tanimlar/ekipler"), Authorize(Policy = Permissions.TeamsManage)]
    public async Task<IActionResult> SaveTeam(int? id, string? name, int unitId, int? supervisorId, string? description, bool isActive)
    {
        var u = await current.GetAsync();
        var t = await definitions.SaveTeamAsync(u, id, name, unitId, supervisorId, description, isActive);
        TempData["Ok"] = id is null ? "Ekip eklendi. Şimdi üyelerini ekleyin." : "Ekip güncellendi.";
        // Yeni ekipten sonra doğrudan ekip sayfasına: üyeler hemen eklenebilsin.
        return Redirect($"/tanimlar/ekipler/{t.Id}");
    }

    /// <summary>Ekip sayfası: ekip bilgileri, şef, üyeler ve toplu üye ekleme.</summary>
    [HttpGet("/tanimlar/ekipler/{id:int}"), Authorize(Policy = Policies.OrgView)]
    public async Task<IActionResult> Team(int id)
    {
        var u = await current.GetAsync();
        ViewData["CanEdit"] = u.CanManageTeams;
        ViewData["Lookups"] = await definitions.LookupsAsync(u);
        ViewData["AllEmployees"] = await access.ScopedEmployees(u).AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FirstName)
            .Select(e => new Option(e.Id, e.FirstName + " " + e.LastName)).ToListAsync();
        return View(await definitions.TeamDetailAsync(u, id));
    }

    [HttpPost("/tanimlar/ekipler/{id:int}/uyeler"), Authorize(Policy = Permissions.TeamsManage)]
    public async Task<IActionResult> AddMembers(int id, int[]? employeeIds, bool setSupervisor)
    {
        var n = await definitions.AddTeamMembersAsync(await current.GetAsync(), id, employeeIds ?? [], setSupervisor);
        TempData["Ok"] = $"{n} personel ekibe eklendi.";
        return Redirect($"/tanimlar/ekipler/{id}");
    }

    [HttpPost("/tanimlar/ekipler/{id:int}/uyeler/{employeeId:int}/cikar"), Authorize(Policy = Permissions.TeamsManage)]
    public async Task<IActionResult> RemoveMember(int id, int employeeId)
    {
        await definitions.RemoveTeamMemberAsync(await current.GetAsync(), id, employeeId);
        TempData["Ok"] = "Personel ekipten çıkarıldı.";
        return Redirect($"/tanimlar/ekipler/{id}");
    }
}

[Authorize(Policy = Policies.MaterialsView)]
public class MaterialsController(CurrentUser current, MaterialService materials, AccessService access) : Controller
{
    [HttpGet("/malzemeler")]
    public async Task<IActionResult> Index(string? q, bool pasif = false)
    {
        ViewData["Q"] = q;
        ViewData["CanEdit"] = (await current.GetAsync()).CanManageMaterials;
        return View(await materials.ListAsync(q, pasif || !string.IsNullOrWhiteSpace(q)));
    }

    [HttpPost("/malzemeler"), Authorize(Policy = Permissions.MaterialsManage)]
    public async Task<IActionResult> Save(int? id, string? code, string? name, string? category, string? unit, string? description, bool isActive)
    {
        var u = await current.GetAsync();
        await materials.SaveAsync(u, id, code, name, category, unit, description, isActive);
        TempData["Ok"] = id is null ? "Malzeme kataloğa eklendi." : "Malzeme güncellendi.";
        return Redirect("/malzemeler");
    }

    [HttpGet("/malzemeler/kullanim")]
    public async Task<IActionResult> Usage(string? q)
    {
        var u = await current.GetAsync();
        ViewData["Q"] = q;
        return View(await materials.UsageAsync(access.VisibleWorks(u), q));
    }
}

[Authorize(Policy = Permissions.ReportsView)]
public class ReportsController(CurrentUser current, ReportService reports, DefinitionService definitions, EmployeeService employees) : Controller
{
    [HttpGet("/raporlar")]
    public async Task<IActionResult> Index([FromQuery] ReportQuery query)
    {
        var u = await current.GetAsync();
        ViewData["Query"] = query;
        ViewData["Lookups"] = await definitions.LookupsAsync(u);
        ViewData["Employees"] = await employees.AllActiveAsync(u);
        return View(await reports.RunAsync(u, query));
    }

    [HttpGet("/raporlar/csv"), Authorize(Policy = Permissions.ReportsExport)]
    public async Task<IActionResult> Csv([FromQuery] ReportQuery query)
    {
        var u = await current.GetAsync();
        var t = await reports.RunAsync(u, query);
        return File(ReportService.ToCsv(t), "text/csv; charset=utf-8", $"rapor-{t.Definition.Key}-{t.FromLocal:yyyyMMdd}-{t.ToLocal:yyyyMMdd}.csv");
    }
}

[Authorize(Policy = Permissions.UsersManage)]
public class UsersController(CurrentUser current, UserAdminService admin, AppDbContext db) : Controller
{
    [HttpGet("/kullanicilar")]
    public async Task<IActionResult> Index() => View(await admin.ListAsync());

    [HttpGet("/kullanicilar/yeni")]
    public async Task<IActionResult> Create() => View("Form", await FormVm(null, new UserInput()));

    [HttpPost("/kullanicilar/yeni")]
    public async Task<IActionResult> Create(UserInput input)
    {
        var u = await current.GetAsync();
        try
        {
            var id = await admin.SaveAsync(u, null, input);
            TempData["Ok"] = "Kullanıcı oluşturuldu.";
            return Redirect($"/kullanicilar/{id}");
        }
        catch (DomainException e)
        {
            ModelState.AddModelError("", e.Message);
            return View("Form", await FormVm(null, input));
        }
    }

    [HttpGet("/kullanicilar/{id}")]
    public async Task<IActionResult> Edit(string id) => View("Form", await FormVm(id, await admin.GetAsync(id)));

    [HttpPost("/kullanicilar/{id}")]
    public async Task<IActionResult> Edit(string id, UserInput input)
    {
        var u = await current.GetAsync();
        try
        {
            await admin.SaveAsync(u, id, input);
            TempData["Ok"] = "Kullanıcı güncellendi.";
            return Redirect($"/kullanicilar/{id}");
        }
        catch (DomainException e)
        {
            ModelState.AddModelError("", e.Message);
            return View("Form", await FormVm(id, input));
        }
    }

    private async Task<UserFormViewModel> FormVm(string? id, UserInput input) => new()
    {
        Id = id,
        Input = input,
        Units = await db.OrganizationUnits.AsNoTracking().OrderBy(x => x.Name).Select(x => new Option(x.Id, x.Name)).ToListAsync(),
        // Yalnız hesabı olmayan veya bu hesaba bağlı personel seçilebilir.
        Employees = await db.Employees.AsNoTracking().Where(e => e.ApplicationUserId == null || e.ApplicationUserId == id)
            .OrderBy(e => e.FirstName).Select(e => new Option(e.Id, e.FirstName + " " + e.LastName + (e.EmployeeNumber != null ? " · " + e.EmployeeNumber : "")))
            .ToListAsync(),
        JobRoles = await db.JobRoles.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.SortOrder).Select(r => new Option(r.Id, r.Name)).ToListAsync()
    };
}
