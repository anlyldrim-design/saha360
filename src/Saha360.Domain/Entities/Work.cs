using Saha360.Domain.Enums;

namespace Saha360.Domain.Entities;

/// <summary>Ana iş kaydı. Durum yalnızca WorkflowService üzerinden değişir.</summary>
public class WorkOrder
{
    public int Id { get; set; }

    /// <summary>Kullanıcıya gösterilen benzersiz numara (ör. IS-2026-00042).</summary>
    public string WorkNumber { get; set; } = "";

    public string Title { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Saha talimatı: personelin işi kabul ederken okuyup onayladığı kısa metin.</summary>
    public string? Instructions { get; set; }

    public int? WorkCategoryId { get; set; }
    public WorkCategory? WorkCategory { get; set; }

    public WorkPriority Priority { get; set; } = WorkPriority.Normal;
    public WorkStatus Status { get; set; } = WorkStatus.Draft;

    public int? WorkLocationId { get; set; }
    public WorkLocation? WorkLocation { get; set; }
    public string? LocationText { get; set; }

    public DateTime PlannedStartAtUtc { get; set; }
    public DateTime PlannedEndAtUtc { get; set; }
    public DateTime? ActualStartAtUtc { get; set; }
    public DateTime? CompletionSubmittedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }

    /// <summary>Personelin şefe gönderirken yazdığı son tamamlanma açıklaması.</summary>
    public string? CompletionNote { get; set; }

    public string CreatedByUserId { get; set; } = "";

    public int ResponsibleUnitId { get; set; }
    public OrganizationUnit ResponsibleUnit { get; set; } = null!;

    /// <summary>Tamamlanma onayını verecek şef.</summary>
    public int? ResponsibleSupervisorEmployeeId { get; set; }
    public Employee? ResponsibleSupervisorEmployee { get; set; }

    /// <summary>Şef onayından sonra bilgilendirilecek yönetici.</summary>
    public string? ManagerUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = [];

    public List<WorkAssignment> Assignments { get; set; } = [];
    public List<WorkMaterial> Materials { get; set; } = [];
    public List<WorkNote> Notes { get; set; } = [];
    public List<WorkAttachment> Attachments { get; set; } = [];
    public List<WorkStatusHistory> StatusHistory { get; set; } = [];

    /// <summary>Bu işin ekip alanları (Şef, İşçi, Mühendis…). Ekranda yalnız bunlar görünür; planlamacı ekler/kaldırır.</summary>
    public List<WorkTeamGroup> TeamGroups { get; set; } = [];
}

/// <summary>İşe özel ekip alanı: "bu işte bir de mühendis çalışacak" gibi. Boş da olsa iş ile birlikte saklanır.</summary>
public class WorkTeamGroup
{
    public int Id { get; set; }
    public int WorkOrderId { get; set; }
    public int JobRoleId { get; set; }
    public JobRole JobRole { get; set; } = null!;
    public int SortOrder { get; set; }
}

/// <summary>
/// Personel ↔ iş ilişkisi. Satır hiç silinmez: atama kaldırılınca veya personel reddedince ReleasedAtUtc dolar.
/// Ekipte sayılan (kişiyi meşgul eden) atama = ReleasedAtUtc boş olan atama.
/// </summary>
public class WorkAssignment
{
    public int Id { get; set; }
    public int WorkOrderId { get; set; }
    public WorkOrder WorkOrder { get; set; } = null!;
    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    /// <summary>Bu işteki görev rolü (personelin varsayılan unvanından farklı olabilir).</summary>
    public int JobRoleId { get; set; }
    public JobRole JobRole { get; set; } = null!;

    public AssignmentStatus AssignmentStatus { get; set; } = AssignmentStatus.Assigned;
    public string? TaskNote { get; set; }

    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RespondedAtUtc { get; set; }
    public DateTime? InstructionAcknowledgedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? RejectionReason { get; set; }

    public DateTime? ReleasedAtUtc { get; set; }
    public string? ReleaseReason { get; set; }

    public string AssignedByUserId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = [];

    public bool IsActive => ReleasedAtUtc is null;
}

public class WorkStatusHistory
{
    public long Id { get; set; }
    public int WorkOrderId { get; set; }
    public WorkStatus FromStatus { get; set; }
    public WorkStatus ToStatus { get; set; }
    public string ChangedByUserId { get; set; } = "";
    public string? Reason { get; set; }
    public DateTime ChangedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
}

/// <summary>Notlar silinmez ve düzenlenmez; düzeltme gerekirse yeni not eklenir.</summary>
public class WorkNote
{
    public int Id { get; set; }
    public int WorkOrderId { get; set; }
    public string AuthorUserId { get; set; } = "";
    public string NoteText { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EditedAtUtc { get; set; }
}

/// <summary>Fotoğraf / dosya üst verisi. Dosyanın kendisi web kökü dışındaki depoda, rastgele adla durur.</summary>
public class WorkAttachment
{
    public int Id { get; set; }
    public int WorkOrderId { get; set; }
    public string UploadedByUserId { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string? Caption { get; set; }
    public AttachmentType AttachmentType { get; set; } = AttachmentType.Photo;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Kaldırılan dosyalar listeden düşer ama kaydı ve geçmişi korunur.</summary>
    public DateTime? DeletedAtUtc { get; set; }
    public string? DeletedByUserId { get; set; }

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.Ordinal);
}

/// <summary>İşte bildirilen malzeme tüketimi. Stok tutulmaz; katalog sonradan değişse de ad/birim anlık görüntüsü kalır.</summary>
public class WorkMaterial
{
    public int Id { get; set; }
    public int WorkOrderId { get; set; }
    public int MaterialCatalogId { get; set; }
    public MaterialCatalog MaterialCatalog { get; set; } = null!;
    public string MaterialNameSnapshot { get; set; } = "";
    public string UnitSnapshot { get; set; } = "";
    public decimal Quantity { get; set; }
    public string AddedByUserId { get; set; } = "";
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}

public class MaterialCatalog
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public string Unit { get; set; } = "adet";
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
