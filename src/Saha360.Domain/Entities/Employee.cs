using Saha360.Domain.Enums;

namespace Saha360.Domain.Entities;

/// <summary>
/// Personel kaydı. Uygulama hesabı olmayan personel de olabilir (ApplicationUserId boş).
/// Silinmez; pasife alınır ki geçmiş iş ve raporlar bozulmasın.
/// "Havuzda mı?" bilgisi burada tutulmaz: aktif atamalardan ve izinlerden her seferinde hesaplanır.
/// </summary>
public class Employee
{
    public int Id { get; set; }

    /// <summary>Kurum sicil numarası (hassas; yalnız yetkili kişilere gösterilir).</summary>
    public string? EmployeeNumber { get; set; }

    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public int OrganizationUnitId { get; set; }
    public OrganizationUnit OrganizationUnit { get; set; } = null!;

    public int? TeamId { get; set; }
    public Team? Team { get; set; }

    /// <summary>Varsayılan görev/unvan. Bir işteki rolü atamada ayrıca seçilir.</summary>
    public int? JobRoleId { get; set; }
    public JobRole? JobRole { get; set; }

    public int? SupervisorEmployeeId { get; set; }
    public Employee? SupervisorEmployee { get; set; }

    public string? ApplicationUserId { get; set; }

    /// <summary>İletişim bilgisi (yalnız yetkili kişilere gösterilir).</summary>
    public string? Phone { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = [];

    public List<EmployeeAvailability> Availabilities { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}";
}

/// <summary>İzin / rapor / müsait olmama dönemi.</summary>
public class EmployeeAvailability
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;
    public AvailabilityType Type { get; set; }
    public DateTime StartAtUtc { get; set; }
    public DateTime EndAtUtc { get; set; }
    public string? Note { get; set; }
    public string CreatedByUserId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
