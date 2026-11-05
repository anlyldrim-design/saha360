using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

public record ReportDefinition(string Key, string Title, string Description, string DateField);

/// <summary>Her rapor aynı biçimde döner: başlık, açıklama (kapsam + hesaplama), sütunlar, satırlar. Ekran ve CSV aynı veriyi kullanır.</summary>
public class ReportTable
{
    public required ReportDefinition Definition { get; init; }
    public List<string> Columns { get; init; } = [];
    public List<string[]> Rows { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public DateTime FromLocal { get; init; }
    public DateTime ToLocal { get; init; }
}

public class ReportQuery
{
    public string Key { get; set; } = "ozet";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int? UnitId { get; set; }
    public int? EmployeeId { get; set; }
}

/// <summary>
/// Raporlar. Kurallar: iş sayısı ile atama sayısı ayrı ölçülür; planlanan ve gerçekleşen süre ayrı gösterilir;
/// eksik veride süre hesaplanmaz; tüm sorgular kullanıcının görme kapsamıyla (VisibleWorks) sınırlıdır.
/// </summary>
public class ReportService(AppDbContext db, AccessService access)
{
    public static readonly ReportDefinition[] All =
    [
        new("ozet", "Dönem özeti", "Seçilen dönemde oluşturulan, tamamlanan ve iptal edilen iş sayıları ile tamamlanma oranı.", "Oluşturulma / tamamlanma tarihi"),
        new("durum", "Durum bazlı iş listesi", "Planlanan zamanı dönemle kesişen işlerin durumlara göre dağılımı.", "Planlanan zaman aralığı"),
        new("personel", "Personel bazlı görevler", "Her personelin dönemde yer aldığı iş ve atama sayıları, rolleri ve planlanan toplam süresi.", "Planlanan zaman aralığı"),
        new("sef", "Şef bazlı onaylar", "Sorumlu şef başına onay bekleyen, onaylanan ve iade edilen işler.", "Durum değişikliği tarihi"),
        new("ekip", "Ekip yoğunluğu", "Ekip başına dönemde yer alınan iş sayısı ve atama sayısı.", "Planlanan zaman aralığı"),
        new("geciken", "Geciken işler", "Planlanan bitişi geçmiş, tamamlanmamış ve iptal edilmemiş işler (bugün itibarıyla).", "Planlanan bitiş"),
        new("malzeme", "Malzeme kullanımı", "İşlerde bildirilen malzeme türü ve toplam miktarı. Stok bakiyesi değildir.", "Malzeme kayıt tarihi"),
        new("ret", "Ret / iade / iptal nedenleri", "Personel retleri, şef iadeleri ve iptallerin gerekçeleri.", "İşlem tarihi"),
        new("sure", "Planlanan / gerçekleşen süre", "Tamamlanan işlerde planlanan süre ile gerçekleşen sürenin karşılaştırması.", "Tamamlanma tarihi"),
        new("kanit", "Fotoğraf ve not kayıtları", "Her işte fotoğraf, not ve malzeme kaydı olup olmadığı.", "Planlanan zaman aralığı"),
        new("atama-gecmisi", "Personel atama geçmişi", "Dönem içindeki tüm atamalar: iş, rol, durum, atanma ve ayrılma zamanları.", "Atanma tarihi"),
    ];

    public async Task<ReportTable> RunAsync(UserContext u, ReportQuery rq)
    {
        if (!u.CanViewReports) throw new ForbiddenException("Raporları görme yetkiniz yok.");
        var def = All.FirstOrDefault(r => r.Key == rq.Key) ?? All[0];
        var fromLocal = (rq.From ?? TrTime.TodayLocal.AddDays(-30)).Date;
        var toLocal = (rq.To ?? TrTime.TodayLocal).Date;
        if (toLocal < fromLocal) (fromLocal, toLocal) = (toLocal, fromLocal);
        var from = TrTime.ToUtc(fromLocal);
        var to = TrTime.ToUtc(toLocal.AddDays(1));

        var works = access.VisibleWorks(u).AsNoTracking();
        if (rq.UnitId is int unit) works = works.Where(w => w.ResponsibleUnitId == unit);

        var t = def.Key switch
        {
            "durum" => await StatusAsync(works, from, to),
            "personel" => await PersonnelAsync(works, from, to),
            "sef" => await SupervisorAsync(works, from, to),
            "ekip" => await TeamAsync(works, from, to),
            "geciken" => await OverdueAsync(works),
            "malzeme" => await MaterialAsync(works, from, to),
            "ret" => await ReasonsAsync(works, from, to),
            "sure" => await DurationAsync(works, from, to),
            "kanit" => await EvidenceAsync(works, from, to),
            "atama-gecmisi" => await AssignmentHistoryAsync(works, from, to, rq.EmployeeId),
            _ => await SummaryAsync(works, from, to)
        };
        return new ReportTable { Definition = def, Columns = t.Columns, Rows = t.Rows, Notes = t.Notes, FromLocal = fromLocal, ToLocal = toLocal };
    }

