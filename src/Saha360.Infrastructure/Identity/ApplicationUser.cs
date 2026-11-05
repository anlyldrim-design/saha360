using Microsoft.AspNetCore.Identity;

namespace Saha360.Infrastructure.Identity;

/// <summary>Uygulamaya giriş yapan kullanıcı. Personel kaydıyla eşlemesi Employee.ApplicationUserId üzerindedir.</summary>
public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = "";

    /// <summary>Pasif kullanıcı giriş yapamaz; açık oturumu en geç 1 dakikada düşer.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Kullanıcının kapsam birimi. Boşsa bağlı personel kaydının birimi kullanılır; o da yoksa birim verisi görmez.</summary>
    public int? OrganizationUnitId { get; set; }

    /// <summary>Kullanıcıya özel: rolünde "tüm birimler" izni olmasa da tüm birimlerin verisini görsün.</summary>
    public bool AllUnits { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }
}
