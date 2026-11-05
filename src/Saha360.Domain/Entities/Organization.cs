namespace Saha360.Domain.Entities;

/// <summary>Tanım tablolarının ortak alanları. Tanımlar silinmez, pasife alınır.</summary>
public abstract class DefinitionEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Birim / müdürlük.</summary>
public class OrganizationUnit : DefinitionEntity
{
    public string? Code { get; set; }
}

/// <summary>Ekip veya kanal.</summary>
public class Team : DefinitionEntity
{
    public int OrganizationUnitId { get; set; }
    public OrganizationUnit OrganizationUnit { get; set; } = null!;

    /// <summary>Ekibin şefi (personel kaydı).</summary>
    public int? SupervisorEmployeeId { get; set; }
    public Employee? SupervisorEmployee { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// İşteki görev / unvan (Şef, İşçi, Kanal Personeli…). Kod içinde sabit değil; kurum değiştirebilir.
/// Personel havuzu ekranında atama grupları bu tablodan gelir.
/// </summary>
public class JobRole : DefinitionEntity
{
    /// <summary>Bu roldeki kişi işin ekip lideri/şefi sayılır (atama ekranında ilk grupta gösterilir).</summary>
    public bool IsLeadRole { get; set; }
    public int SortOrder { get; set; }

    /// <summary>
    /// Kısıtlamasız alan (ör. Şoför): unvanı ne olursa olsun her personel bu göreve atanabilir.
    /// Kısıtlamalı alanlara yalnız unvanı bu olan personel atanır (Kanal Personeli "Şef" alanına konamaz).
    /// </summary>
    public bool IsOpenToAll { get; set; }
}

public class WorkCategory : DefinitionEntity;

/// <summary>İş bölgesi / saha.</summary>
public class WorkLocation : DefinitionEntity;
