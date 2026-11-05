using System.ComponentModel.DataAnnotations;
using Saha360.Domain.Enums;
using Saha360.Domain.Security;

namespace Saha360.Infrastructure.Services;

/// <summary>
/// İsteği yapan kullanıcı: kim, hangi izinler (rollerinden, Yetkiler ekranındaki tabloya göre) ve hangi birim kapsamı.
/// Her servis çağrısına verilir; yetki kararları yalnız bu nesneden okunur (rol adına bakılmaz).
/// </summary>
/// <param name="UnitId">Kapsam birimi: kullanıcı hesabındaki birim, yoksa bağlı personel kaydının birimi.</param>
/// <param name="Granted">Rollerinden gelen izinler. Verilmezse (testler, açılış verisi) varsayılan tablo kullanılır.</param>
/// <param name="AllUnitsGranted">Kullanıcıya özel "tüm birimleri görsün" ayarı.</param>
public sealed record UserContext(string UserId, string FullName, IReadOnlySet<string> Roles, int? EmployeeId, int? UnitId,
    string? IpAddress = null, IReadOnlySet<string>? Granted = null, bool AllUnitsGranted = false)
{
    // Önbelleklenmez: "with" ile kopyalanan kayıtta eski izin kümesi taşınmasın.
    public IReadOnlySet<string> Perms => IsAdmin ? AllKeys : Granted ?? Permissions.DefaultsFor(Roles);
    private static readonly IReadOnlySet<string> AllKeys = Permissions.Keys.ToHashSet();

    public bool Has(string permission) => Perms.Contains(permission);

    public bool IsAdmin => Roles.Contains(SystemRoles.SystemAdmin);
    /// <summary>Şef rolü: yalnız "sorumlu şef seçilebilir" listesi için; onay yetkisi işin sorumlu şefi olmaktan gelir.</summary>
    public bool IsSupervisor => Roles.Contains(SystemRoles.Supervisor);

    /// <summary>Tüm birimlerin verisini görür (izinle veya kullanıcıya özel ayarla).</summary>
    public bool AllUnits => AllUnitsGranted || Has(Permissions.AllUnits);

    /// <summary>Bu birimin verisi kullanıcının kapsamında mı?</summary>
    public bool InScope(int unitId) => AllUnits || UnitId == unitId;

    // Sık kullanılan izinler (okunabilirlik için)
    public bool CanViewAllWorks => Has(Permissions.WorksViewAll);
    public bool CanCreateWork => Has(Permissions.WorksCreate);
    public bool CanAssign => Has(Permissions.WorksAssign);
    public bool CanViewPersonnel => Has(Permissions.PersonnelView);
    public bool CanManagePersonnel => Has(Permissions.PersonnelManage);
    public bool CanManageLeave => Has(Permissions.LeaveManage);
    public bool CanManageTeams => Has(Permissions.TeamsManage);
    public bool CanManageDefinitions => Has(Permissions.DefinitionsManage);
    public bool CanManageMaterials => Has(Permissions.MaterialsManage);
    public bool CanViewReports => Has(Permissions.ReportsView);
    public bool CanExport => Has(Permissions.ReportsExport);
    public bool CanManageUsers => Has(Permissions.UsersManage);

    /// <summary>Sicil numarası ve telefon gibi hassas alanları görebilir.</summary>
    public bool CanSeeSensitive => CanViewPersonnel || CanManagePersonnel;

    /// <summary>Personel/havuz/ekip ekranlarından en az birini kullanabilir.</summary>
    public bool CanSeeOrganization => CanViewPersonnel || CanManagePersonnel || CanManageTeams || CanManageLeave || CanAssign;
}

public record OpResult(bool Changed, string Message)
{
    public static OpResult Done(string message) => new(true, message);
    public static OpResult NoOp(string message) => new(false, message);
}

