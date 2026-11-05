using ClosedXML.Excel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Excel ile toplu yükleme.</summary>
public class ImportTests : IDisposable
{
    private readonly TestHost h = new();
    public void Dispose() => h.Dispose();
    private ImportService Import => h.Get<ImportService>();

    private static MemoryStream Workbook(Dictionary<string, string[][]> sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var (name, rows) in sheets)
        {
            var ws = wb.AddWorksheet(name);
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    ws.Cell(r + 1, c + 1).SetValue(rows[r][c]);
        }
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static Dictionary<string, string[][]> FullFile() => new()
    {
        ["Birimler"] = [["Ad *", "Kod", "Aktif"], ["Fen İşleri", "FİM", "Evet"]],
        ["Unvanlar"] = [["Ad *", "Kısıtlamasız", "Ekip lideri", "Sıra", "Aktif"],
            ["Şef Yardımcısı", "Hayır", "Evet", "1", ""], ["Mühendis", "", "", "2", ""], ["Sürücü", "Evet", "", "3", ""]],
        ["Ekipler"] = [["Ad *", "Birim *", "Şef sicil no", "Açıklama", "Aktif"], ["Yol Ekibi", "fen işleri", "F-1", "", ""]],
        // F-2'nin bağlı şefi F-1; F-1 dosyada sonra da gelse bağlanır.
        ["Personel"] = [["Sicil no *", "Ad *", "Soyad *", "Birim *", "Ekip", "Unvan", "Bağlı şef sicil no", "Telefon", "Aktif"],
            ["F-2", "Deniz", "Aksu", "Fen İşleri", "Yol Ekibi", "Mühendis", "F-1", "", ""],
            ["F-1", "Kemal", "Arslan", "Fen İşleri", "Yol Ekibi", "Şef Yardımcısı", "", "", ""],
            [" 1001 ", "Sayısal", "Sicil", "Fen İşleri", "", "", "", "", "Hayır"]],
        ["Kullanıcılar"] = [["Kullanıcı adı *", "Ad soyad *", "E-posta", "Parola", "Roller *", "Personel sicil no", "Birim kapsamı", "Aktif"],
            ["kemal", "Kemal Arslan", "", "Gecici123", "Şef, Personel", "F-1", "", ""],
            ["fen.planlama", "Fen Planlama", "", "Gecici123", "Planlamacı", "", "Fen İşleri", ""]],
    };

    private async Task<UserContext> AdminAsync() => await h.UserAsync("yonetici", SystemRoles.SystemAdmin, null);

