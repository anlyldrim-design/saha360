using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

public record Option(int Id, string Name);
public record Lookups(List<Option> Units, List<Option> Teams, List<Option> JobRoles, List<Option> Categories, List<Option> Locations,
    List<Option> Supervisors, List<(string Id, string Name)> Managers);

/// <summary>Birim, ekip, görev rolü, kategori ve bölge tanımları. Tanımlar silinmez; pasife alınır.</summary>
public class DefinitionService(AppDbContext db, AuditService audit, AccessService access)
{
    public Task<List<T>> ListAsync<T>() where T : DefinitionEntity =>
        db.Set<T>().AsNoTracking().OrderByDescending(x => x.IsActive).ThenBy(x => x.Name).ToListAsync();

    public async Task<T> SaveAsync<T>(UserContext u, int? id, string? name, bool isActive, Action<T>? apply = null) where T : DefinitionEntity, new()
    {
        if (!u.CanManageDefinitions) throw new ForbiddenException("Tanımları yalnız yöneticiler değiştirebilir.");
        var n = name?.Trim() ?? "";
        if (n.Length is < 2 or > 150) throw new DomainException("Ad 2–150 karakter olmalı.");
        if (await db.Set<T>().AnyAsync(x => x.Name == n && x.Id != (id ?? 0)))
            throw new DomainException($"\"{n}\" adıyla bir kayıt zaten var.");

        T entity;
        if (id is int existing)
            entity = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == existing) ?? throw new NotFoundException();
        else
        {
            entity = new T();
            db.Set<T>().Add(entity);
        }
        entity.Name = n;
        entity.IsActive = isActive;
        apply?.Invoke(entity);
        await db.SaveChangesAsync();
        audit.Add(u, id is null ? "DefinitionCreated" : "DefinitionUpdated", typeof(T).Name, entity.Id,
            $"{typeof(T).Name} {(id is null ? "eklendi" : "güncellendi")}: {n}{(isActive ? "" : " (pasif)")}");
        await db.SaveChangesAsync();
        return entity;
    }

    /// <summary>Yeni unvanın sırası: listenin sonu (kullanıcı sıra yazmaz).</summary>
    public async Task<int> NextRoleOrderAsync() => (await db.JobRoles.MaxAsync(r => (int?)r.SortOrder) ?? 0) + 1;

    /// <summary>Unvanı bir yukarı/aşağı taşır; sıralar 1, 2, 3… olarak yeniden numaralanır.</summary>
    public async Task MoveRoleAsync(UserContext u, int id, bool up)
    {
        if (!u.CanManageDefinitions) throw new ForbiddenException("Tanımları yalnız yöneticiler değiştirebilir.");
        var list = await db.JobRoles.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync();
        var i = list.FindIndex(r => r.Id == id);
        if (i < 0) throw new NotFoundException();
        var j = up ? i - 1 : i + 1;
        if (j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        for (var k = 0; k < list.Count; k++) list[k].SortOrder = k + 1;
        audit.Add(u, "DefinitionUpdated", nameof(JobRole), id, $"Unvan sırası değişti: {list[j].Name} {(up ? "yukarı" : "aşağı")} taşındı.");
        await db.SaveChangesAsync();
    }

    public async Task<Team> SaveTeamAsync(UserContext u, int? id, string? name, int unitId, int? supervisorId, string? description, bool isActive)
    {
        if (!await db.OrganizationUnits.AnyAsync(x => x.Id == unitId)) throw new DomainException("Geçerli bir birim seçin.");
        if (supervisorId is int s && !await db.Employees.AnyAsync(e => e.Id == s && e.IsActive)) throw new DomainException("Seçilen şef geçersiz.");
        var n = name?.Trim() ?? "";
        if (await db.Teams.AnyAsync(t => t.OrganizationUnitId == unitId && t.Name == n && t.Id != (id ?? 0)))
            throw new DomainException("Bu birimde aynı adla bir ekip zaten var.");
        if (!u.CanManageTeams) throw new ForbiddenException("Ekipleri yönetme yetkiniz yok.");
        if (!u.InScope(unitId)) throw new ForbiddenException("Bu birim kapsamınız dışında.");
        if (n.Length is < 2 or > 150) throw new DomainException("Ekip adı 2–150 karakter olmalı.");

        Team t;
        if (id is int existing) t = await access.ScopedTeams(u).FirstOrDefaultAsync(x => x.Id == existing) ?? throw new NotFoundException();
        else { t = new Team(); db.Teams.Add(t); }
        var oldSupervisor = t.SupervisorEmployeeId;
        t.Name = n;
        t.OrganizationUnitId = unitId;
        t.SupervisorEmployeeId = supervisorId;
        t.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        t.IsActive = isActive;
        await db.SaveChangesAsync();

        if (supervisorId is int sup && sup != oldSupervisor)
        {
            // Şef ekibin üyesi olur; eski şefe bağlı (veya şefsiz) üyelerin bağlı şefi yeni şef olur.
            var members = await db.Employees.Where(e => e.TeamId == t.Id || e.Id == sup).ToListAsync();
            foreach (var e in members)
            {
                if (e.Id == sup) { e.TeamId ??= t.Id; continue; }
                if (e.SupervisorEmployeeId is null || e.SupervisorEmployeeId == oldSupervisor) e.SupervisorEmployeeId = sup;
            }
        }
        audit.Add(u, id is null ? "TeamCreated" : "TeamUpdated", "Team", t.Id, $"Ekip {(id is null ? "eklendi" : "güncellendi")}: {n}");
        await db.SaveChangesAsync();
        return t;
    }

    public Task<List<Team>> TeamsAsync(UserContext u) =>
        access.ScopedTeams(u).AsNoTracking().Include(t => t.OrganizationUnit).Include(t => t.SupervisorEmployee)
            .OrderByDescending(t => t.IsActive).ThenBy(t => t.Name).ToListAsync();

    public record TeamMember(int Id, string Name, string? EmployeeNumber, string? Title, string? Supervisor, bool IsActive, bool IsTeamSupervisor);
    public record TeamCandidate(int Id, string Name, string? EmployeeNumber, string? Title, string? CurrentTeam, string Unit);

    public class TeamDetail
    {
        public required Team Team { get; init; }
        public List<TeamMember> Members { get; init; } = [];
        public List<TeamCandidate> Candidates { get; init; } = [];
    }

    /// <summary>Ekip sayfası: üyeler ve eklenebilecek personel (önce ekibin birimi).</summary>
    public async Task<TeamDetail> TeamDetailAsync(UserContext u, int teamId)
    {
        var team = await access.ScopedTeams(u).AsNoTracking().Include(t => t.OrganizationUnit).Include(t => t.SupervisorEmployee)
            .FirstOrDefaultAsync(t => t.Id == teamId) ?? throw new NotFoundException("Ekip bulunamadı.");
        var scope = access.ScopedEmployees(u).AsNoTracking();
        var members = await scope.Where(e => e.TeamId == teamId)
            .OrderByDescending(e => e.Id == team.SupervisorEmployeeId).ThenBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new TeamMember(e.Id, e.FirstName + " " + e.LastName, e.EmployeeNumber, e.JobRole != null ? e.JobRole.Name : null,
                e.SupervisorEmployee != null ? e.SupervisorEmployee.FirstName + " " + e.SupervisorEmployee.LastName : null, e.IsActive,
                e.Id == team.SupervisorEmployeeId))
            .ToListAsync();
        var candidates = await scope.Where(e => e.IsActive && e.TeamId != teamId)
            .OrderByDescending(e => e.OrganizationUnitId == team.OrganizationUnitId).ThenBy(e => e.TeamId != null).ThenBy(e => e.FirstName)
            .Select(e => new TeamCandidate(e.Id, e.FirstName + " " + e.LastName, e.EmployeeNumber, e.JobRole != null ? e.JobRole.Name : null,
                e.Team != null ? e.Team.Name : null, e.OrganizationUnit.Name))
            .Take(1000).ToListAsync();
        return new TeamDetail { Team = team, Members = members, Candidates = candidates };
    }

    /// <summary>
    /// Seçilen personeli ekibe alır (başka ekipteyse taşınır). İstenirse bağlı şefleri ekip şefi yapılır.
    /// "Ekip yönetimi" izni ve birim kapsamı gerekir.
    /// </summary>
    public async Task<int> AddTeamMembersAsync(UserContext u, int teamId, IReadOnlyCollection<int> employeeIds, bool setSupervisor)
    {
        if (!u.CanManageTeams) throw new ForbiddenException("Ekip üyelerini düzenleme yetkiniz yok.");
        var team = await access.ScopedTeams(u).FirstOrDefaultAsync(t => t.Id == teamId) ?? throw new NotFoundException("Ekip bulunamadı.");
        if (employeeIds.Count == 0) throw new DomainException("Eklenecek personeli işaretleyin.");
        var list = await access.ScopedEmployees(u).Where(e => employeeIds.Contains(e.Id)).ToListAsync();
        if (list.Count != employeeIds.Distinct().Count()) throw new DomainException("Seçilen personelden biri bulunamadı veya kapsamınız dışında.");
        foreach (var e in list)
        {
            e.TeamId = team.Id;
            if (setSupervisor && team.SupervisorEmployeeId is int sup && sup != e.Id) e.SupervisorEmployeeId = sup;
            e.UpdatedAtUtc = DateTime.UtcNow;
        }
        audit.Add(u, "TeamMembersAdded", "Team", team.Id, $"{team.Name} ekibine eklendi: {string.Join(", ", list.Select(e => e.FullName))}");
        await db.SaveChangesAsync();
        return list.Count;
    }

    public async Task RemoveTeamMemberAsync(UserContext u, int teamId, int employeeId)
    {
        if (!u.CanManageTeams) throw new ForbiddenException("Ekip üyelerini düzenleme yetkiniz yok.");
        var team = await access.ScopedTeams(u).FirstOrDefaultAsync(t => t.Id == teamId) ?? throw new NotFoundException("Ekip bulunamadı.");
        var e = await access.ScopedEmployees(u).FirstOrDefaultAsync(x => x.Id == employeeId && x.TeamId == teamId)
                ?? throw new NotFoundException("Bu personel ekipte değil.");
        if (team.SupervisorEmployeeId == e.Id) throw new DomainException("Ekip şefi ekipten çıkarılamaz; önce ekibe başka bir şef seçin.");
        e.TeamId = null;
        if (e.SupervisorEmployeeId == team.SupervisorEmployeeId) e.SupervisorEmployeeId = null;
        e.UpdatedAtUtc = DateTime.UtcNow;
        audit.Add(u, "TeamMemberRemoved", "Team", team.Id, $"{team.Name} ekibinden çıkarıldı: {e.FullName}");
        await db.SaveChangesAsync();
    }

    // ───────── Organizasyon şeması: birim → ekip (şef) → personel ─────────

    public record OrgPerson(int Id, string Name, string? Title, bool IsActive, bool HasAccount, bool OnLeaveNow);
    public record OrgTeam(int Id, string Name, bool IsActive, string? SupervisorName, List<OrgPerson> Members);
    public record OrgUnit(int Id, string Name, string? Code, bool IsActive, List<OrgTeam> Teams, List<OrgPerson> Unassigned)
    {
        public int PersonCount => Teams.Sum(t => t.Members.Count) + Unassigned.Count;
    }

    /// <summary>Kullanıcının kapsamındaki birimlerin şeması (yalnız kapsam; başka birimin personeli görünmez).</summary>
    public async Task<List<OrgUnit>> OrgChartAsync(UserContext u, int? unitId, bool includeInactive)
    {
        var units = await access.ScopedUnits(u).AsNoTracking().Where(x => unitId == null || x.Id == unitId)
            .OrderBy(x => x.Name).ToListAsync();
        var ids = units.Select(x => x.Id).ToList();
        var teams = await db.Teams.AsNoTracking().Include(t => t.SupervisorEmployee).Where(t => ids.Contains(t.OrganizationUnitId))
            .OrderBy(t => t.Name).ToListAsync();
        var now = DateTime.UtcNow;
        var people = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.OrganizationUnitId) && (includeInactive || e.IsActive))
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new
            {
                e.Id, e.OrganizationUnitId, e.TeamId, Name = e.FirstName + " " + e.LastName, Title = e.JobRole != null ? e.JobRole.Name : null,
                e.IsActive, HasAccount = e.ApplicationUserId != null,
                OnLeave = e.Availabilities.Any(a => a.StartAtUtc <= now && a.EndAtUtc > now)
            }).ToListAsync();
        return units.Select(unit => new OrgUnit(unit.Id, unit.Name, unit.Code, unit.IsActive,
            teams.Where(t => t.OrganizationUnitId == unit.Id)
                .Select(t => new OrgTeam(t.Id, t.Name, t.IsActive, t.SupervisorEmployee?.FullName,
                    people.Where(p => p.TeamId == t.Id).OrderByDescending(p => p.Id == t.SupervisorEmployeeId)
                        .Select(p => new OrgPerson(p.Id, p.Name, p.Title, p.IsActive, p.HasAccount, p.OnLeave)).ToList()))
                .ToList(),
            people.Where(p => p.OrganizationUnitId == unit.Id && (p.TeamId == null || teams.All(t => t.Id != p.TeamId)))
                .Select(p => new OrgPerson(p.Id, p.Name, p.Title, p.IsActive, p.HasAccount, p.OnLeave)).ToList()))
            .ToList();
    }

    /// <summary>Formlardaki açılır listeler (yalnız aktif kayıtlar).</summary>
    public async Task<Lookups> LookupsAsync(UserContext u)
    {
        var units = await access.ScopedUnits(u).AsNoTracking().Where(x => x.IsActive)
            .OrderBy(x => x.Name).Select(x => new Option(x.Id, x.Name)).ToListAsync();
        var teams = await access.ScopedTeams(u).AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new Option(x.Id, x.Name)).ToListAsync();
        var roles = await db.JobRoles.AsNoTracking().Where(x => x.IsActive).OrderByDescending(x => x.IsLeadRole).ThenBy(x => x.SortOrder)
            .ThenBy(x => x.Name).Select(x => new Option(x.Id, x.Name)).ToListAsync();
        var cats = await db.WorkCategories.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new Option(x.Id, x.Name)).ToListAsync();
        var locs = await db.WorkLocations.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new Option(x.Id, x.Name)).ToListAsync();

        // Şef adayları: hesabı "Şef" rolünde olan aktif personel (onay verebilmeleri için).
        var supRoleId = await db.Roles.Where(r => r.Name == Domain.Security.SystemRoles.Supervisor).Select(r => r.Id).FirstOrDefaultAsync();
        var supervisors = await access.ScopedEmployees(u).AsNoTracking()
            .Where(e => e.IsActive && e.ApplicationUserId != null && db.UserRoles.Any(ur => ur.UserId == e.ApplicationUserId && ur.RoleId == supRoleId))
            .OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new Option(e.Id, e.FirstName + " " + e.LastName)).ToListAsync();

        var managerRoles = await db.Roles.Where(r => r.Name == Domain.Security.SystemRoles.UnitManager || r.Name == Domain.Security.SystemRoles.Planner
                                                     || r.Name == Domain.Security.SystemRoles.SystemAdmin).Select(r => r.Id).ToListAsync();
        var managers = await db.Users.AsNoTracking()
            .Where(x => x.IsActive && db.UserRoles.Any(ur => ur.UserId == x.Id && managerRoles.Contains(ur.RoleId)))
            .OrderBy(x => x.FullName).Select(x => new { x.Id, x.FullName }).ToListAsync();

        return new Lookups(units, teams, roles, cats, locs, supervisors, managers.Select(m => (m.Id, m.FullName)).ToList());
    }
}

