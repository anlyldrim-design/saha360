using System.Globalization;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

public record ImportSheetResult(string Sheet, int Added, int Updated);
public record ImportError(string Sheet, int Row, string Message);

public class ImportResult
{
    public List<ImportSheetResult> Sheets { get; } = [];
    public List<ImportError> Errors { get; } = [];
    public bool Committed { get; set; }
    public int TotalRows => Sheets.Sum(s => s.Added + s.Updated);
}

/// <summary>
/// Excel ile toplu yükleme. Tek şablon dosyası, her tür için bir sayfa. Kayıtlar anahtarına göre eklenir ya da güncellenir
/// (birim/unvan/kategori/bölge: ad · malzeme: kod · ekip: birim+ad · personel: sicil no · kullanıcı: kullanıcı adı),
/// böylece aynı dosya tekrar yüklenince çoğaltma olmaz. Hepsi ya da hiçbiri: bir satırda hata varsa hiçbir şey kaydedilmez.
/// </summary>
public class ImportService(AppDbContext db, UserManager<ApplicationUser> users, AuditService audit)
{
    internal AppDbContext Db => db;
    internal UserManager<ApplicationUser> Users => users;

    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxRowsPerSheet = 5000;
    private static readonly CultureInfo Tr = new("tr-TR");

    // ───────────────────────── Şablon tanımı ─────────────────────────

    public record Column(string Header, bool Required, string Note, bool IsBool = false, string? ListFrom = null);
    public record Sheet(string Name, string Purpose, Column[] Columns);

    public const string SUnits = "Birimler", SRoles = "Unvanlar", SCategories = "Kategoriler", SLocations = "Bölgeler",
        SMaterials = "Malzemeler", STeams = "Ekipler", SEmployees = "Personel", SUsers = "Kullanıcılar";

    public static readonly Sheet[] Sheets =
    [
        new(SUnits, "Birim / müdürlükler", [
            new("Ad", true, "Birimin adı. Aynı adla kayıt varsa güncellenir."),
            new("Kod", false, "Kısa kod (isteğe bağlı), ör. SKM."),
            new("Aktif", false, "Evet / Hayır. Boşsa Evet.", IsBool: true)]),
        new(SRoles, "Unvanlar = iş ekranındaki ekip alanları (Şef, İşçi, Mühendis, Şoför…)", [
            new("Ad", true, "Unvan adı, ör. Tekniker."),
            new("Herkes atanabilir", false, "Hayır (varsayılan): bu alana yalnız unvanı bu olan personel atanır. Evet: unvan şartı yok, " +
                "herkes atanabilir (ör. Şoför: işçi de tekniker de şoför olabilir).", IsBool: true),
            new("Ekip lideri", false, "Evet: işi sahada yöneten rol (ör. Şef). Ekip ekranında en üstte ve yeni işte hazır açık gelir; " +
                "ekipte lider yoksa uyarı verilir. Onay yetkisi vermez.", IsBool: true),
            new("Sıra", false, "Boş bırakın: listenin sonuna eklenir. Sayı yazarsanız o sıraya yerleşir."),
            new("Aktif", false, "Evet / Hayır. Boşsa Evet.", IsBool: true)]),
        new(SCategories, "İş kategorileri", [
            new("Ad", true, "ör. Arıza onarım."),
            new("Aktif", false, "Evet / Hayır. Boşsa Evet.", IsBool: true)]),
        new(SLocations, "Bölgeler / sahalar", [
            new("Ad", true, "ör. Kuzey Bölge."),
            new("Aktif", false, "Evet / Hayır. Boşsa Evet.", IsBool: true)]),
        new(SMaterials, "Malzeme kataloğu (stok tutulmaz)", [
            new("Kod", true, "Benzersiz malzeme kodu. Aynı kodla kayıt varsa güncellenir."),
            new("Ad", true, "Malzeme adı."),
            new("Ölçü birimi", true, "adet, metre, kilogram, litre, m³, torba…"),
            new("Kategori", false, "Serbest metin, ör. Boru."),
            new("Açıklama", false, ""),
            new("Aktif", false, "Evet / Hayır. Boşsa Evet.", IsBool: true)]),
        new(STeams, "Ekipler", [
            new("Ad", true, "Ekip adı. Aynı birimde aynı adla kayıt varsa güncellenir."),
            new("Birim", true, "Birimler sayfasındaki veya sistemdeki birim adı.", ListFrom: SUnits),
            new("Şef sicil no", false, "Ekibin şefinin sicil numarası (Personel sayfasında veya sistemde olmalı)."),
            new("Açıklama", false, ""),
            new("Aktif", false, "Evet / Hayır. Boşsa Evet.", IsBool: true)]),
        new(SEmployees, "Personel (uygulama hesabı olması gerekmez)", [
            new("Sicil no", true, "Kurum sicil numarası; benzersiz. Aynı sicil varsa kayıt güncellenir."),
            new("Ad", true, ""),
            new("Soyad", true, ""),
            new("Birim", true, "Birim adı.", ListFrom: SUnits),
            new("Ekip", false, "Ekip adı (aynı birimde).", ListFrom: STeams),
            new("Unvan", false, "Unvan adı; personel iş ekranında yalnız bu unvanın alanına (ve 'herkes atanabilir' alanlara) atanır.", ListFrom: SRoles),
            new("Bağlı şef sicil no", false, "Bağlı olduğu şefin sicil numarası."),
            new("Telefon", false, "Kişisel veri; gerekiyorsa girin."),
            new("Aktif", false, "Evet / Hayır. Boşsa Evet. Pasif personel işe atanamaz.", IsBool: true)]),
        new(SUsers, "Uygulamaya giriş yapacak kullanıcılar", [
            new("Kullanıcı adı", true, "İngilizce harf, rakam, nokta, tire, @. Aynı ad varsa güncellenir."),
            new("Ad soyad", true, ""),
            new("E-posta", false, ""),
            new("Parola", false, "Yeni kullanıcı için zorunlu (en az 8 karakter, bir rakam). Mevcut kullanıcıda doluysa parola sıfırlanır. Yüklemeden sonra dosyayı silin."),
            new("Roller", true, "Virgülle ayırın: Sistem yöneticisi, Birim yöneticisi, Planlamacı, Şef, Personel, Rapor görüntüleyici."),
            new("Personel sicil no", false, "Şef ve Personel rolü için zorunlu: hesabın bağlanacağı personel."),
            new("Birim kapsamı", false, "Birim yöneticisi / planlamacı / rapor görüntüleyici yalnız bu birimi görür. Boşsa tüm birimler.", ListFrom: SUnits),
            new("Aktif", false, "Evet / Hayır. Boşsa Evet.", IsBool: true)]),
    ];

