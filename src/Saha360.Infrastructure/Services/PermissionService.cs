using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Saha360.Domain.Entities;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

/// <summary>
/// Rol → izin tablosu. Açılışta eksik (rol, izin) çiftleri varsayılanla doldurulur; böylece yeni sürümde eklenen izinler
/// mevcut kurulumlara da gelir. Tablo bellekte önbelleklenir ve değişince temizlenir.
/// </summary>
public class PermissionService(AppDbContext db, IMemoryCache cache, AuditService audit)
{
    private const string CacheKey = "role-permissions";

    /// <summary>Sistem yöneticisi dışındaki roller (sistem yöneticisi her zaman her şeye sahiptir).</summary>
    public static readonly string[] EditableRoles = SystemRoles.All.Where(r => r != SystemRoles.SystemAdmin).ToArray();

    public async Task EnsureDefaultsAsync()
    {
        var existing = await db.RolePermissions.Select(x => x.RoleName + "|" + x.Permission).ToListAsync();
        var have = existing.ToHashSet();
        foreach (var p in Permissions.All)
            foreach (var role in EditableRoles)
                if (!have.Contains(role + "|" + p.Key))
                    db.RolePermissions.Add(new RolePermission { RoleName = role, Permission = p.Key, Granted = p.DefaultRoles.Contains(role) });
        await db.SaveChangesAsync();
        cache.Remove(CacheKey);
    }

    /// <summary>Rol adı → verilen izinler.</summary>
    public async Task<Dictionary<string, HashSet<string>>> MatrixAsync() =>
        (await cache.GetOrCreateAsync(CacheKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            var rows = await db.RolePermissions.AsNoTracking().Where(x => x.Granted).ToListAsync();
            return EditableRoles.ToDictionary(r => r, r => rows.Where(x => x.RoleName == r).Select(x => x.Permission).ToHashSet());
        }))!;

    /// <summary>Kullanıcının rollerinden gelen izinlerin birleşimi.</summary>
    public async Task<HashSet<string>> ForRolesAsync(IEnumerable<string> roles)
    {
        var list = roles.ToList();
        if (list.Contains(SystemRoles.SystemAdmin)) return Permissions.Keys.ToHashSet();
        var matrix = await MatrixAsync();
        return list.SelectMany(r => matrix.GetValueOrDefault(r) ?? []).ToHashSet();
    }

    /// <summary>Yetkiler ekranından kaydet. Yalnız sistem yöneticisi (izinle verilemez: yetki yükseltmeyi önler).</summary>
    public async Task SaveAsync(UserContext u, IReadOnlyCollection<string> grantedPairs)
    {
        if (!u.IsAdmin) throw new ForbiddenException("Yetki tablosunu yalnız sistem yöneticisi değiştirebilir.");
        var wanted = grantedPairs.ToHashSet();
        var rows = await db.RolePermissions.ToListAsync();
        var changes = new List<string>();
        foreach (var row in rows.Where(r => EditableRoles.Contains(r.RoleName)))
        {
            var g = wanted.Contains(row.RoleName + "|" + row.Permission);
            if (g == row.Granted) continue;
            row.Granted = g;
            var label = Permissions.All.FirstOrDefault(p => p.Key == row.Permission)?.Label ?? row.Permission;
            changes.Add($"{SystemRoles.DisplayName(row.RoleName)}: {label} {(g ? "verildi" : "kaldırıldı")}");
        }
        if (changes.Count == 0) return;
        audit.Add(u, "PermissionsChanged", "RolePermissions", "matrix", "Yetki tablosu değişti: " + string.Join(" · ", changes));
        await db.SaveChangesAsync();
        cache.Remove(CacheKey);
    }
}
