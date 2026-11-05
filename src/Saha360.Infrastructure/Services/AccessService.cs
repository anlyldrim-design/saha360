using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

/// <summary>
/// Kim neyi görebilir, kim ne yapabilir? Tüm sorgular ve eylemler bu kurallardan geçer; menü gizlemek yalnız arayüz kolaylığıdır.
///
/// İki eksen var:
///  • İzin (Yetkiler tablosu): ne yapılabilir — iş oluştur, ekip ata, personel yönet…
///  • Birim kapsamı: hangi veri — kullanıcının birimi (ya da "tüm birimler").
/// Bir eylem için ikisi birlikte gerekir: örn. ekip atamak için "ekip atama" izni VE işin biriminin kapsamda olması.
/// Kişisel ilişki her zaman geçerlidir: kendisine atanan, sorumlu şefi olduğu veya kendi açtığı işi görür.
/// </summary>
public class AccessService(AppDbContext db)
{
    /// <summary>Birim işlerini görmeyi gerektiren izinlerden biri var mı?</summary>
    private static bool SeesUnitWorks(UserContext u) =>
        u.CanViewAllWorks || u.Has(Permissions.WorksEdit) || u.Has(Permissions.WorksAssign) || u.Has(Permissions.WorksCancel)
        || u.Has(Permissions.WorksReopen) || u.Has(Permissions.WorksApproveAny) || u.CanViewReports;

    /// <summary>Kullanıcının görmeye yetkili olduğu işler (liste, panel, rapor ve dışa aktarma aynı kapsamı kullanır).</summary>
    public IQueryable<WorkOrder> VisibleWorks(UserContext u)
    {
        var q = db.WorkOrders.AsQueryable();
        var unitWide = SeesUnitWorks(u);
        if (unitWide && u.AllUnits) return q;

        var unit = unitWide ? u.UnitId : null;
        var emp = u.EmployeeId;
        var uid = u.UserId;
        return q.Where(w => (unit != null && w.ResponsibleUnitId == unit)
                            || (emp != null && w.ResponsibleSupervisorEmployeeId == emp)
                            || (emp != null && w.Status != WorkStatus.Draft && w.Assignments.Any(a => a.EmployeeId == emp))
                            || w.CreatedByUserId == uid
                            || w.ManagerUserId == uid);
    }

    /// <summary>Kapsamdaki personel: tüm birimler ya da kullanıcının birimi; birimi olmayan kullanıcı hiç personel görmez.</summary>
    public IQueryable<Employee> ScopedEmployees(UserContext u)
    {
        var q = db.Employees.AsQueryable();
        if (u.AllUnits) return q;
        var unit = u.UnitId ?? -1;
        return q.Where(e => e.OrganizationUnitId == unit);
    }

    public IQueryable<OrganizationUnit> ScopedUnits(UserContext u)
    {
        var q = db.OrganizationUnits.AsQueryable();
        if (u.AllUnits) return q;
        var unit = u.UnitId ?? -1;
        return q.Where(x => x.Id == unit);
    }

    public IQueryable<Team> ScopedTeams(UserContext u)
    {
        var q = db.Teams.AsQueryable();
        if (u.AllUnits) return q;
        var unit = u.UnitId ?? -1;
        return q.Where(x => x.OrganizationUnitId == unit);
    }

    // ───────── İş üzerinde eylemler: izin + işin birimi kapsamda ─────────

    public bool CanEditWork(UserContext u, WorkOrder w) => u.Has(Permissions.WorksEdit) && u.InScope(w.ResponsibleUnitId);
    public bool CanAssignWork(UserContext u, WorkOrder w) => u.Has(Permissions.WorksAssign) && u.InScope(w.ResponsibleUnitId);
    public bool CanCancelWork(UserContext u, WorkOrder w) => u.Has(Permissions.WorksCancel) && u.InScope(w.ResponsibleUnitId);
    public bool CanReopenWork(UserContext u, WorkOrder w) => u.Has(Permissions.WorksReopen) && u.InScope(w.ResponsibleUnitId);

    /// <summary>İşi yöneten taraf (düzenleme veya ekip atama yetkisi): malzeme satırı kaldırma, başkasının dosyasını kaldırma vb.</summary>
    public bool CanManage(UserContext u, WorkOrder w) => CanEditWork(u, w) || CanAssignWork(u, w);

    /// <summary>Tamamlanma onayı / iade: işin sorumlu şefi; yedek olarak "onay" izni olan (kapsam içinde).</summary>
    public bool CanApprove(UserContext u, WorkOrder w) =>
        (u.EmployeeId is not null && w.ResponsibleSupervisorEmployeeId == u.EmployeeId)
        || (u.Has(Permissions.WorksApproveAny) && u.InScope(w.ResponsibleUnitId));

    /// <summary>Kullanıcının bu işteki ekipte sayılan ataması (assignments yüklenmiş olmalı).</summary>
    public WorkAssignment? ActiveAssignmentOf(UserContext u, WorkOrder w) =>
        u.EmployeeId is int e ? w.Assignments.FirstOrDefault(a => a.EmployeeId == e && a.ReleasedAtUtc == null) : null;

    /// <summary>Not, fotoğraf ve malzeme ekleyebilir mi?</summary>
    public bool CanContribute(UserContext u, WorkOrder w) =>
        WorkflowRules.AcceptsContent(w.Status)
        && (CanManage(u, w) || CanApprove(u, w) || (ActiveAssignmentOf(u, w) is not null && w.Status != WorkStatus.Draft));

    /// <summary>İşi görme yetkisini kontrol ederek yükler. Yetkisizse "bulunamadı" der (varlığını açığa çıkarmaz).</summary>
    public async Task<WorkOrder> LoadVisibleAsync(UserContext u, int workId, bool withAssignments = true, bool track = true)
    {
        IQueryable<WorkOrder> q = VisibleWorks(u).Where(w => w.Id == workId);
        if (withAssignments) q = q.Include(w => w.Assignments).ThenInclude(a => a.Employee);
        if (!track) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync() ?? throw new NotFoundException("İş bulunamadı veya görme yetkiniz yok.");
    }
}