    /// <summary>Sayfaya özel toplu ekleme adresleri (/toplu-yukleme/{kısa ad}).</summary>
    public static readonly Dictionary<string, string> Slugs = new()
    {
        ["birimler"] = SUnits, ["unvanlar"] = SRoles, ["kategoriler"] = SCategories, ["bolgeler"] = SLocations,
        ["malzemeler"] = SMaterials, ["ekipler"] = STeams, ["personel"] = SEmployees, ["kullanicilar"] = SUsers
    };

    public static string SlugOf(string sheet) => Slugs.First(kv => kv.Value == sheet).Key;

    /// <summary>Kim hangi listeyi toplu yükleyebilir: tanımlar yönetici, personel/malzeme planlamacı, kullanıcılar sistem yöneticisi.</summary>
    public static bool CanImport(UserContext u, string sheet) => sheet switch
    {
        SUsers => u.CanManageUsers,
        SEmployees => u.CanManagePersonnel,
        STeams => u.CanManageTeams,
        SMaterials => u.CanManageMaterials,
        _ => u.CanManageDefinitions
    };

    public static IEnumerable<Sheet> AllowedSheets(UserContext u) => Sheets.Where(s => CanImport(u, s.Name));

    /// <summary>
    /// Boş şablon (veya mevcut kayıtlarla dolu şablon: düzenleyip geri yüklemek için).
    /// <paramref name="only"/> verilirse yalnız o listenin sayfası olur (ör. yalnız Kullanıcılar).
    /// </summary>
    public async Task<byte[]> BuildTemplateAsync(UserContext u, bool includeExisting, string? only = null)
    {
        var sheets = (only is null ? AllowedSheets(u) : Sheets.Where(s => s.Name == only)).ToList();
        if (sheets.Count == 0 || sheets.Any(s => !CanImport(u, s.Name)))
            throw new ForbiddenException("Bu listeyi toplu yükleme yetkiniz yok.");

        using var wb = new XLWorkbook();
        WriteGuide(wb.AddWorksheet("Nasıl doldurulur"), sheets, u.CanManageUsers);

        // Başvurulan liste (ör. Birim) bu dosyada yoksa öneriler sistemdeki kayıtlardan, gizli "Listeler" sayfasından gelir.
        var needLists = sheets.SelectMany(s => s.Columns).Select(c => c.ListFrom).OfType<string>()
            .Where(src => sheets.All(s => s.Name != src)).Distinct().ToList();
        var listRanges = new Dictionary<string, IXLRange>();
        if (needLists.Count > 0)
        {
            var lists = wb.AddWorksheet("Listeler");
            var col = 1;
            foreach (var src in needLists)
            {
                var values = src switch
                {
                    SUnits => await db.OrganizationUnits.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(),
                    STeams => await db.Teams.Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => x.Name).Distinct().ToListAsync(),
                    SRoles => await db.JobRoles.Where(x => x.IsActive).OrderBy(x => x.SortOrder).Select(x => x.Name).ToListAsync(),
                    _ => []
                };
                lists.Cell(1, col).Value = src;
                for (var i = 0; i < values.Count; i++) lists.Cell(i + 2, col).SetValue(values[i]);
                if (values.Count > 0) listRanges[src] = lists.Range(2, col, values.Count + 1, col);
                col++;
            }
            lists.Visibility = XLWorksheetVisibility.Hidden;
        }

