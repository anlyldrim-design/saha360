using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

/// <summary>İş oluşturma, düzenleme ve listeleme. Durum değişikliği yapmaz (o iş WorkflowService'in).</summary>
public class WorkOrderService(AppDbContext db, AccessService access, AvailabilityService availability, WorkflowService workflow,
    WorkContentService content, AuditService audit, NotificationService notifications)
{
    public async Task<WorkOrder> CreateAsync(UserContext u, WorkOrderInput input, IReadOnlyList<MaterialLineInput>? materials = null,
        IReadOnlyList<UploadInput>? uploads = null)
    {
        if (!u.CanCreateWork) throw new ForbiddenException("İş oluşturma yetkiniz yok.");
        var (start, end) = await ValidateAsync(u, input);

        var w = new WorkOrder
        {
            WorkNumber = "TMP-" + Guid.NewGuid().ToString("N")[..20],
            CreatedByUserId = u.UserId,
            ManagerUserId = string.IsNullOrEmpty(input.ManagerUserId) ? u.UserId : input.ManagerUserId,
        };
        Apply(w, input, start, end);

        var savedKeys = new List<string>();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            db.WorkOrders.Add(w);
            await db.SaveChangesAsync();
            // Numara kimlikten türetilir: benzersizdir ve yarış durumunda çakışmaz.
            w.WorkNumber = $"IS-{TrTime.ToLocal(w.CreatedAtUtc).Year}-{w.Id:D5}";
            db.WorkStatusHistories.Add(new WorkStatusHistory
            {
                WorkOrderId = w.Id, FromStatus = WorkStatus.Draft, ToStatus = WorkStatus.Draft, ChangedByUserId = u.UserId,
                Reason = "İş taslak olarak oluşturuldu."
            });
            audit.Add(u, "WorkCreated", "WorkOrder", w.Id, $"İş oluşturuldu: {w.Title}", w.Id);

            foreach (var m in MergeLines(materials ?? []))
                await content.AddMaterialCoreAsync(u, w, m.MaterialId, m.Quantity, m.Note);
            foreach (var up in uploads ?? [])
                savedKeys.Add(await content.AddAttachmentCoreAsync(u, w, up));

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return w;
        }
        catch
        {
            db.ChangeTracker.Clear();
            content.DeleteFiles(savedKeys);
            throw;
        }
    }

    public async Task<OpResult> UpdateAsync(UserContext u, int workId, WorkOrderInput input, long? expectedVersion)
    {
        var (start, end) = await ValidateAsync(u, input);
        return await workflow.RunAsync(async () =>
        {
            var w = await access.LoadVisibleAsync(u, workId);
            if (!access.CanEditWork(u, w)) throw new ForbiddenException("Bu işi düzenleme yetkiniz yok.");
            if (!WorkflowRules.CanEditDetails(w.Status)) throw new DomainException($"İş '{TrNames.Of(w.Status)}' durumunda; düzenlenemez.");
            if (expectedVersion is long v && v != w.UpdatedAtUtc.Ticks)
                throw new DomainException("Bu iş siz düzenlerken başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin.");
            if (WorkflowRules.IsStarted(w.Status) && input.ResponsibleUnitId != w.ResponsibleUnitId)
                throw new DomainException("Başlamış işin sorumlu birimi değiştirilemez.");

            var rescheduled = start != w.PlannedStartAtUtc || end != w.PlannedEndAtUtc;
            if (rescheduled)
            {
                // Yeni zaman aralığında ekipteki herkes hâlâ uygun mu?
                var team = w.Assignments.Where(a => a.ReleasedAtUtc == null).ToList();
                var eval = await availability.EvaluateAsync(team.Select(a => a.EmployeeId).ToList(), start, end, w.Id);
                var problems = team.Where(a => eval[a.EmployeeId].State is PoolState.Busy or PoolState.OnLeave)
                    .Select(a => $"{a.Employee.FullName}: {eval[a.EmployeeId].Reason}").ToList();
                if (problems.Count > 0)
                    throw new DomainException("Yeni tarihlerde ekipte çakışma var; önce ekibi düzenleyin. " + string.Join(" · ", problems));
            }

            var changes = Diff(w, input, start, end);
            if (changes.Count == 0) return OpResult.NoOp("Değişiklik yapılmadı.");
            var key = $"wo:{w.Id}:changed:{w.UpdatedAtUtc.Ticks}";
            Apply(w, input, start, end);
            if (!string.IsNullOrEmpty(input.ManagerUserId)) w.ManagerUserId = input.ManagerUserId;
            WorkflowService.Touch(w);
            audit.Add(u, rescheduled ? "WorkRescheduled" : "WorkUpdated", "WorkOrder", w.Id, "İş güncellendi: " + string.Join(", ", changes), w.Id);

            if (!WorkflowRules.IsBeforeSend(w.Status))
            {
                var team = await notifications.UserIdsOfEmployeesAsync(w.Assignments.Where(a => a.ReleasedAtUtc == null).Select(a => (int?)a.EmployeeId));
                await notifications.AddAsync(team, w, NotificationType.WorkChanged, rescheduled ? "İş yeniden planlandı" : "İşte değişiklik yapıldı",
                    string.Join(", ", changes), key, u.UserId);
            }
            return OpResult.Done("İş güncellendi.");
        });
    }

    public async Task<PagedResult<WorkListItem>> ListAsync(UserContext u, WorkListQuery f)
    {
        var now = DateTime.UtcNow;
        var q = Filter(access.VisibleWorks(u).AsNoTracking(), f, u, now);

        q = f.Sort switch
        {
            "baslangic" => q.OrderBy(w => w.PlannedStartAtUtc),
            "baslangic_desc" => q.OrderByDescending(w => w.PlannedStartAtUtc),
            "oncelik" => q.OrderByDescending(w => w.Priority).ThenBy(w => w.PlannedStartAtUtc),
            "numara" => q.OrderByDescending(w => w.Id),
            "durum" => q.OrderBy(w => w.Status).ThenBy(w => w.PlannedStartAtUtc),
            _ => q.OrderByDescending(w => w.UpdatedAtUtc)
        };

        var pageSize = Math.Clamp(f.PageSize, 10, 100);
        var page = Math.Max(1, f.Page);
        var total = await q.CountAsync();
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(w => new WorkListItem(w.Id, w.WorkNumber, w.Title,
                w.WorkCategory != null ? w.WorkCategory.Name : null,
                w.WorkLocation != null ? w.WorkLocation.Name + (w.LocationText != null ? " · " + w.LocationText : "") : w.LocationText,
                w.PlannedStartAtUtc, w.PlannedEndAtUtc,
                // Tamamlanan işte ekip serbest bırakılmıştır; işi yapan kişiler sayılır.
                w.Assignments.Count(a => a.ReleasedAtUtc == null || a.AssignmentStatus == AssignmentStatus.Finished),
                w.ResponsibleSupervisorEmployee != null ? w.ResponsibleSupervisorEmployee.FirstName + " " + w.ResponsibleSupervisorEmployee.LastName : null,
                w.Priority, w.Status,
                w.Status != WorkStatus.Completed && w.Status != WorkStatus.Cancelled && w.PlannedEndAtUtc < now,
                w.UpdatedAtUtc))
            .ToListAsync();
        return new PagedResult<WorkListItem> { Items = items, Page = page, PageSize = pageSize, Total = total };
    }

    /// <summary>Liste, panel kartları ve dışa aktarma aynı süzgeci kullanır (sunucu tarafında).</summary>
    public static IQueryable<WorkOrder> Filter(IQueryable<WorkOrder> q, WorkListQuery f, UserContext u, DateTime now)
    {
        switch (f.View)
        {
            case "aktif": q = q.Where(w => w.Status != WorkStatus.Completed && w.Status != WorkStatus.Cancelled); break;
            case "onay": q = q.Where(w => w.Status == WorkStatus.CompletionApprovalPending); break;
            case "iade":
                q = q.Where(w => w.Status == WorkStatus.ReturnedBySupervisor || w.Status == WorkStatus.AwaitingAssignment
                                 || (w.Status == WorkStatus.AwaitingAcceptance && w.Assignments.Any(a => a.AssignmentStatus == AssignmentStatus.Rejected)));
                break;
            case "tamamlanan": q = q.Where(w => w.Status == WorkStatus.Completed); break;
            case "atama": q = q.Where(w => w.Status == WorkStatus.Draft || w.Status == WorkStatus.AwaitingAssignment); break;
            case "devam": q = q.Where(w => w.Status == WorkStatus.InProgress); break;
            case "bugun":
                var todayStart = TrTime.ToUtc(TrTime.TodayLocal);
                var todayEnd = todayStart.AddDays(1);
                q = q.Where(w => w.PlannedStartAtUtc < todayEnd && w.PlannedEndAtUtc > todayStart && w.Status != WorkStatus.Cancelled);
                break;
            case "onayimda":
                var me = u.EmployeeId ?? -1;
                q = q.Where(w => w.Status == WorkStatus.CompletionApprovalPending && w.ResponsibleSupervisorEmployeeId == me);
                break;
        }
        if (f.Status is WorkStatus s) q = q.Where(w => w.Status == s);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var t = f.Q.Trim();
            q = q.Where(w => w.Title.Contains(t) || w.WorkNumber.Contains(t) || (w.LocationText != null && w.LocationText.Contains(t)));
        }
        if (f.UnitId is int unit) q = q.Where(w => w.ResponsibleUnitId == unit);
        if (f.SupervisorId is int sup) q = q.Where(w => w.ResponsibleSupervisorEmployeeId == sup);
        if (f.EmployeeId is int emp) q = q.Where(w => w.Assignments.Any(a => a.EmployeeId == emp));
        if (f.CategoryId is int cat) q = q.Where(w => w.WorkCategoryId == cat);
        if (f.Priority is WorkPriority p) q = q.Where(w => w.Priority == p);
        if (f.LocationId is int loc) q = q.Where(w => w.WorkLocationId == loc);
        // Tarih süzgeci planlanan zaman aralığına uygulanır (ekranda belirtilir).
        if (f.From is DateTime from) { var fu = TrTime.ToUtc(from.Date); q = q.Where(w => w.PlannedEndAtUtc >= fu); }
        if (f.To is DateTime to) { var tu = TrTime.ToUtc(to.Date.AddDays(1)); q = q.Where(w => w.PlannedStartAtUtc < tu); }
        if (f.Overdue) q = q.Where(w => w.Status != WorkStatus.Completed && w.Status != WorkStatus.Cancelled && w.PlannedEndAtUtc < now);
        if (f.HasMaterials) q = q.Where(w => w.Materials.Any());
        return q;
    }

    // ───────────────────────── yardımcılar ─────────────────────────

    private async Task<(DateTime Start, DateTime End)> ValidateAsync(UserContext u, WorkOrderInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) throw new DomainException("İş başlığı zorunludur.");
        if (input.PlannedStartLocal is null || input.PlannedEndLocal is null) throw new DomainException("Planlanan başlangıç ve bitiş zorunludur.");
        var start = TrTime.ToUtc(input.PlannedStartLocal.Value);
        var end = TrTime.ToUtc(input.PlannedEndLocal.Value);
        if (end <= start) throw new DomainException("Planlanan bitiş, başlangıçtan sonra olmalı.");
        if ((end - start).TotalDays > 366) throw new DomainException("Bir işin planlanan süresi bir yılı geçemez.");
        if (!Enum.IsDefined(input.Priority)) throw new DomainException("Geçersiz öncelik.");

        if (input.ResponsibleUnitId is not int unit || !await db.OrganizationUnits.AnyAsync(x => x.Id == unit && x.IsActive))
            throw new DomainException("Geçerli bir sorumlu birim seçin.");
        if (!u.InScope(unit))
            throw new DomainException("Yalnız kendi biriminiz için iş oluşturabilirsiniz.");
        if (input.WorkCategoryId is int cat && !await db.WorkCategories.AnyAsync(x => x.Id == cat && x.IsActive))
            throw new DomainException("Seçilen kategori geçersiz.");
        if (input.WorkLocationId is int loc && !await db.WorkLocations.AnyAsync(x => x.Id == loc && x.IsActive))
            throw new DomainException("Seçilen bölge geçersiz.");
        if (input.ResponsibleSupervisorEmployeeId is int sup
            && !await db.Employees.AnyAsync(e => e.Id == sup && e.IsActive))
            throw new DomainException("Seçilen sorumlu şef geçersiz veya pasif.");
        if (!string.IsNullOrEmpty(input.ManagerUserId) && !await db.Users.AnyAsync(x => x.Id == input.ManagerUserId && x.IsActive))
            throw new DomainException("Seçilen yönetici geçersiz.");
        return (start, end);
    }

    private static void Apply(WorkOrder w, WorkOrderInput i, DateTime start, DateTime end)
    {
        w.Title = i.Title.Trim();
        w.Description = Clean(i.Description);
        w.Instructions = Clean(i.Instructions);
        w.WorkCategoryId = i.WorkCategoryId;
        w.Priority = i.Priority;
        w.WorkLocationId = i.WorkLocationId;
        w.LocationText = Clean(i.LocationText);
        w.PlannedStartAtUtc = start;
        w.PlannedEndAtUtc = end;
        w.ResponsibleUnitId = i.ResponsibleUnitId!.Value;
        w.ResponsibleSupervisorEmployeeId = i.ResponsibleSupervisorEmployeeId;
    }

    private static List<string> Diff(WorkOrder w, WorkOrderInput i, DateTime start, DateTime end)
    {
        var c = new List<string>();
        if (w.Title != i.Title.Trim()) c.Add("başlık");
        if (w.Description != Clean(i.Description)) c.Add("açıklama");
        if (w.Instructions != Clean(i.Instructions)) c.Add("saha talimatı");
        if (w.WorkCategoryId != i.WorkCategoryId) c.Add("kategori");
        if (w.Priority != i.Priority) c.Add($"öncelik ({TrNames.Of(i.Priority)})");
        if (w.WorkLocationId != i.WorkLocationId || w.LocationText != Clean(i.LocationText)) c.Add("konum");
        if (w.PlannedStartAtUtc != start || w.PlannedEndAtUtc != end) c.Add($"plan ({TrTime.Range(start, end)})");
        if (w.ResponsibleUnitId != i.ResponsibleUnitId) c.Add("sorumlu birim");
        if (w.ResponsibleSupervisorEmployeeId != i.ResponsibleSupervisorEmployeeId) c.Add("sorumlu şef");
        if (!string.IsNullOrEmpty(i.ManagerUserId) && w.ManagerUserId != i.ManagerUserId) c.Add("bilgilendirilecek yönetici");
        return c;
    }

    /// <summary>Aynı malzeme birden çok kez seçildiyse miktarlar birleştirilir.</summary>
    public static List<MaterialLineInput> MergeLines(IEnumerable<MaterialLineInput> lines) =>
        lines.GroupBy(l => l.MaterialId)
            .Select(g => new MaterialLineInput(g.Key, g.Sum(x => x.Quantity),
                string.Join(" / ", g.Select(x => x.Note).Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } n ? n : null))
            .ToList();

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