public class EmployeeInput
{
    public string? EmployeeNumber { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int OrganizationUnitId { get; set; }
    public int? TeamId { get; set; }
    public int? JobRoleId { get; set; }
    public int? SupervisorEmployeeId { get; set; }
    public string? Phone { get; set; }
}

public record EmployeeRow(int Id, string FullName, string? EmployeeNumber, bool IsActive, string Unit, string? Team, string? JobRole,
    string? Supervisor, bool HasAccount, string? Phone);

public class EmployeeService(AppDbContext db, AccessService access, AuditService audit)
{
    public async Task<PagedResult<EmployeeRow>> ListAsync(UserContext u, string? q, int? teamId, int? roleId, bool? active, int page, int? unitId = null)
    {
        var query = access.ScopedEmployees(u).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = u.CanSeeSensitive
                ? query.Where(e => (e.FirstName + " " + e.LastName).Contains(t) || e.EmployeeNumber == t)
                : query.Where(e => (e.FirstName + " " + e.LastName).Contains(t));
        }
        if (unitId is int unit) query = query.Where(e => e.OrganizationUnitId == unit);
        if (teamId is int team) query = query.Where(e => e.TeamId == team);
        if (roleId is int role) query = query.Where(e => e.JobRoleId == role);
        if (active is bool a) query = query.Where(e => e.IsActive == a);
        page = Math.Max(1, page);
        const int size = 50;
        var total = await query.CountAsync();
        var sensitive = u.CanSeeSensitive;
        // Organizasyon sırası: birim → aktifler önce → ad
        var items = await query.OrderBy(e => e.OrganizationUnit.Name).ThenByDescending(e => e.IsActive).ThenBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Skip((page - 1) * size).Take(size)
            .Select(e => new EmployeeRow(e.Id, e.FirstName + " " + e.LastName, sensitive ? e.EmployeeNumber : null, e.IsActive,
                e.OrganizationUnit.Name, e.Team != null ? e.Team.Name : null, e.JobRole != null ? e.JobRole.Name : null,
                e.SupervisorEmployee != null ? e.SupervisorEmployee.FirstName + " " + e.SupervisorEmployee.LastName : null,
                e.ApplicationUserId != null, sensitive ? e.Phone : null))
            .ToListAsync();
        return new PagedResult<EmployeeRow> { Items = items, Page = page, PageSize = size, Total = total };
    }