    private record Table(List<string> Columns, List<string[]> Rows, List<string> Notes);

    private static string N(decimal d) => d.ToString("0.###", TrTime.Culture);
    private static string Hours(TimeSpan s) => Math.Round(s.TotalHours, 1).ToString("0.0", TrTime.Culture);

    private async Task<Table> SummaryAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var created = works.Where(w => w.CreatedAtUtc >= from && w.CreatedAtUtc < to);
        var createdCount = await created.CountAsync();
        var createdCancelled = await created.CountAsync(w => w.Status == WorkStatus.Cancelled);
        var createdCompleted = await created.CountAsync(w => w.Status == WorkStatus.Completed);
        var completed = await works.CountAsync(w => w.Status == WorkStatus.Completed && w.CompletedAtUtc >= from && w.CompletedAtUtc < to);
        var cancelled = await works.CountAsync(w => w.Status == WorkStatus.Cancelled && w.CancelledAtUtc >= from && w.CancelledAtUtc < to);
        var active = await works.CountAsync(w => w.Status != WorkStatus.Completed && w.Status != WorkStatus.Cancelled);
        var assignments = await db.WorkAssignments.CountAsync(a => works.Any(w => w.Id == a.WorkOrderId) && a.AssignedAtUtc >= from && a.AssignedAtUtc < to);
        var denominator = createdCount - createdCancelled;
        var rate = denominator == 0 ? "—" : (createdCompleted * 100.0 / denominator).ToString("0.0", TrTime.Culture) + " %";

        return new Table(["Ölçüt", "Değer", "Tanım"],
        [
            ["Oluşturulan iş", createdCount.ToString(), "Oluşturulma zamanı dönem içinde olan benzersiz iş sayısı"],
            ["Tamamlanan iş", completed.ToString(), "Şef onayı (tamamlanma) zamanı dönem içinde olan işler"],
            ["İptal edilen iş", cancelled.ToString(), "İptal zamanı dönem içinde olan işler"],
            ["Aktif iş (bugün)", active.ToString(), "Tamamlanmamış ve iptal edilmemiş tüm işler"],
            ["Personel ataması", assignments.ToString(), "Dönemde yapılan iş-personel atama sayısı (iş sayısı değildir)"],
            ["Tamamlanma oranı", rate, $"Dönemde oluşturulup tamamlanan ({createdCompleted}) / dönemde oluşturulup iptal edilmeyen ({denominator})"],
        ], ["İptal edilen işler tamamlanma oranının paydasından çıkarılır."]);
    }

    private static IQueryable<Domain.Entities.WorkOrder> InPlan(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to) =>
        works.Where(w => w.PlannedStartAtUtc < to && w.PlannedEndAtUtc > from);

    private async Task<Table> StatusAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var rows = await InPlan(works, from, to).GroupBy(w => w.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var total = rows.Sum(r => r.Count);
        return new Table(["Durum", "İş sayısı", "Pay"],
            rows.OrderBy(r => r.Key).Select(r => new[] { TrNames.Of(r.Key), r.Count.ToString(),
                total == 0 ? "—" : (r.Count * 100.0 / total).ToString("0.0", TrTime.Culture) + " %" })
                .Append(["Toplam", total.ToString(), ""]).ToList(), []);
    }