    [Fact]
    public async Task Template_has_every_sheet_with_headers_and_dropdowns()
    {
        var bytes = await Import.BuildTemplateAsync(await AdminAsync(), includeExisting: false);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.True(wb.TryGetWorksheet("Nasıl doldurulur", out _));
        foreach (var sheet in ImportService.Sheets)
        {
            var ws = wb.Worksheet(sheet.Name);
            Assert.Equal(sheet.Columns[0].Header + (sheet.Columns[0].Required ? " *" : ""), ws.Cell(1, 1).GetString());
            Assert.Equal(sheet.Columns.Length, ws.Row(1).CellsUsed().Count());
        }
        Assert.NotEmpty(wb.Worksheet("Unvanlar").DataValidations);

        // Excel'in açılır listeyi göstermesi için dosyadaki kural: sabit liste tırnaklı, sayfa başvurusu tırnaksız ve '=' olmadan.
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        var xml = string.Join("\n", zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/"))
            .Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.Contains("<x:formula1>\"Evet,Hayır\"</x:formula1>", xml.Replace("<formula1>", "<x:formula1>").Replace("</formula1>", "</x:formula1>"));
        Assert.Contains("Birimler!$A$2:$A$5001", xml);
    }

    [Fact]
    public async Task Existing_records_template_contains_current_data()
    {
        var bytes = await Import.BuildTemplateAsync(await AdminAsync(), includeExisting: true);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.Contains(wb.Worksheet("Birimler").Column(1).CellsUsed(), c => c.GetString() == "Test Birimi");
        Assert.Contains(wb.Worksheet("Kullanıcılar").Column(1).CellsUsed(), c => c.GetString() == "planlama");
        Assert.DoesNotContain(wb.Worksheet("Kullanıcılar").Column(4).CellsUsed().Skip(1), c => c.GetString().Length > 0); // parola yok
    }

    [Fact]
    public async Task Full_file_imports_with_cross_sheet_references()
    {
        var r = await Import.ImportAsync(await AdminAsync(), Workbook(FullFile()), 1000);
        Assert.True(r.Committed, string.Join(" | ", r.Errors.Select(e => $"{e.Sheet}:{e.Row} {e.Message}")));
        h.Db.ChangeTracker.Clear();

        Assert.True(h.Db.JobRoles.Single(x => x.Name == "Sürücü").IsOpenToAll);
        var kemal = h.Db.Employees.Include(e => e.JobRole).Single(e => e.EmployeeNumber == "F-1");
        var deniz = h.Db.Employees.Single(e => e.EmployeeNumber == "F-2");
        Assert.Equal(kemal.Id, deniz.SupervisorEmployeeId);
        Assert.Equal(kemal.Id, h.Db.Teams.Single(t => t.Name == "Yol Ekibi").SupervisorEmployeeId);
        Assert.False(h.Db.Employees.Single(e => e.EmployeeNumber == "1001").IsActive);

        var users = h.Get<UserManager<ApplicationUser>>();
        var kemalUser = await users.FindByNameAsync("kemal");
        Assert.NotNull(kemalUser);
        Assert.Equal(kemalUser!.Id, kemal.ApplicationUserId);
        Assert.True(await users.CheckPasswordAsync(kemalUser, "Gecici123"));
        Assert.Equal([SystemRoles.Personnel, SystemRoles.Supervisor], (await users.GetRolesAsync(kemalUser)).Order());
        var planner = await users.FindByNameAsync("fen.planlama");
        Assert.Equal(h.Db.OrganizationUnits.Single(u => u.Name == "Fen İşleri").Id, planner!.OrganizationUnitId);
    }

    [Fact]
    public async Task Reimporting_the_same_file_updates_instead_of_duplicating()
    {
        var admin = await AdminAsync();
        await Import.ImportAsync(admin, Workbook(FullFile()), 1000);
        var file = FullFile();
        file["Personel"][1][1] = "Deniz Can";
        var r = await Import.ImportAsync(admin, Workbook(file), 1000);
        Assert.True(r.Committed);
        Assert.Equal(0, r.Sheets.Sum(s => s.Added));
        h.Db.ChangeTracker.Clear();
        Assert.Equal(1, h.Db.Employees.Count(e => e.EmployeeNumber == "F-2"));
        Assert.Equal("Deniz Can", h.Db.Employees.Single(e => e.EmployeeNumber == "F-2").FirstName);
    }

    [Fact]
    public async Task Any_error_rolls_back_everything_and_reports_row_numbers()
    {
        var file = FullFile();
        file["Personel"] = [file["Personel"][0], file["Personel"][1], file["Personel"][2],
            ["F-3", "Hatalı", "Kayıt", "Olmayan Birim", "", "", "", "", ""]];
        file["Kullanıcılar"][1][4] = "Şef, Uçak pilotu";
        var r = await Import.ImportAsync(await AdminAsync(), Workbook(file), 1000);

        Assert.False(r.Committed);
        Assert.Contains(r.Errors, e => e.Sheet == "Personel" && e.Row == 4 && e.Message.Contains("Olmayan Birim"));
        Assert.Contains(r.Errors, e => e.Sheet == "Kullanıcılar" && e.Row == 2 && e.Message.Contains("Uçak pilotu"));
        h.Db.ChangeTracker.Clear();
        Assert.False(h.Db.OrganizationUnits.Any(u => u.Name == "Fen İşleri"));
        Assert.False(h.Db.Employees.Any(e => e.EmployeeNumber == "F-1"));
        Assert.Null(await h.Get<UserManager<ApplicationUser>>().FindByNameAsync("kemal"));
    }

    [Fact]
    public async Task Missing_required_header_and_bad_boolean_are_reported()
    {
        var r = await Import.ImportAsync(await AdminAsync(), Workbook(new()
        {
            ["Birimler"] = [["Kod", "Aktif"], ["X", "Evet"]],
            ["Bölgeler"] = [["Ad", "Aktif"], ["Kuzey", "belki"]],
        }), 100);
        Assert.False(r.Committed);
        Assert.Contains(r.Errors, e => e.Sheet == "Birimler" && e.Message.Contains("Ad"));
        Assert.Contains(r.Errors, e => e.Sheet == "Bölgeler" && e.Message.Contains("belki"));
    }

    [Fact]
    public async Task Import_permissions_follow_roles()
    {
        var manager = await h.UserAsync("birim.mudur", SystemRoles.UnitManager, null);
        var r = await Import.ImportAsync(manager, Workbook(FullFile()), 1000);
        Assert.False(r.Committed);
        Assert.Contains(r.Errors, e => e.Sheet == "Kullanıcılar");

        // Planlamacı: yalnız malzeme yükler; tanım sayfaları yetki hatası verir, kullanıcı ve personel yüklemesi hiç açılmaz.
        var p = await Import.ImportAsync(h.Planner, Workbook(FullFile()), 1000);
        Assert.False(p.Committed);
        Assert.Contains(p.Errors, e => e.Sheet == "Birimler" && e.Message.Contains("yetkiniz yok"));
        await Assert.ThrowsAsync<ForbiddenException>(() => Import.ImportAsync(h.Planner, Workbook(FullFile()), 1000, "Kullanıcılar"));
        await Assert.ThrowsAsync<ForbiddenException>(() => Import.BuildTemplateAsync(h.Planner, false, "Birimler"));

        await Assert.ThrowsAsync<ForbiddenException>(() => Import.ImportAsync(h.Planner, Workbook(FullFile()), 1000, "Personel"));
        var mat = await Import.ImportAsync(h.Planner, Workbook(new()
        {
            ["Malzemeler"] = [["Kod", "Ad", "Ölçü birimi"], ["VN-1", "Vana", "adet"]],
        }), 100, "Malzemeler");
        Assert.True(mat.Committed, string.Join(" | ", mat.Errors.Select(e => e.Message)));

        // Personeli birim yöneticisi yükler (kendi birimine).
        var ok = await Import.ImportAsync(h.Manager, Workbook(new()
        {
            ["Personel"] = [["Sicil no", "Ad", "Soyad", "Birim", "Unvan"], ["T-9", "Selin", "Polat", "Test Birimi", "İşçi"]],
        }), 100, "Personel");
        Assert.True(ok.Committed, string.Join(" | ", ok.Errors.Select(e => e.Message)));
        Assert.True(h.Db.Employees.Any(e => e.EmployeeNumber == "T-9"));
    }

    [Fact]
    public async Task Single_list_template_has_only_that_sheet_and_suggestions_from_system()
    {
        var bytes = await Import.BuildTemplateAsync(await AdminAsync(), false, "Personel");
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(new HashSet<string> { "Nasıl doldurulur", "Listeler", "Personel" }, wb.Worksheets.Select(w => w.Name).ToHashSet());
        Assert.Equal(XLWorksheetVisibility.Hidden, wb.Worksheet("Listeler").Visibility);
        Assert.Contains(wb.Worksheet("Listeler").CellsUsed(), c => c.GetString() == "Test Birimi");
        Assert.NotEmpty(wb.Worksheet("Personel").DataValidations);
    }

    [Fact]
    public async Task Single_list_import_ignores_other_sheets()
    {
        var r = await Import.ImportAsync(await AdminAsync(), Workbook(FullFile()), 1000, "Birimler");
        Assert.True(r.Committed, string.Join(" | ", r.Errors.Select(e => e.Message)));
        Assert.Equal(["Birimler"], r.Sheets.Select(s => s.Sheet));
        h.Db.ChangeTracker.Clear();
        Assert.True(h.Db.OrganizationUnits.Any(u => u.Name == "Fen İşleri"));
        Assert.False(h.Db.Employees.Any(e => e.EmployeeNumber == "F-1"));

        var missing = await Import.ImportAsync(await h.UserAsync("y2", SystemRoles.SystemAdmin, null), Workbook(new()
        {
            ["Birimler"] = [["Ad"], ["X"]],
        }), 100, "Kullanıcılar");
        Assert.False(missing.Committed);
        Assert.Contains(missing.Errors, e => e.Message.Contains("sayfası yok"));
    }

    [Fact]
    public async Task New_roles_go_to_the_end_when_order_is_blank_and_old_header_still_works()
    {
        var r = await Import.ImportAsync(await AdminAsync(), Workbook(new()
        {
            ["Unvanlar"] = [["Ad", "Kısıtlamasız"], ["Tekniker", ""], ["Bekçi", "Evet"]],
        }), 100, "Unvanlar");
        Assert.True(r.Committed);
        h.Db.ChangeTracker.Clear();
        var max = h.Db.JobRoles.Where(x => x.Name != "Tekniker" && x.Name != "Bekçi").Max(x => x.SortOrder);
        Assert.Equal(max + 1, h.Db.JobRoles.Single(x => x.Name == "Tekniker").SortOrder);
        Assert.Equal(max + 2, h.Db.JobRoles.Single(x => x.Name == "Bekçi").SortOrder);
        Assert.True(h.Db.JobRoles.Single(x => x.Name == "Bekçi").IsOpenToAll);
    }

    [Fact]
    public async Task Roles_can_be_moved_up_and_down()
    {
        var admin = await AdminAsync();
        var defs = h.Get<DefinitionService>();
        await defs.MoveRoleAsync(admin, h.WorkerRole.Id, up: true);
        h.Db.ChangeTracker.Clear();
        var order = h.Db.JobRoles.OrderBy(r => r.SortOrder).Select(r => r.Name).ToList();
        Assert.Equal(["İşçi", "Şef", "Şoför", "Mühendis"], order);
        Assert.Equal([1, 2, 3, 4], h.Db.JobRoles.OrderBy(r => r.SortOrder).Select(r => r.SortOrder).ToList());
        Assert.Equal(5, await defs.NextRoleOrderAsync());
        await Assert.ThrowsAsync<ForbiddenException>(() => defs.MoveRoleAsync(h.Planner, h.WorkerRole.Id, up: false));
    }

    [Fact]
    public async Task Last_admin_cannot_be_demoted_by_import()
    {
        var admin = await AdminAsync();
        var r = await Import.ImportAsync(admin, Workbook(new()
        {
            ["Kullanıcılar"] = [["Kullanıcı adı", "Ad soyad", "Roller"], ["yonetici", "Yönetici", "Personel"]],
        }), 100);
        Assert.False(r.Committed);
        Assert.Contains(r.Errors, e => e.Message.Contains("Son aktif sistem yöneticisi") || e.Message.Contains("sicil"));
    }
}
