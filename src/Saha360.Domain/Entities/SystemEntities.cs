using Saha360.Domain.Enums;

namespace Saha360.Domain.Entities;

/// <summary>Uygulama içi bildirim. Aynı olay için ikinci kez oluşmasın diye (alıcı, tekilleştirme anahtarı) benzersizdir.</summary>
public class Notification
{
    public long Id { get; set; }
    public string RecipientUserId { get; set; } = "";
    public int? WorkOrderId { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public NotificationType NotificationType { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAtUtc { get; set; }
    public string? DeduplicationKey { get; set; }
}

/// <summary>Rol → izin tablosu (Yetkiler ekranı). Her (rol, izin) çifti için bir satır: verildi mi?</summary>
public class RolePermission
{
    public int Id { get; set; }
    public string RoleName { get; set; } = "";
    public string Permission { get; set; } = "";
    public bool Granted { get; set; }
}

/// <summary>Önemli işlemlerin değiştirilemez denetim kaydı. Parola, belge içeriği gibi hassas veri yazılmaz.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public string? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";

    /// <summary>İşlem geçmişi ekranında gösterilen Türkçe özet.</summary>
    public string Summary { get; set; } = "";

    /// <summary>İş kaydıyla ilgiliyse iş numarası (iş geçmişi sekmesi bu alandan süzer).</summary>
    public int? WorkOrderId { get; set; }

    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string? DetailsJson { get; set; }
}