public class PagedResult<T>
{
    public List<T> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

/// <summary>İş oluşturma/düzenleme formu. Tarihler Türkiye saatiyle girilir; servis UTC'ye çevirir.</summary>
public class WorkOrderInput
{
    [Required(ErrorMessage = "İş başlığı zorunludur.")]
    [StringLength(200, ErrorMessage = "İş başlığı en fazla 200 karakter olabilir.")]
    [Display(Name = "İş başlığı")]
    public string Title { get; set; } = "";

    [StringLength(4000, ErrorMessage = "Açıklama en fazla 4000 karakter olabilir.")]
    [Display(Name = "Açıklama")]
    public string? Description { get; set; }

    [StringLength(1000, ErrorMessage = "Talimat en fazla 1000 karakter olabilir.")]
    [Display(Name = "Saha talimatı")]
    public string? Instructions { get; set; }

    [Display(Name = "Kategori")]
    public int? WorkCategoryId { get; set; }

    [Display(Name = "Öncelik")]
    public WorkPriority Priority { get; set; } = WorkPriority.Normal;

    [Display(Name = "Bölge / saha")]
    public int? WorkLocationId { get; set; }

    [StringLength(300, ErrorMessage = "Adres en fazla 300 karakter olabilir.")]
    [Display(Name = "Adres / konum")]
    public string? LocationText { get; set; }

    [Required(ErrorMessage = "Planlanan başlangıç zorunludur.")]
    [Display(Name = "Planlanan başlangıç")]
    public DateTime? PlannedStartLocal { get; set; }

    [Required(ErrorMessage = "Planlanan bitiş zorunludur.")]
    [Display(Name = "Planlanan bitiş")]
    public DateTime? PlannedEndLocal { get; set; }

    [Required(ErrorMessage = "Sorumlu birim zorunludur.")]
    [Display(Name = "Sorumlu birim")]
    public int? ResponsibleUnitId { get; set; }

    [Display(Name = "Sorumlu şef (onaylayacak)")]
    public int? ResponsibleSupervisorEmployeeId { get; set; }

    [Display(Name = "Bilgilendirilecek yönetici")]
    public string? ManagerUserId { get; set; }
}

public record MaterialLineInput(int MaterialId, decimal Quantity, string? Note);

public record AssignmentInput(int EmployeeId, int JobRoleId, string? TaskNote);

public record UploadInput(Stream Content, string FileName, long Length, string? Caption, AttachmentType Type);

public class WorkListQuery
{
    public string? Q { get; set; }
    public WorkStatus? Status { get; set; }

    /// <summary>Hazır görünümler: aktif, onay, iade, tamamlanan, atama, bugun.</summary>
    public string? View { get; set; }

    public int? UnitId { get; set; }
    public int? SupervisorId { get; set; }
    public int? EmployeeId { get; set; }
    public int? CategoryId { get; set; }
    public WorkPriority? Priority { get; set; }
    public int? LocationId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public bool Overdue { get; set; }
    public bool HasMaterials { get; set; }
    public string? Sort { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public record WorkListItem(int Id, string WorkNumber, string Title, string? Category, string? Location, DateTime PlannedStartAtUtc,
    DateTime PlannedEndAtUtc, int AssignedCount, string? SupervisorName, WorkPriority Priority, WorkStatus Status, bool IsOverdue,
    DateTime UpdatedAtUtc);

public enum PoolState { Available, Busy, OnLeave, Inactive }

public record PoolItem(int EmployeeId, string FullName, string? EmployeeNumber, int? JobRoleId, string? JobRoleName, int? TeamId,
    string? TeamName, string? SupervisorName, bool HasAccount, PoolState State, string? Reason);

public class PoolQuery
{
    public string? Q { get; set; }
    public int? UnitId { get; set; }
    public int? TeamId { get; set; }
    public int? JobRoleId { get; set; }
    public int? SupervisorId { get; set; }
    public PoolState? State { get; set; }
    public bool IncludeInactive { get; set; }
    public DateTime? FromLocal { get; set; }
    public DateTime? ToLocal { get; set; }
}