        var existing = includeExisting ? await ExistingRowsAsync(u) : [];
        foreach (var s in sheets)
        {
            var ws = wb.AddWorksheet(s.Name);
            for (var c = 0; c < s.Columns.Length; c++)
            {
                var col = s.Columns[c];
                var cell = ws.Cell(1, c + 1);
                cell.Value = col.Header + (col.Required ? " *" : "");
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = col.Required ? XLColor.FromHtml("#FCE4C4") : XLColor.FromHtml("#DCE9F7");
                if (col.Note.Length > 0) cell.CreateComment().AddText(col.Note);
                // Sicil no, telefon gibi değerler sayıya dönüşüp bozulmasın: tüm sütunlar metin.
                ws.Column(c + 1).Style.NumberFormat.Format = "@";
                ws.Column(c + 1).Width = Math.Max(14, col.Header.Length + 6);
                var range = ws.Range(2, c + 1, MaxRowsPerSheet + 1, c + 1);
                if (col.IsBool)
                {
                    var dv = range.CreateDataValidation();
                    dv.List("\"Evet,Hayır\"");
                    dv.ErrorStyle = XLErrorStyle.Stop;
                    dv.ErrorMessage = "Evet veya Hayır seçin.";
                }
                else if (col.ListFrom is string src)
                {
                    // Öneri listesi; listede olmayan değerler de yazılabilir (yalnız bilgi verir).
                    IXLRange? source = sheets.Any(x => x.Name == src)
                        ? wb.Worksheet(src).Range(2, 1, MaxRowsPerSheet + 1, 1)
                        : listRanges.GetValueOrDefault(src);
                    if (source is not null)
                    {
                        var dv = range.CreateDataValidation();
                        dv.List(source);
                        dv.ErrorStyle = XLErrorStyle.Information;
                        dv.ShowErrorMessage = false;
                    }
                }
            }
            if (existing.TryGetValue(s.Name, out var rows))
                for (var r = 0; r < rows.Count; r++)
                    for (var c = 0; c < rows[r].Length; c++)
                        ws.Cell(r + 2, c + 1).SetValue(rows[r][c]);
            ws.SheetView.FreezeRows(1);
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteGuide(IXLWorksheet ws, List<Sheet> sheets, bool isAdmin)
    {
        var single = sheets.Count == 1;
        var lines = new List<string>
        {
            single ? $"Saha360 · {sheets[0].Name} toplu ekleme şablonu" : "Saha360 · Toplu yükleme şablonu",
            "",
            single
                ? $"1. '{sheets[0].Name}' sayfasını doldurun ({sheets[0].Purpose}). İlk satırdaki başlıkları değiştirmeyin."
                : "1. Doldurmak istediğiniz sayfaları doldurun; boş bıraktığınız sayfalar atlanır. İlk satırdaki başlıkları değiştirmeyin.",
            "2. Turuncu başlıklı sütunlar zorunludur (*). Başlığın üzerine gelince açıklaması görünür.",
        };
        if (single)
            lines.Add("3. Birim, ekip ve unvan sütunlarında açılır listede sistemdeki kayıtlar önerilir; önce o kayıtların sistemde olması gerekir.");
        else
            lines.AddRange([
                "3. Sayfalar şu sırayla işlenir, aynı dosyada birbirine başvurabilirler:",
                "   " + string.Join(" → ", sheets.Select(s => s.Name)),
                "   (Ekip şefi ve bağlı şef, personel yüklendikten sonra bağlanır.)"]);
        lines.AddRange([
            "4. Aynı anahtarla kayıt varsa EKLENMEZ, GÜNCELLENİR: birim/unvan/kategori/bölge → ad, malzeme → kod,",
            "   ekip → birim + ad, personel → sicil no, kullanıcı → kullanıcı adı. Dosyayı düzeltip tekrar yükleyebilirsiniz.",
            "5. Hepsi ya da hiçbiri: bir satırda hata varsa hiçbir kayıt yapılmaz; hatalar sayfa ve satır numarasıyla listelenir.",
            "6. Evet/Hayır sütunlarında açılır listeyi kullanın. Boş bırakılan 'Aktif' Evet sayılır.",
            "7. Kayıt silinmez; kullanılmayacak kaydı 'Aktif = Hayır' yaparak pasife alın.",
        ]);
        if (sheets.Any(s => s.Name == SUsers) && isAdmin)
            lines.Add("8. Kullanıcılar: Parola yalnız yeni kullanıcıda zorunludur. Yükledikten sonra parola içeren dosyayı silin; kullanıcılar ilk girişte parolasını değiştirmelidir.");
        lines.Add("");
        if (sheets.Any(s => s.Name == SRoles))
            lines.Add("Örnek unvanlar → Şef (Ekip lideri: Evet) · İşçi · Tekniker · Mühendis · Şoför (Herkes atanabilir: Evet). Sıra boş kalabilir.");
        if (sheets.Any(s => s.Name == SEmployees))
            lines.Add("Örnek personel → S-1001 | Ali | Demir | Fen İşleri Müdürlüğü | Saha Ekibi 1 | Tekniker");
        if (sheets.Any(s => s.Name == SUsers))
            lines.Add("Örnek kullanıcı → ali.demir | Ali Demir | | GeciciParola1 | Personel | S-1001");
        for (var i = 0; i < lines.Count; i++) ws.Cell(i + 1, 1).Value = lines[i];
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Column(1).Width = 140;
    }

    private async Task<Dictionary<string, List<string[]>>> ExistingRowsAsync(UserContext u)
    {
        static string B(bool v) => v ? "Evet" : "Hayır";
        var units = await db.OrganizationUnits.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        var unitName = units.ToDictionary(x => x.Id, x => x.Name);
        var roles = await db.JobRoles.AsNoTracking().OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync();
        var emps = await db.Employees.AsNoTracking().OrderBy(x => x.FirstName).ToListAsync();
        var empNo = emps.ToDictionary(e => e.Id, e => e.EmployeeNumber ?? "");
        var teams = await db.Teams.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
        var result = new Dictionary<string, List<string[]>>
        {
            [SUnits] = units.Select(x => new[] { x.Name, x.Code ?? "", B(x.IsActive) }).ToList(),
            [SRoles] = roles.Select(x => new[] { x.Name, B(x.IsOpenToAll), B(x.IsLeadRole), x.SortOrder.ToString(), B(x.IsActive) }).ToList(),
            [SCategories] = (await db.WorkCategories.AsNoTracking().OrderBy(x => x.Name).ToListAsync()).Select(x => new[] { x.Name, B(x.IsActive) }).ToList(),
            [SLocations] = (await db.WorkLocations.AsNoTracking().OrderBy(x => x.Name).ToListAsync()).Select(x => new[] { x.Name, B(x.IsActive) }).ToList(),
            [SMaterials] = (await db.MaterialCatalog.AsNoTracking().OrderBy(x => x.Code).ToListAsync())
                .Select(x => new[] { x.Code, x.Name, x.Unit, x.Category ?? "", x.Description ?? "", B(x.IsActive) }).ToList(),
            [STeams] = teams.Select(x => new[] { x.Name, unitName[x.OrganizationUnitId],
                x.SupervisorEmployeeId is int s ? empNo.GetValueOrDefault(s, "") : "", x.Description ?? "", B(x.IsActive) }).ToList(),
            [SEmployees] = emps.Select(e => new[] { e.EmployeeNumber ?? "", e.FirstName, e.LastName, unitName[e.OrganizationUnitId],
                e.TeamId is int t ? teams.First(x => x.Id == t).Name : "", e.JobRoleId is int r ? roles.First(x => x.Id == r).Name : "",
                e.SupervisorEmployeeId is int s ? empNo.GetValueOrDefault(s, "") : "", e.Phone ?? "", B(e.IsActive) }).ToList(),
        };
        if (u.CanManageUsers)
        {
            var list = await db.Users.AsNoTracking().OrderBy(x => x.UserName).ToListAsync();
            var userRoles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync();
            result[SUsers] = list.Select(x => new[]
            {
                x.UserName ?? "", x.FullName, x.Email ?? "", "",
                string.Join(", ", userRoles.Where(r => r.UserId == x.Id).Select(r => SystemRoles.DisplayName(r.Name!))),
                emps.FirstOrDefault(e => e.ApplicationUserId == x.Id)?.EmployeeNumber ?? "",
                x.OrganizationUnitId is int un ? unitName.GetValueOrDefault(un, "") : "", B(x.IsActive)
            }).ToList();
        }
        return result;
    }

    // ───────────────────────── İçe aktarma ─────────────────────────

    /// <param name="only">Verilirse dosyadaki yalnız bu sayfa işlenir (sayfaya özel toplu ekleme); diğer sayfalar yok sayılır.</param>
    public async Task<ImportResult> ImportAsync(UserContext u, Stream file, long length, string? only = null)
    {
        if (!AllowedSheets(u).Any()) throw new ForbiddenException("Toplu yükleme yetkiniz yok.");
        if (only is not null && !CanImport(u, only)) throw new ForbiddenException("Bu listeyi toplu yükleme yetkiniz yok.");
        if (length > MaxFileBytes) throw new DomainException("Dosya en fazla 5 MB olabilir.");
        XLWorkbook wb;
        try { wb = new XLWorkbook(file); }
        catch (Exception) { throw new DomainException("Dosya okunamadı. Lütfen şablondaki .xlsx dosyasını kullanın."); }

        using (wb)
        {
            if (only is not null && !wb.TryGetWorksheet(only, out _))
            {
                var missing = new ImportResult();
                missing.Errors.Add(new ImportError(only, 0, $"Dosyada '{only}' sayfası yok. Bu ekrandan indirdiğiniz şablonu kullanın."));
                return missing;
            }
            var ctx = new Ctx(this, u, wb, only);
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                await ctx.LoadAsync();
                await ctx.UnitsAsync();
                await ctx.SimpleAsync(SRoles);
                await ctx.SimpleAsync(SCategories);
                await ctx.SimpleAsync(SLocations);
                await ctx.MaterialsAsync();
                await ctx.TeamsAsync();
                await ctx.EmployeesAsync();
                await ctx.LinkSupervisorsAsync();
                await ctx.UsersAsync();

                if (ctx.Result.Errors.Count > 0 || ctx.Result.TotalRows == 0)
                {
                    await tx.RollbackAsync();
                    db.ChangeTracker.Clear();
                    if (ctx.Result.Errors.Count == 0)
                        ctx.Result.Errors.Add(new ImportError("—", 0, "Dosyada yüklenecek satır bulunamadı. Başlıkların altına veri girdiğinizden emin olun."));
                    return ctx.Result;
                }
                foreach (var s in ctx.Result.Sheets.Where(s => s.Added + s.Updated > 0))
                    audit.Add(u, "BulkImport", "Import", s.Sheet, $"Toplu yükleme · {s.Sheet}: {s.Added} eklendi, {s.Updated} güncellendi.");
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                ctx.Result.Committed = true;
                return ctx.Result;
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        }
    }

    /// <summary>Bir yükleme işleminin durumu: okunan sayfalar, ad → kayıt eşlemeleri, hatalar.</summary>
    private sealed class Ctx(ImportService s, UserContext u, XLWorkbook wb, string? only)
    {
        public ImportResult Result { get; } = new();
        private readonly AppDbContext db = s.Db;

        private Dictionary<string, OrganizationUnit> units = null!;
        private Dictionary<string, JobRole> roles = null!;
        private Dictionary<string, Employee> employeesByNo = null!;
        private List<Employee> allEmployees = null!;
        private List<Team> teams = null!;
        private readonly List<(int Row, Team Team, string SupervisorNo)> teamSupervisors = [];
        private readonly List<(int Row, Employee Employee, string SupervisorNo)> employeeSupervisors = [];

        public async Task LoadAsync()
        {
            units = (await db.OrganizationUnits.ToListAsync()).ToDictionary(x => Key(x.Name), StringComparer.Ordinal);
            roles = (await db.JobRoles.ToListAsync()).ToDictionary(x => Key(x.Name), StringComparer.Ordinal);
            teams = await db.Teams.ToListAsync();
            allEmployees = await db.Employees.ToListAsync();
            employeesByNo = allEmployees.Where(e => e.EmployeeNumber != null)
                .ToDictionary(e => Key(e.EmployeeNumber!), StringComparer.Ordinal);
        }

        // ─── Sayfa okuma yardımcıları ───

        private sealed record RowData(int Row, Dictionary<string, string> Values)
        {
            public string this[string header] => Values.GetValueOrDefault(Norm(header), "");
        }

        private List<RowData>? Read(string sheetName)
        {
            var spec = Sheets.First(x => x.Name == sheetName);
            if (only is not null && only != sheetName) return null;           // sayfaya özel yüklemede diğer sayfalar yok sayılır
            if (!wb.TryGetWorksheet(sheetName, out var ws)) return null;
            var last = ws.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0;
            if (last < 2) return null;
            if (!CanImport(u, sheetName))
            {
                Error(sheetName, 0, sheetName == SUsers
                    ? "Kullanıcılar sayfasını yalnız sistem yöneticisi yükleyebilir."
                    : $"{sheetName} sayfasını yükleme yetkiniz yok; bu sayfayı boş bırakın veya yöneticinize başvurun.");
                return null;
            }
            if (last - 1 > MaxRowsPerSheet) { Error(sheetName, 0, $"En fazla {MaxRowsPerSheet} satır yüklenebilir."); return null; }

            var headers = new Dictionary<string, int>();
            var lastCol = ws.Row(1).LastCellUsed(XLCellsUsedOptions.Contents)?.Address.ColumnNumber ?? 0;
            for (var c = 1; c <= lastCol; c++)
            {
                var h = Norm(ws.Cell(1, c).GetString());
                if (h.Length > 0) headers.TryAdd(h, c);
            }
            // Eski şablonlardaki başlık adı da kabul edilir.
            if (headers.TryGetValue("kısıtlamasız", out var oldOpen)) headers.TryAdd("herkes atanabilir", oldOpen);
            var missing = spec.Columns.Where(c => c.Required && !headers.ContainsKey(Norm(c.Header))).Select(c => c.Header).ToList();
            if (missing.Count > 0) { Error(sheetName, 1, "Zorunlu sütun başlığı bulunamadı: " + string.Join(", ", missing)); return null; }

            var rows = new List<RowData>();
            for (var r = 2; r <= last; r++)
            {
                var values = headers.ToDictionary(h => h.Key, h => Clean(ws.Cell(r, h.Value).GetFormattedString()));
                if (values.Values.All(v => v.Length == 0)) continue;
                rows.Add(new RowData(r, values));
            }
            return rows.Count == 0 ? null : rows;
        }

        private bool Required(string sheet, RowData row, params string[] headers)
        {
            var empty = headers.Where(h => row[h].Length == 0).ToList();
            if (empty.Count == 0) return true;
            Error(sheet, row.Row, "Zorunlu alan boş: " + string.Join(", ", empty));
            return false;
        }

        private bool? Bool(string sheet, RowData row, string header, bool @default)
        {
            var v = row[header].ToLower(Tr);
            if (v.Length == 0) return @default;
            if (v is "evet" or "e" or "yes" or "y" or "true" or "doğru" or "1" or "x" or "✓") return true;
            if (v is "hayır" or "hayir" or "h" or "no" or "n" or "false" or "yanlış" or "0") return false;
            Error(sheet, row.Row, $"{header}: \"{row[header]}\" anlaşılamadı; Evet veya Hayır yazın.");
            return null;
        }

        private bool Len(string sheet, RowData row, string header, int max)
        {
            if (row[header].Length <= max) return true;
            Error(sheet, row.Row, $"{header} en fazla {max} karakter olabilir.");
            return false;
        }

        private void Error(string sheet, int row, string message) => Result.Errors.Add(new ImportError(sheet, row, message));

        private bool Duplicate(string sheet, RowData row, HashSet<string> seen, string key, string label)
        {
            if (seen.Add(key)) return false;
            Error(sheet, row.Row, $"{label} dosyada birden fazla kez geçiyor.");
            return true;
        }

        private bool UnitAllowed(string sheet, RowData row, OrganizationUnit unit)
        {
            if (unit.Id == 0 || u.InScope(unit.Id)) return true;   // 0: aynı dosyada yeni eklenen birim
            Error(sheet, row.Row, $"Yalnız kendi biriminize kayıt yükleyebilirsiniz ({unit.Name} kapsamınız dışında).");
            return false;
        }

        // ─── Sayfalar ───

        public async Task UnitsAsync()
        {
            var rows = Read(SUnits);
            if (rows is null) return;
            int added = 0, updated = 0;
            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                if (!Required(SUnits, row, "Ad") || !Len(SUnits, row, "Ad", 150) || !Len(SUnits, row, "Kod", 30)) continue;
                var key = Key(row["Ad"]);
                if (Duplicate(SUnits, row, seen, key, $"\"{row["Ad"]}\"")) continue;
                if (Bool(SUnits, row, "Aktif", true) is not bool active) continue;
                if (!units.TryGetValue(key, out var unit))
                {
                    unit = new OrganizationUnit { Name = row["Ad"] };
                    db.OrganizationUnits.Add(unit);
                    units[key] = unit;
                    added++;
                }
                else updated++;
                unit.Code = NullIfEmpty(row["Kod"]);
                unit.IsActive = active;
            }
            await db.SaveChangesAsync();
            Result.Sheets.Add(new ImportSheetResult(SUnits, added, updated));
        }

        /// <summary>Unvanlar, kategoriler, bölgeler: ada göre ekle/güncelle.</summary>
        public async Task SimpleAsync(string sheet)
        {
            var rows = Read(sheet);
            if (rows is null) return;
            int added = 0, updated = 0;
            var seen = new HashSet<string>();
            var existingCats = sheet == SCategories ? (await db.WorkCategories.ToListAsync()).ToDictionary(x => Key(x.Name)) : null;
            var existingLocs = sheet == SLocations ? (await db.WorkLocations.ToListAsync()).ToDictionary(x => Key(x.Name)) : null;
            // Sırası boş bırakılan yeni unvan listenin sonuna eklenir.
            var nextOrder = roles.Values.Select(r => r.SortOrder).DefaultIfEmpty(0).Max() + 1;
            foreach (var row in rows)
            {
                if (!Required(sheet, row, "Ad") || !Len(sheet, row, "Ad", 150)) continue;
                var key = Key(row["Ad"]);
                if (Duplicate(sheet, row, seen, key, $"\"{row["Ad"]}\"")) continue;
                if (Bool(sheet, row, "Aktif", true) is not bool active) continue;
                DefinitionEntity entity;
                bool isNew;
                if (sheet == SRoles)
                {
                    if (Bool(sheet, row, "Herkes atanabilir", false) is not bool open || Bool(sheet, row, "Ekip lideri", false) is not bool lead) continue;
                    int order = 0;
                    if (row["Sıra"].Length > 0 && !int.TryParse(row["Sıra"], NumberStyles.Integer, CultureInfo.InvariantCulture, out order))
                    { Error(sheet, row.Row, "Sıra bir tam sayı olmalı."); continue; }
                    isNew = !roles.TryGetValue(key, out var role);
                    role ??= new JobRole { SortOrder = nextOrder++ };
                    role.IsOpenToAll = open;
                    role.IsLeadRole = lead;
                    if (row["Sıra"].Length > 0) role.SortOrder = order;
                    if (isNew) { db.JobRoles.Add(role); roles[key] = role; }
                    entity = role;
                }
                else if (sheet == SCategories)
                {
                    isNew = !existingCats!.TryGetValue(key, out var cat);
                    if (isNew) { cat = new WorkCategory(); db.WorkCategories.Add(cat); existingCats[key] = cat; }
                    entity = cat!;
                }
                else
                {
                    isNew = !existingLocs!.TryGetValue(key, out var loc);
                    if (isNew) { loc = new WorkLocation(); db.WorkLocations.Add(loc); existingLocs[key] = loc; }
                    entity = loc!;
                }
                if (isNew) { entity.Name = row["Ad"]; added++; } else updated++;
                entity.IsActive = active;
            }
            await db.SaveChangesAsync();
            Result.Sheets.Add(new ImportSheetResult(sheet, added, updated));
        }

        public async Task MaterialsAsync()
        {
            var rows = Read(SMaterials);
            if (rows is null) return;
            var existing = (await db.MaterialCatalog.ToListAsync()).ToDictionary(x => Key(x.Code));
            int added = 0, updated = 0;
            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                if (!Required(SMaterials, row, "Kod", "Ad", "Ölçü birimi")) continue;
                if (!Len(SMaterials, row, "Kod", 40) || !Len(SMaterials, row, "Ad", 200) || !Len(SMaterials, row, "Ölçü birimi", 30)
                    || !Len(SMaterials, row, "Kategori", 100) || !Len(SMaterials, row, "Açıklama", 500)) continue;
                var key = Key(row["Kod"]);
                if (Duplicate(SMaterials, row, seen, key, $"Kod \"{row["Kod"]}\"")) continue;
                if (Bool(SMaterials, row, "Aktif", true) is not bool active) continue;
                if (!existing.TryGetValue(key, out var m))
                {
                    m = new MaterialCatalog { Code = row["Kod"] };
                    db.MaterialCatalog.Add(m);
                    existing[key] = m;
                    added++;
                }
                else updated++;
                m.Name = row["Ad"];
                m.Unit = row["Ölçü birimi"];
                m.Category = NullIfEmpty(row["Kategori"]);
                m.Description = NullIfEmpty(row["Açıklama"]);
                m.IsActive = active;
            }
            await db.SaveChangesAsync();
            Result.Sheets.Add(new ImportSheetResult(SMaterials, added, updated));
        }