    private async Task<Table> PersonnelAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var plan = InPlan(works, from, to);
        var rows = await db.WorkAssignments.AsNoTracking().Where(a => plan.Any(w => w.Id == a.WorkOrderId))
            .Select(a => new
            {
                a.EmployeeId, Name = a.Employee.FirstName + " " + a.Employee.LastName, a.Employee.IsActive, Role = a.JobRole.Name,
                a.WorkOrderId, a.AssignmentStatus, a.WorkOrder.PlannedStartAtUtc, a.WorkOrder.PlannedEndAtUtc
            }).ToListAsync();
        var result = rows.GroupBy(r => new { r.EmployeeId, r.Name, r.IsActive })
            .Select(g =>
            {
                var notRejected = g.Where(x => x.AssignmentStatus != AssignmentStatus.Rejected).ToList();
                var works = notRejected.DistinctBy(x => x.WorkOrderId).ToList();
                return new[]
                {
                    g.Key.Name + (g.Key.IsActive ? "" : " (pasif)"),
                    works.Count.ToString(),
                    g.Count().ToString(),
                    g.Count(x => x.AssignmentStatus == AssignmentStatus.Finished).ToString(),
                    g.Count(x => x.AssignmentStatus == AssignmentStatus.Rejected).ToString(),
                    string.Join(", ", notRejected.Select(x => x.Role).Distinct()),
                    Hours(TimeSpan.FromTicks(works.Sum(x => (x.PlannedEndAtUtc - x.PlannedStartAtUtc).Ticks)))
                };
            }).OrderBy(r => r[0], StringComparer.Create(TrTime.Culture, true)).ToList();
        return new Table(["Personel", "İş sayısı", "Atama sayısı", "Tamamlanan atama", "Reddettiği", "İşteki rolleri", "Planlanan süre (saat)"], result,
        [
            "İş sayısı: reddedilmemiş atamalarının bulunduğu benzersiz iş. Atama sayısı: tüm atama kayıtları (ret dahil).",
            "Bu tablo performans ölçütü değildir; işin türü, zorluğu, ekip büyüklüğü ve süre birlikte değerlendirilmelidir."
        ]);
    }

    private async Task<Table> SupervisorAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var pending = await works.Where(w => w.Status == WorkStatus.CompletionApprovalPending && w.ResponsibleSupervisorEmployeeId != null)
            .GroupBy(w => w.ResponsibleSupervisorEmployeeId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var transitions = await db.WorkStatusHistories.AsNoTracking()
            .Where(h => h.ChangedAtUtc >= from && h.ChangedAtUtc < to && (h.ToStatus == WorkStatus.Completed || h.ToStatus == WorkStatus.ReturnedBySupervisor))
            .Join(works, h => h.WorkOrderId, w => w.Id, (h, w) => new { w.ResponsibleSupervisorEmployeeId, h.ToStatus })
            .ToListAsync();
        var ids = pending.Select(p => p.Key).Concat(transitions.Select(t => t.ResponsibleSupervisorEmployeeId)).Where(x => x != null).Distinct().ToList();
        var names = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.FirstName + " " + e.LastName);
        var rows = ids.Select(id => new[]
        {
            names.GetValueOrDefault(id!.Value) ?? "—",
            (pending.FirstOrDefault(p => p.Key == id)?.Count ?? 0).ToString(),
            transitions.Count(t => t.ResponsibleSupervisorEmployeeId == id && t.ToStatus == WorkStatus.Completed).ToString(),
            transitions.Count(t => t.ResponsibleSupervisorEmployeeId == id && t.ToStatus == WorkStatus.ReturnedBySupervisor).ToString()
        }).OrderBy(r => r[0]).ToList();
        return new Table(["Şef", "Şu an onay bekleyen", "Dönemde onaylanan", "Dönemde iade edilen"], rows,
            ["Onay bekleyen sayısı bugünkü durumdur; onay ve iade sayıları dönem içindeki durum değişikliklerinden sayılır."]);
    }

    private async Task<Table> TeamAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var plan = InPlan(works, from, to);
        var rows = await db.WorkAssignments.AsNoTracking()
            .Where(a => plan.Any(w => w.Id == a.WorkOrderId) && a.AssignmentStatus != AssignmentStatus.Rejected)
            .Select(a => new { Team = a.Employee.Team != null ? a.Employee.Team.Name : "(ekipsiz)", a.WorkOrderId, a.EmployeeId })
            .ToListAsync();
        return new Table(["Ekip", "İş sayısı", "Atama sayısı", "Görev alan kişi"],
            rows.GroupBy(r => r.Team).OrderByDescending(g => g.Select(x => x.WorkOrderId).Distinct().Count())
                .Select(g => new[] { g.Key, g.Select(x => x.WorkOrderId).Distinct().Count().ToString(), g.Count().ToString(),
                    g.Select(x => x.EmployeeId).Distinct().Count().ToString() }).ToList(),
            ["Ekip, personelin bugünkü ekibine göre gruplanır. Reddedilen atamalar sayılmaz."]);
    }

    private async Task<Table> OverdueAsync(IQueryable<Domain.Entities.WorkOrder> works)
    {
        var now = DateTime.UtcNow;
        var rows = await works.Where(w => w.Status != WorkStatus.Completed && w.Status != WorkStatus.Cancelled && w.PlannedEndAtUtc < now)
            .OrderBy(w => w.PlannedEndAtUtc)
            .Select(w => new
            {
                w.WorkNumber, w.Title, w.Status, w.PlannedEndAtUtc,
                Sup = w.ResponsibleSupervisorEmployee != null ? w.ResponsibleSupervisorEmployee.FirstName + " " + w.ResponsibleSupervisorEmployee.LastName : "—"
            }).Take(1000).ToListAsync();
        return new Table(["İş no", "Başlık", "Durum", "Planlanan bitiş", "Gecikme", "Sorumlu şef"],
            rows.Select(r => new[] { r.WorkNumber, r.Title, TrNames.Of(r.Status), TrTime.Format(r.PlannedEndAtUtc), TrTime.Duration(now - r.PlannedEndAtUtc), r.Sup }).ToList(),
            ["Gecikme = şimdiki zaman − planlanan bitiş. Tarih süzgeci bu rapora uygulanmaz; bugünkü durumu gösterir."]);
    }

    private async Task<Table> MaterialAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var rows = await db.WorkMaterials.AsNoTracking()
            .Where(m => m.AddedAtUtc >= from && m.AddedAtUtc < to && works.Any(w => w.Id == m.WorkOrderId))
            .Select(m => new { m.MaterialNameSnapshot, m.UnitSnapshot, m.Quantity, m.WorkOrderId, m.MaterialCatalog.Code })
            .ToListAsync();
        return new Table(["Kod", "Malzeme", "Birim", "Toplam miktar", "İş sayısı"],
            rows.GroupBy(r => new { r.Code, r.MaterialNameSnapshot, r.UnitSnapshot }).OrderBy(g => g.Key.MaterialNameSnapshot)
                .Select(g => new[] { g.Key.Code, g.Key.MaterialNameSnapshot, g.Key.UnitSnapshot, N(g.Sum(x => x.Quantity)),
                    g.Select(x => x.WorkOrderId).Distinct().Count().ToString() }).ToList(),
            ["Yalnız işte bildirilen tüketimdir; gerçek stok/ambar bakiyesi tutulmaz."]);
    }

    private async Task<Table> ReasonsAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var rejections = await db.WorkAssignments.AsNoTracking()
            .Where(a => a.AssignmentStatus == AssignmentStatus.Rejected && a.RespondedAtUtc >= from && a.RespondedAtUtc < to
                        && works.Any(w => w.Id == a.WorkOrderId))
            .Select(a => new { At = a.RespondedAtUtc!.Value, a.WorkOrder.WorkNumber, a.WorkOrder.Title,
                Who = a.Employee.FirstName + " " + a.Employee.LastName, a.RejectionReason })
            .ToListAsync();
        var history = await db.WorkStatusHistories.AsNoTracking()
            .Where(h => h.ChangedAtUtc >= from && h.ChangedAtUtc < to
                        && (h.ToStatus == WorkStatus.ReturnedBySupervisor || h.ToStatus == WorkStatus.Cancelled))
            .Join(works, h => h.WorkOrderId, w => w.Id, (h, w) => new { h.ChangedAtUtc, w.WorkNumber, w.Title, h.ChangedByUserId, h.ToStatus, h.Reason })
            .ToListAsync();
        var userIds = history.Select(h => h.ChangedByUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName);
        var rows = rejections.Select(r => (r.At, new[] { TrTime.Format(r.At), r.WorkNumber, r.Title, "Personel reddi", r.Who, r.RejectionReason ?? "" }))
            .Concat(history.Select(h => (h.ChangedAtUtc, new[] { TrTime.Format(h.ChangedAtUtc), h.WorkNumber, h.Title,
                h.ToStatus == WorkStatus.Cancelled ? "İptal" : "Şef iadesi", names.GetValueOrDefault(h.ChangedByUserId) ?? "—", h.Reason ?? "" })))
            .OrderByDescending(x => x.Item1).Select(x => x.Item2).ToList();
        return new Table(["Tarih", "İş no", "Başlık", "Tür", "Kim", "Gerekçe"], rows, []);
    }

    private async Task<Table> DurationAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var rows = await works.Where(w => w.Status == WorkStatus.Completed && w.CompletedAtUtc >= from && w.CompletedAtUtc < to)
            .OrderBy(w => w.CompletedAtUtc)
            .Select(w => new { w.WorkNumber, w.Title, w.PlannedStartAtUtc, w.PlannedEndAtUtc, w.ActualStartAtUtc, w.CompletionSubmittedAtUtc, w.CompletedAtUtc })
            .ToListAsync();
        return new Table(["İş no", "Başlık", "Planlanan başlangıç", "Planlanan bitiş", "Planlanan süre (saat)", "Gerçek başlangıç",
                "Tamamlanma bildirimi", "Gerçekleşen süre (saat)", "Fark (saat)"],
            rows.Select(r =>
            {
                var planned = r.PlannedEndAtUtc - r.PlannedStartAtUtc;
                TimeSpan? actual = r.ActualStartAtUtc is DateTime s && r.CompletionSubmittedAtUtc is DateTime e ? e - s : null;
                return new[] { r.WorkNumber, r.Title, TrTime.Format(r.PlannedStartAtUtc), TrTime.Format(r.PlannedEndAtUtc), Hours(planned),
                    TrTime.Format(r.ActualStartAtUtc), TrTime.Format(r.CompletionSubmittedAtUtc),
                    actual is null ? "eksik veri" : Hours(actual.Value), actual is null ? "—" : Hours(actual.Value - planned) };
            }).ToList(),
            ["Gerçekleşen süre = ilk çalışma başlangıcı → son tamamlanma bildirimi. Başlangıç kaydı yoksa süre hesaplanmaz (\"eksik veri\")."]);
    }

    private async Task<Table> EvidenceAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to)
    {
        var rows = await InPlan(works, from, to).OrderBy(w => w.PlannedStartAtUtc)
            .Select(w => new
            {
                w.WorkNumber, w.Title, w.Status,
                Photos = w.Attachments.Count(a => a.DeletedAtUtc == null && a.ContentType.StartsWith("image/")),
                Docs = w.Attachments.Count(a => a.DeletedAtUtc == null && !a.ContentType.StartsWith("image/")),
                Notes = w.Notes.Count(), Materials = w.Materials.Count()
            }).Take(2000).ToListAsync();
        static string YesNo(int n) => n > 0 ? $"Var ({n})" : "Yok";
        return new Table(["İş no", "Başlık", "Durum", "Fotoğraf", "Belge", "Not", "Malzeme"],
            rows.Select(r => new[] { r.WorkNumber, r.Title, TrNames.Of(r.Status), YesNo(r.Photos), YesNo(r.Docs), YesNo(r.Notes), YesNo(r.Materials) }).ToList(), []);
    }

    private async Task<Table> AssignmentHistoryAsync(IQueryable<Domain.Entities.WorkOrder> works, DateTime from, DateTime to, int? employeeId)
    {
        var q = db.WorkAssignments.AsNoTracking().Where(a => a.AssignedAtUtc >= from && a.AssignedAtUtc < to && works.Any(w => w.Id == a.WorkOrderId));
        if (employeeId is int e) q = q.Where(a => a.EmployeeId == e);
        var rows = await q.OrderBy(a => a.Employee.FirstName).ThenBy(a => a.AssignedAtUtc)
            .Select(a => new
            {
                Name = a.Employee.FirstName + " " + a.Employee.LastName, a.WorkOrder.WorkNumber, a.WorkOrder.Title, Role = a.JobRole.Name,
                a.AssignmentStatus, a.AssignedAtUtc, a.StartedAtUtc, a.ReleasedAtUtc, a.ReleaseReason
            }).Take(5000).ToListAsync();
        return new Table(["Personel", "İş no", "Başlık", "Rol", "Atama durumu", "Atanma", "Çalışma başlangıcı", "Ayrılma", "Ayrılma nedeni"],
            rows.Select(r => new[] { r.Name, r.WorkNumber, r.Title, r.Role, TrNames.Of(r.AssignmentStatus), TrTime.Format(r.AssignedAtUtc),
                TrTime.Format(r.StartedAtUtc), TrTime.Format(r.ReleasedAtUtc), r.ReleaseReason ?? "" }).ToList(), []);
    }

    /// <summary>
    /// Excel'in Türkçe ayarlarıyla doğrudan açılan CSV: UTF-8 BOM, noktalı virgül ayırıcı.
    /// Formül enjeksiyonuna karşı =, +, -, @ ile başlayan hücreler kaçışlanır.
    /// </summary>
    public static byte[] ToCsv(ReportTable t)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Cell(t.Definition.Title) + ";" + Cell($"{t.FromLocal:dd.MM.yyyy} – {t.ToLocal:dd.MM.yyyy}") + ";" + Cell("Tarih alanı: " + t.Definition.DateField));
        sb.AppendLine(string.Join(';', t.Columns.Select(Cell)));
        foreach (var r in t.Rows) sb.AppendLine(string.Join(';', r.Select(Cell)));
        foreach (var n in t.Notes) sb.AppendLine(Cell("Not: " + n));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Cell(string? v)
    {
        var s = v ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, TrTime.Culture, out _)) s = "'" + s;
        return s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
