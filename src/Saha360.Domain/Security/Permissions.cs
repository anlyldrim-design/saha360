namespace Saha360.Domain.Security;

/// <summary>
/// Uygulamadaki tüm izinler. Hangi sistem rolünün hangi izne sahip olduğu veritabanında (RolePermissions) tutulur ve
/// sistem yöneticisi "Yetkiler" ekranından değiştirir. Burada yalnız ilk kurulumdaki varsayılanlar var.
/// Sistem yöneticisi her zaman tüm izinlere sahiptir (kilitlenme olmasın diye değiştirilemez).
///
/// İzin "ne yapılabileceğini", birim kapsamı "hangi verinin" görüleceğini belirler: izinler yalnız kullanıcının birimi
/// içinde geçerlidir; "Tüm birimler" izni (veya kullanıcıya özel tüm birimler ayarı) olan herkesi görür.
/// </summary>
public static class Permissions
{
    // İşler
    public const string WorksViewAll = "is.goruntule";
    public const string WorksCreate = "is.olustur";
    public const string WorksEdit = "is.duzenle";
    public const string WorksAssign = "is.ekipata";
    public const string WorksCancel = "is.iptal";
    public const string WorksReopen = "is.yenidenac";
    public const string WorksApproveAny = "is.onay";
    // Personel ve organizasyon
    public const string PersonnelView = "personel.goruntule";
    public const string PersonnelManage = "personel.yonet";
    public const string LeaveManage = "izin.yonet";
    public const string TeamsManage = "ekip.yonet";
    // Tanımlar
    public const string DefinitionsManage = "tanim.yonet";
    public const string MaterialsManage = "malzeme.yonet";
    // Raporlar
    public const string ReportsView = "rapor.goruntule";
    public const string ReportsExport = "rapor.disaaktar";
    // Sistem
    public const string UsersManage = "kullanici.yonet";
    public const string AuditView = "denetim.goruntule";
    public const string AllUnits = "kapsam.tumbirimler";

    public record Info(string Key, string Group, string Label, string Description, string[] DefaultRoles);

    private static readonly string[] Managers = [SystemRoles.UnitManager];
    private static readonly string[] Planning = [SystemRoles.UnitManager, SystemRoles.Planner];

    public static readonly Info[] All =
    [
        new(WorksViewAll, "İşler", "Birimin tüm işlerini görme",
            "Kapalıysa kişi yalnız kendisine atanan, sorumlu şefi olduğu veya kendi açtığı işleri görür.",
            [SystemRoles.UnitManager, SystemRoles.Planner, SystemRoles.ReportViewer]),
        new(WorksCreate, "İşler", "Yeni iş oluşturma", "Yeni İş Oluştur ekranı.", Planning),
        new(WorksEdit, "İşler", "İş bilgilerini ve planını düzenleme", "Başlık, tarih, konum, sorumlu şef vb.", Planning),
        new(WorksAssign, "İşler", "Ekip atama ve personele gönderme", "İş Programı / Havuz ekranı, ekip alanları, işi personele gönderme.", Planning),
        new(WorksCancel, "İşler", "İşi iptal etme", "Gerekçe zorunludur; ekip serbest kalır.", Planning),
        new(WorksReopen, "İşler", "Tamamlanan / iptal edilen işi yeniden açma", "Gerekçe zorunludur; önceki ekip geri atanabilir.", Planning),
        new(WorksApproveAny, "İşler", "Sorumlu şefi olmadığı işi de onaylama / iade etme",
            "İşin sorumlu şefi her zaman onay verebilir; bu izin, şef yokken onay için yedek yetkidir.", Managers),

        new(PersonnelView, "Personel", "Personel listesi, havuz, ekipler ve organizasyonu görme", "Sicil no ve telefon da görünür.", Planning),
        new(PersonnelManage, "Personel", "Personel ekleme, düzenleme, pasife alma", "Excel ile toplu personel yükleme dahil.", Managers),
        new(LeaveManage, "Personel", "Müsaitlik / izin kaydı ekleme ve kaldırma", "İzinli personel o aralıkta işe atanamaz.", Managers),
        new(TeamsManage, "Personel", "Ekip oluşturma, ekip şefi ve üyeleri yönetme", "Ekipler ve Şefler ekranı.", Managers),

        new(DefinitionsManage, "Tanımlar", "Birim, unvan, kategori ve bölge tanımlarını yönetme", "Tanımlar ekranı ve toplu yükleme.", Managers),
        new(MaterialsManage, "Tanımlar", "Malzeme kataloğunu yönetme", "Malzeme ekleme, düzenleme, toplu yükleme.", Planning),

        new(ReportsView, "Raporlar", "Raporları görme", "Yalnız kapsamındaki işler.",
            [SystemRoles.UnitManager, SystemRoles.Planner, SystemRoles.ReportViewer]),
        new(ReportsExport, "Raporlar", "Rapor ve iş listesini CSV / Excel'e aktarma", "Dışa aktarılan dosya da kapsamla sınırlıdır.",
            [SystemRoles.UnitManager, SystemRoles.Planner, SystemRoles.ReportViewer]),

        new(UsersManage, "Sistem", "Kullanıcı hesaplarını yönetme",
            "Hesap açma, rol verme, parola sıfırlama. Sistem yöneticisi rolünü yalnız sistem yöneticisi verebilir.", []),
        new(AuditView, "Sistem", "Sistem bilgisi ve denetim kaydını görme", "", []),
        new(AllUnits, "Sistem", "Tüm birimlerin verisini görme",
            "Kapalıysa kişi yalnız kendi biriminin (kullanıcı hesabındaki birim ya da personel kaydındaki birim) verisini görür.", []),
    ];

    public static readonly string[] Keys = All.Select(p => p.Key).ToArray();

    /// <summary>Roller için varsayılan izinler (veritabanı tablosu henüz yoksa ya da testlerde).</summary>
    public static HashSet<string> DefaultsFor(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet();
        if (set.Contains(SystemRoles.SystemAdmin)) return Keys.ToHashSet();
        return All.Where(p => p.DefaultRoles.Any(set.Contains)).Select(p => p.Key).ToHashSet();
    }
}