    public async Task<Employee> GetAsync(UserContext u, int id) =>
        await access.ScopedEmployees(u).AsNoTracking().Include(e => e.Availabilities.OrderByDescending(a => a.StartAtUtc))
            .FirstOrDefaultAsync(e => e.Id == id) ?? throw new NotFoundException("Personel bulunamadı.");

    public async Task<Employee> SaveAsync(UserContext u, int? id, EmployeeInput input)
    {
        if (!u.CanManagePersonnel) throw new ForbiddenException("Personel ekleme ve düzenleme yetkiniz yok.");
        var first = input.FirstName?.Trim() ?? "";
        var last = input.LastName?.Trim() ?? "";
        if (first.Length is < 1 or > 80 || last.Length is < 1 or > 80) throw new DomainException("Ad ve soyad zorunludur (en fazla 80 karakter).");
        var number = string.IsNullOrWhiteSpace(input.EmployeeNumber) ? null : input.EmployeeNumber.Trim();
        if (number is { Length: > 30 }) throw new DomainException("Sicil numarası en fazla 30 karakter olabilir.");
        if (number is not null && await db.Employees.AnyAsync(e => e.EmployeeNumber == number && e.Id != (id ?? 0)))
            throw new DomainException("Bu sicil numarası başka bir personelde kayıtlı.");
        if (!await db.OrganizationUnits.AnyAsync(x => x.Id == input.OrganizationUnitId)) throw new DomainException("Geçerli bir birim seçin.");
        if (!u.InScope(input.OrganizationUnitId))
            throw new DomainException("Yalnız kendi biriminize personel ekleyebilirsiniz.");
        if (input.TeamId is int t && !await access.ScopedTeams(u).AnyAsync(x => x.Id == t)) throw new DomainException("Seçilen ekip geçersiz.");
        if (input.JobRoleId is int r && !await db.JobRoles.AnyAsync(x => x.Id == r)) throw new DomainException("Seçilen unvan geçersiz.");
        if (input.SupervisorEmployeeId is int s && (s == id || !await db.Employees.AnyAsync(x => x.Id == s)))
            throw new DomainException("Seçilen bağlı şef geçersiz.");

        Employee e;
        if (id is int existing)
        {
            e = await access.ScopedEmployees(u).FirstOrDefaultAsync(x => x.Id == existing) ?? throw new NotFoundException("Personel bulunamadı.");
            if (e.IsActive && !input.IsActive && await HasOpenAssignmentsAsync(e.Id))
                throw new DomainException("Personelin devam eden işleri var. Pasife almadan önce bu işlerden çıkarın.");
        }
        else
        {
            e = new Employee();
            db.Employees.Add(e);
        }
        e.EmployeeNumber = number;
        e.FirstName = first;
        e.LastName = last;
        e.IsActive = input.IsActive;
        e.OrganizationUnitId = input.OrganizationUnitId;
        e.TeamId = input.TeamId;
        e.JobRoleId = input.JobRoleId;
        e.SupervisorEmployeeId = input.SupervisorEmployeeId;
        e.Phone = string.IsNullOrWhiteSpace(input.Phone) ? null : input.Phone.Trim();
        e.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        // Denetim kaydına kişisel veri (telefon, sicil) yazılmaz.
        audit.Add(u, id is null ? "EmployeeCreated" : "EmployeeUpdated", "Employee", e.Id,
            $"Personel {(id is null ? "eklendi" : "güncellendi")}: {e.FullName}{(e.IsActive ? "" : " (pasif)")}");
        await db.SaveChangesAsync();
        return e;
    }

