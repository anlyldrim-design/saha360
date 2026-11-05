using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

public record UserRow(string Id, string UserName, string FullName, string? Email, bool IsActive, List<string> Roles, string? Unit,
    int? EmployeeId, string? EmployeeName, DateTime? LastLoginAtUtc);

public class UserInput
{
    public string UserName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public int? OrganizationUnitId { get; set; }
    /// <summary>Kullanıcıya özel: tüm birimlerin verisini görsün.</summary>
    public bool AllUnits { get; set; }
    public int? EmployeeId { get; set; }
    public List<string> Roles { get; set; } = [];
    public string? NewPassword { get; set; }

    /// <summary>EmployeeId bu değerse hesapla birlikte yeni personel kaydı açılır (personel listede yoksa).</summary>
    public const int NewEmployee = -1;
    public int? NewEmployeeUnitId { get; set; }
    public int? NewEmployeeJobRoleId { get; set; }
    public string? NewEmployeeNumber { get; set; }
}

/// <summary>Kullanıcı hesapları, sistem rolleri ve personel eşlemesi. Yalnız sistem yöneticisi kullanır.</summary>
public class UserAdminService(AppDbContext db, UserManager<ApplicationUser> users, AuditService audit)
{
    public async Task<List<UserRow>> ListAsync()
    {
        var list = await db.Users.AsNoTracking().OrderByDescending(u => u.IsActive).ThenBy(u => u.FullName).ToListAsync();
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync();
        var units = await db.OrganizationUnits.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name);
        var emps = await db.Employees.AsNoTracking().Where(e => e.ApplicationUserId != null)
            .Select(e => new { e.Id, e.ApplicationUserId, Name = e.FirstName + " " + e.LastName }).ToListAsync();
        return list.Select(u =>
        {
            var emp = emps.FirstOrDefault(e => e.ApplicationUserId == u.Id);
            return new UserRow(u.Id, u.UserName!, u.FullName, u.Email, u.IsActive,
                roles.Where(r => r.UserId == u.Id).Select(r => r.Name!).ToList(),
                u.AllUnits ? "Tüm birimler" : u.OrganizationUnitId is int unit && units.TryGetValue(unit, out var un) ? un : null, emp?.Id, emp?.Name, u.LastLoginAtUtc);
        }).ToList();
    }

    public async Task<UserInput> GetAsync(string id)
    {
        var u = await users.FindByIdAsync(id) ?? throw new NotFoundException("Kullanıcı bulunamadı.");
        var empId = await db.Employees.Where(e => e.ApplicationUserId == id).Select(e => (int?)e.Id).FirstOrDefaultAsync();
        return new UserInput
        {
            UserName = u.UserName!, FullName = u.FullName, Email = u.Email, IsActive = u.IsActive, OrganizationUnitId = u.OrganizationUnitId, AllUnits = u.AllUnits,
            EmployeeId = empId, Roles = (await users.GetRolesAsync(u)).ToList()
        };
    }

    public async Task<string> SaveAsync(UserContext actor, string? id, UserInput input)
    {
        if (!actor.CanManageUsers) throw new ForbiddenException("Kullanıcıları yönetme yetkiniz yok.");
        var roles = input.Roles.Where(r => SystemRoles.All.Contains(r)).Distinct().ToList();
        // Yetki yükseltme engeli: sistem yöneticisi rolünü vermek/almak ve bir yöneticinin hesabını değiştirmek yalnız sistem yöneticisine açık.
        if (!actor.IsAdmin)
        {
            if (roles.Contains(SystemRoles.SystemAdmin)) throw new ForbiddenException("Sistem yöneticisi rolünü yalnız sistem yöneticisi verebilir.");
            if (input.AllUnits) throw new ForbiddenException("Tüm birimler kapsamını yalnız sistem yöneticisi verebilir.");
            if (id is not null && await users.FindByIdAsync(id) is { } target && await users.IsInRoleAsync(target, SystemRoles.SystemAdmin))
                throw new ForbiddenException("Sistem yöneticisi hesabını yalnız sistem yöneticisi değiştirebilir.");
            // Kendi biriminin dışına hesap açamaz / bağlayamaz (aksi halde başka birimin verisini gören hesap üretebilirdi).
            if (input.OrganizationUnitId is int ou && !actor.InScope(ou))
                throw new ForbiddenException("Yalnız kendi biriminiz için hesap açabilirsiniz.");
            if (input.EmployeeId == UserInput.NewEmployee && input.NewEmployeeUnitId is int nu0 && !actor.InScope(nu0))
                throw new ForbiddenException("Yalnız kendi biriminize personel ekleyebilirsiniz.");
            if (input.EmployeeId is int ei and > 0 && await db.Employees.Where(e => e.Id == ei).Select(e => (int?)e.OrganizationUnitId).FirstOrDefaultAsync() is int eu
                && !actor.InScope(eu))
                throw new ForbiddenException("Bu personel kaydı kapsamınız dışında.");
        }
        if (roles.Count == 0) throw new DomainException("En az bir sistem rolü seçin.");
        if (string.IsNullOrWhiteSpace(input.FullName)) throw new DomainException("Ad soyad zorunludur.");
        if (input.OrganizationUnitId is int unit && !await db.OrganizationUnits.AnyAsync(x => x.Id == unit))
            throw new DomainException("Seçilen birim geçersiz.");
        var createEmployee = input.EmployeeId == UserInput.NewEmployee;
        if (createEmployee)
        {
            if (input.NewEmployeeUnitId is not int nu || !await db.OrganizationUnits.AnyAsync(x => x.Id == nu))
                throw new DomainException("Yeni personel kaydı için birim seçin.");
            if (input.NewEmployeeJobRoleId is int nr && !await db.JobRoles.AnyAsync(x => x.Id == nr))
                throw new DomainException("Seçilen unvan geçersiz.");
            var no = string.IsNullOrWhiteSpace(input.NewEmployeeNumber) ? null : input.NewEmployeeNumber.Trim();
            if (no is not null && await db.Employees.AnyAsync(e => e.EmployeeNumber == no))
                throw new DomainException("Bu sicil numarası başka bir personelde kayıtlı.");
        }
        else if (input.EmployeeId is int empId)
        {
            var linked = await db.Employees.Where(e => e.Id == empId).Select(e => new { e.ApplicationUserId }).FirstOrDefaultAsync()
                         ?? throw new DomainException("Seçilen personel bulunamadı.");
            if (linked.ApplicationUserId is not null && linked.ApplicationUserId != id)
                throw new DomainException("Bu personel başka bir kullanıcı hesabına bağlı.");
        }
        if ((roles.Contains(SystemRoles.Personnel) || roles.Contains(SystemRoles.Supervisor)) && input.EmployeeId is null)
            throw new DomainException("Personel ve Şef rolündeki hesaplar bir personel kaydına bağlanmalı.");

        await using var tx = await db.Database.BeginTransactionAsync();
        ApplicationUser u;
        if (id is null)
        {
            if (string.IsNullOrWhiteSpace(input.NewPassword)) throw new DomainException("Yeni kullanıcı için ilk parola zorunludur.");
            u = new ApplicationUser
            {
                UserName = input.UserName?.Trim(), Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim(),
                FullName = input.FullName.Trim(), IsActive = input.IsActive, OrganizationUnitId = input.OrganizationUnitId, AllUnits = input.AllUnits, EmailConfirmed = true
            };
            Check(await users.CreateAsync(u, input.NewPassword));
        }
        else
        {
            u = await users.FindByIdAsync(id) ?? throw new NotFoundException("Kullanıcı bulunamadı.");
            var currentRoles = await users.GetRolesAsync(u);
            var losingAdmin = currentRoles.Contains(SystemRoles.SystemAdmin) && (!roles.Contains(SystemRoles.SystemAdmin) || !input.IsActive);
            if (losingAdmin && await CountActiveAdminsAsync() <= 1)
                throw new DomainException("Son aktif sistem yöneticisinin yetkisi kaldırılamaz.");
            u.FullName = input.FullName.Trim();
            u.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
            u.IsActive = input.IsActive;
            u.OrganizationUnitId = input.OrganizationUnitId;
            if (actor.IsAdmin) u.AllUnits = input.AllUnits;
            if (!string.Equals(u.UserName, input.UserName?.Trim(), StringComparison.Ordinal))
                Check(await users.SetUserNameAsync(u, input.UserName?.Trim()));
            Check(await users.UpdateAsync(u));
            Check(await users.RemoveFromRolesAsync(u, currentRoles.Except(roles)));
            if (!string.IsNullOrWhiteSpace(input.NewPassword))
            {
                Check(await users.RemovePasswordAsync(u));
                Check(await users.AddPasswordAsync(u, input.NewPassword));
            }
            // Rol veya aktiflik değişikliği açık oturumlara en geç 1 dakikada yansısın.
            await users.UpdateSecurityStampAsync(u);
        }
        var existingRoles = await users.GetRolesAsync(u);
        Check(await users.AddToRolesAsync(u, roles.Except(existingRoles)));

        // Personel eşlemesi: önceki bağlantı kaldırılır, yenisi kurulur.
        await db.Employees.Where(e => e.ApplicationUserId == u.Id && e.Id != (input.EmployeeId ?? 0))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ApplicationUserId, (string?)null));
        if (createEmployee)
        {
            // Ad soyaddan personel kaydı: son kelime soyad, öncesi ad.
            var parts = input.FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var emp = new Domain.Entities.Employee
            {
                FirstName = parts.Length > 1 ? string.Join(' ', parts[..^1]) : parts[0],
                LastName = parts.Length > 1 ? parts[^1] : "-",
                EmployeeNumber = string.IsNullOrWhiteSpace(input.NewEmployeeNumber) ? null : input.NewEmployeeNumber.Trim(),
                OrganizationUnitId = input.NewEmployeeUnitId!.Value,
                JobRoleId = input.NewEmployeeJobRoleId,
                IsActive = true,
                ApplicationUserId = u.Id
            };
            db.Employees.Add(emp);
            await db.SaveChangesAsync();
            audit.Add(actor, "EmployeeCreated", "Employee", emp.Id, $"Personel eklendi (kullanıcı hesabıyla birlikte): {emp.FullName}");
        }
        else if (input.EmployeeId is int eid)
            await db.Employees.Where(e => e.Id == eid).ExecuteUpdateAsync(s => s.SetProperty(e => e.ApplicationUserId, u.Id));

        audit.Add(actor, id is null ? "UserCreated" : "UserUpdated", "User", u.Id,
            $"Kullanıcı {(id is null ? "oluşturuldu" : "güncellendi")}: {u.UserName} — roller: {string.Join(", ", roles.Select(SystemRoles.DisplayName))}"
            + (u.IsActive ? "" : " (pasif)") + (string.IsNullOrWhiteSpace(input.NewPassword) || id is null ? "" : " — parola sıfırlandı"));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return u.Id;
    }

    public async Task<int> CountActiveAdminsAsync()
    {
        var admins = await users.GetUsersInRoleAsync(SystemRoles.SystemAdmin);
        return admins.Count(a => a.IsActive);
    }

    public static async Task EnsureRolesAsync(RoleManager<IdentityRole> roles)
    {
        foreach (var r in SystemRoles.All)
            if (!await roles.RoleExistsAsync(r))
                await roles.CreateAsync(new IdentityRole(r));
    }

    private static void Check(IdentityResult r)
    {
        if (!r.Succeeded) throw new DomainException(string.Join(" ", r.Errors.Select(e => e.Description)));
    }
}