        public async Task TeamsAsync()
        {
            var rows = Read(STeams);
            if (rows is null) return;
            int added = 0, updated = 0;
            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                if (!Required(STeams, row, "Ad", "Birim") || !Len(STeams, row, "Ad", 150) || !Len(STeams, row, "Açıklama", 500)) continue;
                if (!units.TryGetValue(Key(row["Birim"]), out var unit)) { Error(STeams, row.Row, $"Birim bulunamadı: \"{row["Birim"]}\"."); continue; }
                if (!UnitAllowed(STeams, row, unit)) continue;
                if (Duplicate(STeams, row, seen, Key(row["Birim"]) + "|" + Key(row["Ad"]), $"\"{row["Ad"]}\" ekibi")) continue;
                if (Bool(STeams, row, "Aktif", true) is not bool active) continue;
                var team = teams.FirstOrDefault(t => Key(t.Name) == Key(row["Ad"]) && (t.OrganizationUnitId == unit.Id && unit.Id != 0 || t.OrganizationUnit == unit));
                if (team is null)
                {
                    team = new Team { Name = row["Ad"], OrganizationUnit = unit };
                    db.Teams.Add(team);
                    teams.Add(team);
                    added++;
                }
                else updated++;
                team.Description = NullIfEmpty(row["Açıklama"]);
                team.IsActive = active;
                if (row["Şef sicil no"].Length > 0) teamSupervisors.Add((row.Row, team, row["Şef sicil no"]));
            }
            await db.SaveChangesAsync();
            Result.Sheets.Add(new ImportSheetResult(STeams, added, updated));
        }

        public async Task EmployeesAsync()
        {
            var rows = Read(SEmployees);
            if (rows is null) return;
            int added = 0, updated = 0;
            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                if (!Required(SEmployees, row, "Sicil no", "Ad", "Soyad", "Birim")) continue;
                if (!Len(SEmployees, row, "Sicil no", 30) || !Len(SEmployees, row, "Ad", 80) || !Len(SEmployees, row, "Soyad", 80)
                    || !Len(SEmployees, row, "Telefon", 30)) continue;
                var key = Key(row["Sicil no"]);
                if (Duplicate(SEmployees, row, seen, key, $"Sicil no \"{row["Sicil no"]}\"")) continue;
                if (!units.TryGetValue(Key(row["Birim"]), out var unit)) { Error(SEmployees, row.Row, $"Birim bulunamadı: \"{row["Birim"]}\"."); continue; }
                if (!UnitAllowed(SEmployees, row, unit)) continue;
                Team? team = null;
                if (row["Ekip"].Length > 0)
                {
                    team = teams.FirstOrDefault(t => Key(t.Name) == Key(row["Ekip"]) && (t.OrganizationUnit == unit || t.OrganizationUnitId == unit.Id));
                    if (team is null) { Error(SEmployees, row.Row, $"\"{row["Birim"]}\" biriminde \"{row["Ekip"]}\" ekibi bulunamadı."); continue; }
                }
                JobRole? role = null;
                if (row["Unvan"].Length > 0 && !roles.TryGetValue(Key(row["Unvan"]), out role))
                { Error(SEmployees, row.Row, $"Unvan bulunamadı: \"{row["Unvan"]}\". Önce Unvanlar sayfasına ekleyin."); continue; }
                if (Bool(SEmployees, row, "Aktif", true) is not bool active) continue;

                if (!employeesByNo.TryGetValue(key, out var e))
                {
                    e = new Employee { EmployeeNumber = row["Sicil no"] };
                    db.Employees.Add(e);
                    employeesByNo[key] = e;
                    allEmployees.Add(e);
                    added++;
                }
                else
                {
                    if (e.IsActive && !active && await db.WorkAssignments.AnyAsync(a => a.EmployeeId == e.Id && a.ReleasedAtUtc == null))
                    { Error(SEmployees, row.Row, $"{e.FullName} devam eden işlerde; pasife almadan önce bu işlerden çıkarın."); continue; }
                    updated++;
                }
                e.FirstName = row["Ad"];
                e.LastName = row["Soyad"];
                e.OrganizationUnit = unit;
                e.Team = team;
                if (team is null) e.TeamId = null;
                e.JobRole = role;
                if (role is null) e.JobRoleId = null;
                e.Phone = NullIfEmpty(row["Telefon"]);
                e.IsActive = active;
                e.UpdatedAtUtc = DateTime.UtcNow;
                if (row["Bağlı şef sicil no"].Length > 0) employeeSupervisors.Add((row.Row, e, row["Bağlı şef sicil no"]));
            }
            await db.SaveChangesAsync();
            Result.Sheets.Add(new ImportSheetResult(SEmployees, added, updated));
        }

        /// <summary>Ekip şefi ve bağlı şef, tüm personel yüklendikten sonra bağlanır (aynı dosyada sonra gelen kişiye başvurabilir).</summary>
        public async Task LinkSupervisorsAsync()
        {
            foreach (var (row, team, no) in teamSupervisors)
            {
                if (employeesByNo.TryGetValue(Key(no), out var sup)) team.SupervisorEmployee = sup;
                else Error(STeams, row, $"Şef sicil no bulunamadı: \"{no}\".");
            }
            foreach (var (row, e, no) in employeeSupervisors)
            {
                if (!employeesByNo.TryGetValue(Key(no), out var sup)) Error(SEmployees, row, $"Bağlı şef sicil no bulunamadı: \"{no}\".");
                else if (sup == e) Error(SEmployees, row, "Personel kendisinin şefi olamaz.");
                else e.SupervisorEmployee = sup;
            }
            await db.SaveChangesAsync();
        }

        public async Task UsersAsync()
        {
            var rows = Read(SUsers);
            if (rows is null) return;
            int added = 0, updated = 0;
            var seen = new HashSet<string>();
            var roleLookup = SystemRoles.All.SelectMany(r => new[] { (Key(r), r), (Key(SystemRoles.DisplayName(r)), r) })
                .Append((Key("Planlamacı"), SystemRoles.Planner)).Append((Key("İş koordinatörü"), SystemRoles.Planner))
                .DistinctBy(x => x.Item1).ToDictionary(x => x.Item1, x => x.Item2);
            var linkedInFile = new Dictionary<int, string>();

            foreach (var row in rows)
            {
                if (!Required(SUsers, row, "Kullanıcı adı", "Ad soyad", "Roller") || !Len(SUsers, row, "Ad soyad", 150)) continue;
                var userName = row["Kullanıcı adı"];
                if (Duplicate(SUsers, row, seen, Key(userName), $"Kullanıcı adı \"{userName}\"")) continue;
                if (Bool(SUsers, row, "Aktif", true) is not bool active) continue;

                var wanted = new List<string>();
                var badRole = false;
                foreach (var part in row["Roller"].Split([',', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (roleLookup.TryGetValue(Key(part), out var r)) wanted.Add(r);
                    else { Error(SUsers, row.Row, $"Rol anlaşılamadı: \"{part}\". Geçerli roller: {string.Join(", ", SystemRoles.All.Select(SystemRoles.DisplayName))}."); badRole = true; }
                }
                if (badRole) continue;
                wanted = wanted.Distinct().ToList();

                Employee? emp = null;
                if (row["Personel sicil no"].Length > 0 && !employeesByNo.TryGetValue(Key(row["Personel sicil no"]), out emp))
                { Error(SUsers, row.Row, $"Personel sicil no bulunamadı: \"{row["Personel sicil no"]}\"."); continue; }
                if (emp is null && (wanted.Contains(SystemRoles.Personnel) || wanted.Contains(SystemRoles.Supervisor)))
                { Error(SUsers, row.Row, "Şef ve Personel rolündeki hesap bir personel sicil numarasına bağlanmalı."); continue; }
                OrganizationUnit? scope = null;
                if (row["Birim kapsamı"].Length > 0 && !units.TryGetValue(Key(row["Birim kapsamı"]), out scope))
                { Error(SUsers, row.Row, $"Birim bulunamadı: \"{row["Birim kapsamı"]}\"."); continue; }

                var user = await s.Users.FindByNameAsync(userName);
                var isNew = user is null;
                if (emp is not null)
                {
                    if (emp.ApplicationUserId is not null && emp.ApplicationUserId != user?.Id)
                    { Error(SUsers, row.Row, $"{emp.FullName} başka bir kullanıcı hesabına bağlı."); continue; }
                    if (emp.Id != 0 && linkedInFile.TryGetValue(emp.Id, out var other) && other != userName)
                    { Error(SUsers, row.Row, $"{emp.FullName} dosyada başka bir kullanıcıya da bağlanmış ({other})."); continue; }
                }

                if (isNew)
                {
                    if (row["Parola"].Length == 0) { Error(SUsers, row.Row, "Yeni kullanıcı için parola zorunlu."); continue; }
                    user = new ApplicationUser { UserName = userName, EmailConfirmed = true };
                }
                else
                {
                    var current = await s.Users.GetRolesAsync(user!);
                    if (current.Contains(SystemRoles.SystemAdmin) && (!wanted.Contains(SystemRoles.SystemAdmin) || !active)
                        && (await s.Users.GetUsersInRoleAsync(SystemRoles.SystemAdmin)).Count(a => a.IsActive) <= 1)
                    { Error(SUsers, row.Row, "Son aktif sistem yöneticisinin yetkisi kaldırılamaz."); continue; }
                }
                user!.FullName = row["Ad soyad"];
                user.Email = NullIfEmpty(row["E-posta"]);
                user.IsActive = active;
                user.OrganizationUnitId = scope?.Id;

                var ok = isNew ? Check(row, await s.Users.CreateAsync(user, row["Parola"])) : Check(row, await s.Users.UpdateAsync(user));
                if (!ok) continue;
                if (!isNew && row["Parola"].Length > 0)
                {
                    if (!Check(row, await s.Users.RemovePasswordAsync(user)) || !Check(row, await s.Users.AddPasswordAsync(user, row["Parola"]))) continue;
                }
                var have = await s.Users.GetRolesAsync(user);
                if (!Check(row, await s.Users.RemoveFromRolesAsync(user, have.Except(wanted))) ||
                    !Check(row, await s.Users.AddToRolesAsync(user, wanted.Except(have)))) continue;
                if (!isNew) await s.Users.UpdateSecurityStampAsync(user);

                // Personel bağlantısı: önceki bağlantı kaldırılır.
                foreach (var old in allEmployees.Where(x => x.ApplicationUserId == user.Id && x != emp)) old.ApplicationUserId = null;
                if (emp is not null) { emp.ApplicationUserId = user.Id; linkedInFile[emp.Id] = userName; }

                if (isNew) added++; else updated++;
            }
            await db.SaveChangesAsync();
            Result.Sheets.Add(new ImportSheetResult(SUsers, added, updated));
        }

        private bool Check(RowData row, IdentityResult r)
        {
            if (r.Succeeded) return true;
            Error(SUsers, row.Row, string.Join(" ", r.Errors.Select(e => e.Description)));
            return false;
        }
    }

    private static string Clean(string? s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Norm(string header) => Clean(header.Replace("*", "")).ToLower(Tr);
    private static string Key(string s) => Clean(s).ToLower(Tr);
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