    public Task<bool> HasOpenAssignmentsAsync(int employeeId) =>
        db.WorkAssignments.AnyAsync(a => a.EmployeeId == employeeId && a.ReleasedAtUtc == null);

    public async Task AddAvailabilityAsync(UserContext u, int employeeId, Domain.Enums.AvailabilityType type, DateTime? startLocal,
        DateTime? endLocal, string? note)
    {
        if (!u.CanManageLeave) throw new ForbiddenException("İzin / müsaitlik kaydı yönetme yetkiniz yok.");
        var e = await access.ScopedEmployees(u).FirstOrDefaultAsync(x => x.Id == employeeId) ?? throw new NotFoundException();
        if (startLocal is null || endLocal is null) throw new DomainException("Başlangıç ve bitiş zorunludur.");
        var s = TrTime.ToUtc(startLocal.Value);
        var en = TrTime.ToUtc(endLocal.Value);
        if (en <= s) throw new DomainException("Bitiş, başlangıçtan sonra olmalı.");
        if (!Enum.IsDefined(type)) throw new DomainException("Geçersiz izin türü.");
        db.EmployeeAvailabilities.Add(new EmployeeAvailability
        {
            EmployeeId = e.Id, Type = type, StartAtUtc = s, EndAtUtc = en, CreatedByUserId = u.UserId,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        });
        audit.Add(u, "AvailabilityAdded", "Employee", e.Id, $"{e.FullName}: {Domain.Enums.TrNames.Of(type)} {TrTime.Range(s, en)}");
        await db.SaveChangesAsync();
    }

