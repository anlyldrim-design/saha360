using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Domain.Rules;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Services;

namespace Saha360.Infrastructure.Persistence;

/// <summary>
/// Rolleri oluşturur; ilk yönetici hesabını yapılandırmadan (ortam değişkeni / user-secrets) kurar.
/// Kod içinde gömülü varsayılan yönetici veya parola yoktur. Tanıtım verisi yalnız açıkça istenirse yüklenir.
/// </summary>
public class DataSeeder(AppDbContext db, UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles,
    WorkOrderService works, AssignmentService assignments, WorkflowService workflow, WorkContentService content,
    ILogger<DataSeeder> logger)
{
    public async Task EnsureRolesAsync() => await UserAdminService.EnsureRolesAsync(roles);

    /// <summary>Hiç sistem yöneticisi yoksa verilen bilgilerle ilk yöneticiyi oluşturur.</summary>
    public async Task EnsureBootstrapAdminAsync(string? userName, string? password, string? fullName)
    {
        if ((await users.GetUsersInRoleAsync(SystemRoles.SystemAdmin)).Count > 0) return;
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Henüz sistem yöneticisi yok. Bootstrap:AdminUserName ve Bootstrap:AdminPassword ayarlarını " +
                              "(ortam değişkeni veya user-secrets) verip uygulamayı yeniden başlatın. Ayrıntı: README.");
            return;
        }
        var u = await users.FindByNameAsync(userName) ?? new ApplicationUser { UserName = userName, FullName = fullName ?? "Sistem Yöneticisi" };
        if (u.Id is null || await users.FindByIdAsync(u.Id) is null)
        {
            var r = await users.CreateAsync(u, password);
            if (!r.Succeeded) throw new InvalidOperationException("İlk yönetici oluşturulamadı: " + string.Join(" ", r.Errors.Select(e => e.Description)));
        }
        await users.AddToRoleAsync(u, SystemRoles.SystemAdmin);
        logger.LogInformation("İlk sistem yöneticisi oluşturuldu: {User}. Parolayı ilk girişten sonra değiştirin.", userName);
    }

    /// <summary>Boş veritabanına tanıtım verisi: birimler, ekipler, personel, kullanıcılar, malzemeler ve farklı durumlarda işler.</summary>
    public async Task SeedDemoAsync(string password)
    {
        if (await db.Employees.AnyAsync()) return;
        logger.LogInformation("Tanıtım verisi yükleniyor…");

        var su = new OrganizationUnit { Name = "Su ve Kanalizasyon Müdürlüğü", Code = "SKM" };
        var fen = new OrganizationUnit { Name = "Fen İşleri Müdürlüğü", Code = "FİM" };
        db.OrganizationUnits.AddRange(su, fen);

        var sef = new JobRole { Name = "Şef", IsLeadRole = true, SortOrder = 1 };
        var isci = new JobRole { Name = "İşçi", SortOrder = 2 };
        var kanal = new JobRole { Name = "Kanal Personeli", SortOrder = 3 };
        var operator_ = new JobRole { Name = "Operatör", SortOrder = 4 };
        // Şoför kısıtlamasız: unvanı ne olursa olsun her personel bu göreve atanabilir.
        var sofor = new JobRole { Name = "Şoför", SortOrder = 5, IsOpenToAll = true };
        var muhendis = new JobRole { Name = "Mühendis", SortOrder = 6 };
        db.JobRoles.AddRange(sef, isci, kanal, operator_, sofor, muhendis);

        var cats = new[] { "Arıza onarım", "Kanal temizliği", "Periyodik bakım", "Yeni hat döşeme", "Yol ve kaldırım" }
            .Select(n => new WorkCategory { Name = n }).ToArray();
        db.WorkCategories.AddRange(cats);
        var locs = new[] { "Merkez", "Kuzey Bölge", "Sahil", "Sanayi Sitesi", "Güney Bölge" }.Select(n => new WorkLocation { Name = n }).ToArray();
        db.WorkLocations.AddRange(locs);

        db.MaterialCatalog.AddRange(
            new MaterialCatalog { Code = "BRU-110", Name = "PVC boru Ø110", Category = "Boru", Unit = "metre" },
            new MaterialCatalog { Code = "BRU-200", Name = "Koruge boru Ø200", Category = "Boru", Unit = "metre" },
            new MaterialCatalog { Code = "DIR-110", Name = "PVC dirsek Ø110", Category = "Bağlantı", Unit = "adet" },
            new MaterialCatalog { Code = "KLP-01", Name = "Kanal kapağı (döküm)", Category = "Kapak", Unit = "adet" },
            new MaterialCatalog { Code = "CMT-50", Name = "Çimento (50 kg)", Category = "Yapı", Unit = "torba" },
            new MaterialCatalog { Code = "KUM-01", Name = "Kum", Category = "Yapı", Unit = "m³" },
            new MaterialCatalog { Code = "ASF-01", Name = "Soğuk asfalt", Category = "Yol", Unit = "kilogram" },
            new MaterialCatalog { Code = "CNT-01", Name = "Conta seti", Category = "Bağlantı", Unit = "adet" },
            new MaterialCatalog { Code = "IKZ-01", Name = "İkaz bandı", Category = "Güvenlik", Unit = "metre" },
            new MaterialCatalog { Code = "VAN-80", Name = "Sürgülü vana Ø80", Category = "Vana", Unit = "adet" });
        await db.SaveChangesAsync();

        Employee Emp(string first, string last, string no, OrganizationUnit unit, JobRole role, string phone) =>
            new() { FirstName = first, LastName = last, EmployeeNumber = no, OrganizationUnit = unit, JobRole = role, Phone = phone };

        var mehmet = Emp("Mehmet", "Yılmaz", "S-1001", su, sef, "0532 000 10 01");
        var ayse = Emp("Ayşe", "Kaya", "S-1002", su, sef, "0532 000 10 02");
        var kemal = Emp("Kemal", "Arslan", "F-2001", fen, sef, "0532 000 20 01");
        db.Employees.AddRange(mehmet, ayse, kemal);
        await db.SaveChangesAsync();

        var t1 = new Team { Name = "Kanal Ekibi 1", OrganizationUnit = su, SupervisorEmployee = mehmet };
        var t2 = new Team { Name = "Kanal Ekibi 2", OrganizationUnit = su, SupervisorEmployee = ayse };
        var t3 = new Team { Name = "Onarım Ekibi", OrganizationUnit = su, SupervisorEmployee = mehmet };
        var t4 = new Team { Name = "Yol Bakım Ekibi", OrganizationUnit = fen, SupervisorEmployee = kemal };
        db.Teams.AddRange(t1, t2, t3, t4);
        mehmet.Team = t1; ayse.Team = t2; kemal.Team = t4;

        var staff = new List<Employee>
        {
            Emp("Ali", "Demir", "S-1101", su, kanal, "0533 000 11 01"),
            Emp("Fatma", "Çelik", "S-1102", su, isci, "0533 000 11 02"),
            Emp("Hasan", "Öztürk", "S-1103", su, kanal, "0533 000 11 03"),
            Emp("Zeynep", "Aydın", "S-1104", su, isci, "0533 000 11 04"),
            Emp("Murat", "Şahin", "S-1105", su, operator_, "0533 000 11 05"),
            Emp("Emre", "Koç", "S-1106", su, kanal, "0533 000 11 06"),
            Emp("Elif", "Kurt", "S-1107", su, isci, "0533 000 11 07"),
            Emp("Burak", "Özdemir", "S-1108", su, sofor, "0533 000 11 08"),
            Emp("Selin", "Polat", "S-1109", su, kanal, "0533 000 11 09"),
            Emp("Oğuz", "Erdoğan", "S-1110", su, isci, "0533 000 11 10"),
            Emp("Cem", "Güneş", "S-1111", su, operator_, "0533 000 11 11"),
            Emp("Derya", "Tekin", "S-1112", su, isci, "0533 000 11 12"),
            Emp("Deniz", "Aksu", "S-1113", su, muhendis, "0533 000 11 13"),
            Emp("Serkan", "Aksoy", "F-2101", fen, isci, "0533 000 21 01"),
            Emp("Gül", "Yıldız", "F-2102", fen, operator_, "0533 000 21 02"),
        };
        for (var i = 0; i < staff.Count; i++)
        {
            var e = staff[i];
            e.Team = e.OrganizationUnit == fen ? t4 : (i % 3) switch { 0 => t1, 1 => t2, _ => t3 };
            e.SupervisorEmployee = e.Team.SupervisorEmployee;
        }
        staff.Add(new Employee { FirstName = "Yusuf", LastName = "Bulut", EmployeeNumber = "S-1199", OrganizationUnit = su, JobRole = isci, Team = t1, IsActive = false });
        db.Employees.AddRange(staff);
        await db.SaveChangesAsync();

        async Task<ApplicationUser> User(string userName, string fullName, string role, Employee? emp = null, OrganizationUnit? unit = null)
        {
            var u = new ApplicationUser { UserName = userName, FullName = fullName, Email = $"{userName}@ornek.local", EmailConfirmed = true, OrganizationUnitId = unit?.Id };
            var r = await users.CreateAsync(u, password);
            if (!r.Succeeded) throw new InvalidOperationException(string.Join(" ", r.Errors.Select(e => e.Description)));
            await users.AddToRoleAsync(u, role);
            if (emp is not null) emp.ApplicationUserId = u.Id;
            return u;
        }

        var admin = await users.FindByNameAsync("admin") ?? await User("admin", "Sistem Yöneticisi", SystemRoles.SystemAdmin);
        if (!await users.IsInRoleAsync(admin, SystemRoles.SystemAdmin)) await users.AddToRoleAsync(admin, SystemRoles.SystemAdmin);
        var mudur = await User("mudur", "Nuri Akın (Birim Müdürü)", SystemRoles.UnitManager, unit: su);
        var planlama = await User("planlama", "Pınar Şen (Planlamacı)", SystemRoles.Planner, unit: su);
        await User("sef.mehmet", "Mehmet Yılmaz", SystemRoles.Supervisor, mehmet);
        await User("sef.ayse", "Ayşe Kaya", SystemRoles.Supervisor, ayse);
        await User("ali", "Ali Demir", SystemRoles.Personnel, staff[0]);
        await User("fatma", "Fatma Çelik", SystemRoles.Personnel, staff[1]);
        await User("hasan", "Hasan Öztürk", SystemRoles.Personnel, staff[2]);
        await User("rapor", "Rapor Görüntüleyici", SystemRoles.ReportViewer, unit: su);
        await db.SaveChangesAsync();

        // Bugün izinli bir personel (havuzda "İzinli" görünür).
        db.EmployeeAvailabilities.Add(new EmployeeAvailability
        {
            EmployeeId = staff[6].Id, Type = AvailabilityType.Leave, StartAtUtc = TrTime.ToUtc(TrTime.TodayLocal),
            EndAtUtc = TrTime.ToUtc(TrTime.TodayLocal.AddDays(3)), Note = "Yıllık izin", CreatedByUserId = planlama.Id
        });
        await db.SaveChangesAsync();

        // ── Örnek işler: gerçek servisler üzerinden (geçmiş, bildirim ve kurallar gerçek akışla oluşur) ──
        var planner = Ctx(planlama, [SystemRoles.Planner], null, su.Id);
        var sefCtx = Ctx(await users.FindByNameAsync("sef.mehmet"), [SystemRoles.Supervisor], mehmet.Id, null);
        UserContext P(int i) => Ctx(users.FindByIdAsync(staff[i].ApplicationUserId!).GetAwaiter().GetResult(), [SystemRoles.Personnel], staff[i].Id, null);
        var today = TrTime.TodayLocal;
        var mats = await db.MaterialCatalog.ToDictionaryAsync(m => m.Code, m => m.Id);

        WorkOrderInput Input(string title, int dayOffset, int startHour, int hours, Category c, int loc, WorkPriority p, string? address, string? instructions = null) => new()
        {
            Title = title, Description = $"{title} — örnek iş kaydı.", Instructions = instructions, WorkCategoryId = cats[(int)c].Id,
            WorkLocationId = locs[loc].Id, LocationText = address, Priority = p, ResponsibleUnitId = su.Id,
            ResponsibleSupervisorEmployeeId = mehmet.Id, ManagerUserId = mudur.Id,
            PlannedStartLocal = today.AddDays(dayOffset).AddHours(startHour), PlannedEndLocal = today.AddDays(dayOffset).AddHours(startHour + hours)
        };
        AssignmentInput A(Employee e, JobRole r) => new(e.Id, r.Id, null);

        // 1) Tamamlanmış iş (dün)
        var w1 = await works.CreateAsync(planner, Input("Atatürk Cad. kanal tıkanıklığı açılması", -1, 8, 6, Category.Kanal, 0, WorkPriority.High,
            "Atatürk Cad. No: 45 önü"), [new(mats["IKZ-01"], 20, null)]);
        await assignments.SaveAsync(planner, w1.Id, [A(mehmet, sef), A(staff[0], kanal), A(staff[3], isci)], true, null);
        await workflow.AcceptAsync(sefCtx, w1.Id);
        await workflow.AcceptAsync(P(0), w1.Id);
        await workflow.StartAsync(P(0), w1.Id);
        await content.AddNoteAsync(P(0), w1.Id, "Tıkanıklık 12 m ileride kök birikmesinden kaynaklanıyor; temizlendi.");
        await workflow.SubmitCompletionAsync(P(0), w1.Id, "Hat açıldı, akış normal. Kapak yerine oturtuldu.");
        await workflow.ApproveAsync(sefCtx, w1.Id, "Kontrol edildi, uygun.");

        // 2) Devam eden iş (bugün)
        var w2 = await works.CreateAsync(planner, Input("Sahil yolu su borusu arızası", 0, 8, 9, Category.Ariza, 2, WorkPriority.Urgent,
            "Sahil yolu, iskele karşısı", "Kazı öncesi ikaz bandı çekin; vana kapatılmadan boru kesilmez."),
            [new(mats["BRU-110"], 6, null), new(mats["DIR-110"], 2, null)]);
        await assignments.SaveAsync(planner, w2.Id, [A(mehmet, sef), A(staff[2], kanal), A(staff[4], operator_), A(staff[7], sofor)], true, null);
        await workflow.AcceptAsync(sefCtx, w2.Id);
        await workflow.AcceptAsync(P(2), w2.Id);
        await workflow.StartAsync(P(2), w2.Id);

        // 3) Kabul bekleyen iş (yarın) — Fatma'ya atanmış, yanıt bekliyor
        var w3 = await works.CreateAsync(planner, Input("Kuzey bölge periyodik rögar bakımı", 1, 9, 7, Category.Bakim, 1, WorkPriority.Normal,
            "Kuzey bölge 3. sokak", "Rögar kapakları açıkken bölge güvenlik bandıyla çevrilecek."));
        await assignments.SaveAsync(planner, w3.Id, [A(ayse, sef), A(staff[1], isci), A(staff[5], kanal)], true, null);

        // 4) Şef onayı bekleyen iş (bugün sabah)
        var w4 = await works.CreateAsync(planner, Input("Sanayi sitesi kanal kapağı değişimi", 0, 7, 3, Category.Ariza, 3, WorkPriority.High,
            "Sanayi Sitesi 5. blok"), [new(mats["KLP-01"], 2, null), new(mats["CMT-50"], 3, null)]);
        await assignments.SaveAsync(planner, w4.Id, [A(staff[0], kanal), A(staff[9], isci)], true, null);
        await workflow.AcceptAsync(P(0), w4.Id);
        await workflow.StartAsync(P(0), w4.Id);
        await workflow.SubmitCompletionAsync(P(0), w4.Id, "İki kapak değiştirildi, çevresi betonlandı.");

        // 5) Taslak iş (gelecek hafta) — ekip atanmamış
        await works.CreateAsync(planner, Input("Güney bölge yeni atık su hattı döşenmesi", 6, 8, 9, Category.YeniHat, 4, WorkPriority.Normal,
            "Güney bölge 12. cadde"));

        // 6) Personelin reddettiği iş (2 gün sonra)
        var w6 = await works.CreateAsync(planner, Input("Merkez çarşı yağmur suyu ızgara temizliği", 2, 8, 4, Category.Kanal, 0, WorkPriority.Low,
            "Çarşı içi"));
        await assignments.SaveAsync(planner, w6.Id, [A(staff[2], kanal)], true, null);
        await workflow.RejectAsync(P(2), w6.Id, "O gün sağlık raporum var, katılamayacağım.");

        // 7) Gecikmiş iş (3 gün önce bitmesi gerekiyordu, hâlâ devam ediyor)
        var w7 = await works.CreateAsync(planner, Input("Sahil parkı sulama hattı onarımı", -3, 9, 5, Category.Ariza, 2, WorkPriority.Normal,
            "Sahil parkı"));
        await assignments.SaveAsync(planner, w7.Id, [A(staff[1], isci), A(staff[10], operator_)], true, null);
        await workflow.AcceptAsync(P(1), w7.Id);
        await workflow.StartAsync(P(1), w7.Id);

        logger.LogInformation("Tanıtım verisi yüklendi.");
    }

    private enum Category { Ariza = 0, Kanal = 1, Bakim = 2, YeniHat = 3, Yol = 4 }

    private static UserContext Ctx(ApplicationUser? u, string[] roles, int? employeeId, int? unitId) =>
        new(u!.Id, u.FullName, roles.ToHashSet(), employeeId, unitId);
}
