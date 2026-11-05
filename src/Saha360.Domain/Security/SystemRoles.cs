namespace Saha360.Domain.Security;

/// <summary>
/// Uygulamaya giriş yetkisini belirleyen sistem rolleri. İş içindeki görev rolleriyle (Şef, İşçi, Kanal Personeli…)
/// karıştırılmamalı; onlar <see cref="Entities.JobRole"/> tablosunda tanımlanabilir veridir.
/// </summary>
public static class SystemRoles
{
    public const string SystemAdmin = "SistemYoneticisi";
    public const string UnitManager = "BirimYoneticisi";
    public const string Planner = "Planlamaci";
    public const string Supervisor = "Sef";
    public const string Personnel = "Personel";
    public const string ReportViewer = "RaporGoruntuleyici";

    public static readonly string[] All = [SystemAdmin, UnitManager, Planner, Supervisor, Personnel, ReportViewer];

    public static string DisplayName(string role) => role switch
    {
        SystemAdmin => "Sistem yöneticisi",
        UnitManager => "Birim yöneticisi",
        Planner => "Planlamacı / İş koordinatörü",
        Supervisor => "Şef",
        Personnel => "Personel",
        ReportViewer => "Rapor görüntüleyici",
        _ => role
    };
}