    public async Task RemoveAvailabilityAsync(UserContext u, int employeeId, int availabilityId)
    {
        if (!u.CanManageLeave) throw new ForbiddenException("İzin / müsaitlik kaydı yönetme yetkiniz yok.");
        var e = await access.ScopedEmployees(u).FirstOrDefaultAsync(x => x.Id == employeeId) ?? throw new NotFoundException();
        var a = await db.EmployeeAvailabilities.FirstOrDefaultAsync(x => x.Id == availabilityId && x.EmployeeId == e.Id) ?? throw new NotFoundException();
        db.EmployeeAvailabilities.Remove(a);
        audit.Add(u, "AvailabilityRemoved", "Employee", e.Id, $"{e.FullName}: izin kaydı kaldırıldı ({TrTime.Range(a.StartAtUtc, a.EndAtUtc)})");
        await db.SaveChangesAsync();
    }

    public async Task<List<Option>> AllActiveAsync(UserContext u) =>
        await access.ScopedEmployees(u).AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.FirstName).ThenBy(e => e.LastName)
            .Select(e => new Option(e.Id, e.FirstName + " " + e.LastName)).ToListAsync();
}

public class MaterialService(AppDbContext db, AuditService audit)
{
    public async Task<List<MaterialCatalog>> ListAsync(string? q, bool includeInactive)
    {
        var query = db.MaterialCatalog.AsNoTracking();
        if (!includeInactive) query = query.Where(m => m.IsActive);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(m => m.Name.Contains(t) || m.Code.Contains(t) || (m.Category != null && m.Category.Contains(t)));
        }
        return await query.OrderByDescending(m => m.IsActive).ThenBy(m => m.Name).Take(500).ToListAsync();
    }

    public async Task<MaterialCatalog> SaveAsync(UserContext u, int? id, string? code, string? name, string? category, string? unit,
        string? description, bool isActive)
    {
        if (!u.CanManageMaterials) throw new ForbiddenException("Malzeme kataloğunu düzenleme yetkiniz yok.");
        var c = code?.Trim() ?? "";
        var n = name?.Trim() ?? "";
        var un = unit?.Trim() ?? "";
        if (c.Length is < 1 or > 40) throw new DomainException("Malzeme kodu zorunludur (en fazla 40 karakter).");
        if (n.Length is < 2 or > 200) throw new DomainException("Malzeme adı 2–200 karakter olmalı.");
        if (un.Length is < 1 or > 30) throw new DomainException("Birim zorunludur (adet, metre, kilogram…).");
        if (await db.MaterialCatalog.AnyAsync(m => m.Code == c && m.Id != (id ?? 0))) throw new DomainException("Bu malzeme kodu zaten kullanılıyor.");

        MaterialCatalog m;
        if (id is int existing) m = await db.MaterialCatalog.FirstOrDefaultAsync(x => x.Id == existing) ?? throw new NotFoundException();
        else { m = new MaterialCatalog(); db.MaterialCatalog.Add(m); }
        m.Code = c;
        m.Name = n;
        m.Unit = un;
        m.Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        m.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        m.IsActive = isActive;
        await db.SaveChangesAsync();
        audit.Add(u, id is null ? "MaterialCreated" : "MaterialUpdated", "MaterialCatalog", m.Id,
            $"Malzeme {(id is null ? "eklendi" : "güncellendi")}: {c} {n} ({un})");
        await db.SaveChangesAsync();
        return m;
    }

    public record UsageRow(int WorkOrderId, string WorkNumber, string Title, string Material, string Unit, decimal Quantity, DateTime AddedAtUtc);

    /// <summary>İş bazlı malzeme kullanımı (yalnız kullanıcının görebildiği işler).</summary>
    public async Task<List<UsageRow>> UsageAsync(IQueryable<WorkOrder> visibleWorks, string? q)
    {
        var query = db.WorkMaterials.AsNoTracking().Where(m => visibleWorks.Any(w => w.Id == m.WorkOrderId));
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(m => m.MaterialNameSnapshot.Contains(q.Trim()));
        return await query.OrderByDescending(m => m.AddedAtUtc).Take(500)
            .Join(db.WorkOrders, m => m.WorkOrderId, w => w.Id, (m, w) => new UsageRow(w.Id, w.WorkNumber, w.Title, m.MaterialNameSnapshot,
                m.UnitSnapshot, m.Quantity, m.AddedAtUtc))
            .ToListAsync();
    }
}
