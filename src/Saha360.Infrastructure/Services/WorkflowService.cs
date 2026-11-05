using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

/// <summary>
/// İş durumlarını değiştiren TEK yer. Her geçişte: yetki → mevcut durum → zorunlu alanlar → durum + geçmiş + denetim
/// tek transaction'da → bildirimler. Aynı istek ikinci kez gelirse işlem tekrarlanmaz (OpResult.NoOp).
/// </summary>
public class WorkflowService(AppDbContext db, AccessService access, AuditService audit, NotificationService notifications)
{
    public const int MinReasonLength = 5;
    internal const string ConcurrencyMessage =
        "İş bu sırada başka bir kullanıcı tarafından güncellendi. Sayfayı yenileyip tekrar deneyin.";

    // ───────────────────────── Personel eylemleri ─────────────────────────

    public Task<OpResult> AcceptAsync(UserContext u, int workId) => RunAsync(async () =>
    {
        var w = await access.LoadVisibleAsync(u, workId);
        var a = access.ActiveAssignmentOf(u, w) ?? throw new ForbiddenException("Bu işe atanmış değilsiniz.");
        if (a.AssignmentStatus is AssignmentStatus.Accepted or AssignmentStatus.Working)
            return OpResult.NoOp("İşi zaten kabul ettiniz.");
        if (w.Status == WorkStatus.Draft || WorkflowRules.IsFinal(w.Status))
            throw new DomainException($"İş '{TrNames.Of(w.Status)}' durumunda; kabul edilemez.");

        var now = DateTime.UtcNow;
        a.AssignmentStatus = AssignmentStatus.Accepted;
        a.RespondedAtUtc = now;
        if (!string.IsNullOrWhiteSpace(w.Instructions)) a.InstructionAcknowledgedAtUtc = now;
        audit.Add(u, "AssignmentAccepted", "WorkAssignment", a.Id, $"{a.Employee.FullName} işi kabul etti.", w.Id);
        Recompute(w, u);
        Touch(w);
        return OpResult.Done("İşi kabul ettiniz.");
    });

    public Task<OpResult> RejectAsync(UserContext u, int workId, string? reason) => RunAsync(async () =>
    {
        var text = RequireReason(reason, "Ret gerekçesi");
        var w = await access.LoadVisibleAsync(u, workId);
        var a = access.ActiveAssignmentOf(u, w);
        if (a is null)
        {
            var rejected = u.EmployeeId is int e && w.Assignments.Any(x => x.EmployeeId == e && x.AssignmentStatus == AssignmentStatus.Rejected);
            return rejected ? OpResult.NoOp("Bu işi zaten reddettiniz.") : throw new ForbiddenException("Bu işe atanmış değilsiniz.");
        }
        if (a.AssignmentStatus == AssignmentStatus.Working || WorkflowRules.IsStarted(w.Status))
            throw new DomainException("Çalışma başladıktan sonra iş reddedilemez. Durumu not olarak şefinize iletin.");
        if (w.Status == WorkStatus.Draft || WorkflowRules.IsFinal(w.Status))
            throw new DomainException($"İş '{TrNames.Of(w.Status)}' durumunda; reddedilemez.");

        var now = DateTime.UtcNow;
        a.AssignmentStatus = AssignmentStatus.Rejected;
        a.RejectionReason = text;
        a.RespondedAtUtc = now;
        a.ReleasedAtUtc = now;
        a.ReleaseReason = "Personel reddetti";
        audit.Add(u, "AssignmentRejected", "WorkAssignment", a.Id, $"{a.Employee.FullName} işi reddetti: {text}", w.Id);
        Recompute(w, u);
        Touch(w);
        await db.SaveChangesAsync();

        // Ret işi silmez, sessizce havuza da döndürmez: şef ve planlamacı karar versin.
        var supervisor = await SupervisorUserIdAsync(w);
        await notifications.AddAsync([supervisor, w.CreatedByUserId, w.ManagerUserId], w, NotificationType.AssignmentRejected,
            "Personel işi reddetti", $"{a.Employee.FullName}: {text}", $"as:{a.Id}:rejected", u.UserId);
        return OpResult.Done("Ret gerekçeniz kaydedildi; şef ve planlamacı bilgilendirildi.");
    });

    public Task<OpResult> StartAsync(UserContext u, int workId) => RunAsync(async () =>
    {
        var w = await access.LoadVisibleAsync(u, workId);
        var a = access.ActiveAssignmentOf(u, w) ?? throw new ForbiddenException("Bu işe atanmış değilsiniz.");
        if (a.AssignmentStatus == AssignmentStatus.Working && w.Status == WorkStatus.InProgress)
            return OpResult.NoOp("Zaten çalışıyor olarak işaretlisiniz.");
        if (a.AssignmentStatus == AssignmentStatus.Assigned)
            throw new DomainException("Önce işi kabul etmelisiniz.");
        if (w.Status is not (WorkStatus.Planned or WorkStatus.InProgress or WorkStatus.ReturnedBySupervisor))
            throw new DomainException(w.Status == WorkStatus.AwaitingAcceptance
                ? "Ekipteki herkes yanıt vermeden iş başlatılamaz."
                : $"İş '{TrNames.Of(w.Status)}' durumunda; çalışma başlatılamaz.");

        var now = DateTime.UtcNow;
        a.AssignmentStatus = AssignmentStatus.Working;
        a.StartedAtUtc ??= now;
        var key = $"wo:{w.Id}:started:{w.UpdatedAtUtc.Ticks}";
        var workStarted = w.Status != WorkStatus.InProgress;
        if (workStarted)
        {
            Transition(w, WorkStatus.InProgress, u, w.Status == WorkStatus.ReturnedBySupervisor ? "İade sonrası çalışmaya devam" : null);
            w.ActualStartAtUtc ??= now;
        }
        audit.Add(u, "WorkStarted", "WorkAssignment", a.Id, $"{a.Employee.FullName} çalışmaya başladı.", w.Id);
        Touch(w);
        if (workStarted)
            await notifications.AddAsync([await SupervisorUserIdAsync(w)], w, NotificationType.WorkStarted, "Çalışma başladı",
                $"{a.Employee.FullName} işe başladı: {w.Title}", key, u.UserId);
        return OpResult.Done("Çalışmaya başladınız.");
    });

    public Task<OpResult> SubmitCompletionAsync(UserContext u, int workId, string? note) => RunAsync(async () =>
    {
        var text = RequireReason(note, "Tamamlanma açıklaması");
        var w = await access.LoadVisibleAsync(u, workId);
        var a = access.ActiveAssignmentOf(u, w) ?? throw new ForbiddenException("Bu işe atanmış değilsiniz.");
        if (w.Status == WorkStatus.CompletionApprovalPending) return OpResult.NoOp("Tamamlanma bildirimi zaten şefe gönderildi.");
        if (w.Status == WorkStatus.Completed) return OpResult.NoOp("İş zaten tamamlandı.");
        if (w.Status is not (WorkStatus.InProgress or WorkStatus.ReturnedBySupervisor))
            throw new DomainException("Yalnız devam eden veya iade edilen iş tamamlandı olarak gönderilebilir.");
        if (a.AssignmentStatus == AssignmentStatus.Assigned) throw new DomainException("Önce işi kabul etmelisiniz.");

        var key = $"wo:{w.Id}:submitted:{w.UpdatedAtUtc.Ticks}";
        a.AssignmentStatus = AssignmentStatus.Working;
        w.CompletionNote = text;
        w.CompletionSubmittedAtUtc = DateTime.UtcNow;
        Transition(w, WorkStatus.CompletionApprovalPending, u, text);
        Touch(w);

        var supervisor = await SupervisorUserIdAsync(w);
        await notifications.AddAsync([supervisor ?? w.ManagerUserId ?? w.CreatedByUserId], w, NotificationType.CompletionSubmitted,
            "Tamamlanma onayı bekliyor", $"{a.Employee.FullName}: {text}", key, u.UserId);
        return OpResult.Done("İş tamamlandı olarak şefe gönderildi.");
    });

    // ───────────────────────── Şef eylemleri ─────────────────────────

    public Task<OpResult> ApproveAsync(UserContext u, int workId, string? note) => RunAsync(async () =>
    {
        var w = await access.LoadVisibleAsync(u, workId);
        if (!access.CanApprove(u, w)) throw new ForbiddenException("Bu işin tamamlanma onayını yalnız sorumlu şef verebilir.");
        if (w.Status == WorkStatus.Completed) return OpResult.NoOp("İş zaten onaylanmış.");
        if (w.Status != WorkStatus.CompletionApprovalPending) throw new DomainException("Yalnız onay bekleyen iş onaylanabilir.");

        var now = DateTime.UtcNow;
        var reason = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Transition(w, WorkStatus.Completed, u, reason);
        w.CompletedAtUtc = now;
        // Şef onayı tamamlanmayı kesinleştirir; ekip serbest kalır ve havuz yeniden hesaplanır.
        foreach (var a in w.Assignments.Where(a => a.ReleasedAtUtc == null))
        {
            a.AssignmentStatus = AssignmentStatus.Finished;
            a.FinishedAtUtc = now;
            a.ReleasedAtUtc = now;
            a.ReleaseReason = "İş tamamlandı";
        }
        Touch(w);

        var team = await notifications.UserIdsOfEmployeesAsync(w.Assignments.Where(a => a.FinishedAtUtc == now).Select(a => (int?)a.EmployeeId));
        await notifications.AddAsync([w.ManagerUserId ?? w.CreatedByUserId, w.CreatedByUserId], w, NotificationType.Approved,
            "İş tamamlandı (şef onayladı)", $"{u.FullName} işi onayladı: {w.Title}", $"wo:{w.Id}:approved", u.UserId);
        await notifications.AddAsync(team, w, NotificationType.Approved, "İşiniz onaylandı", $"{u.FullName} işi onayladı.",
            $"wo:{w.Id}:approved", u.UserId);
        return OpResult.Done("İş onaylandı ve tamamlandı. Yönetici bilgilendirildi.");
    });

    public Task<OpResult> ReturnAsync(UserContext u, int workId, string? reason) => RunAsync(async () =>
    {
        var text = RequireReason(reason, "İade gerekçesi");
        var w = await access.LoadVisibleAsync(u, workId);
        if (!access.CanApprove(u, w)) throw new ForbiddenException("Bu işi yalnız sorumlu şef iade edebilir.");
        if (w.Status == WorkStatus.ReturnedBySupervisor) return OpResult.NoOp("İş zaten iade edilmiş.");
        if (w.Status != WorkStatus.CompletionApprovalPending) throw new DomainException("Yalnız onay bekleyen iş iade edilebilir.");

        var key = $"wo:{w.Id}:returned:{w.UpdatedAtUtc.Ticks}";
        Transition(w, WorkStatus.ReturnedBySupervisor, u, text);
        Touch(w);
        var team = await notifications.UserIdsOfEmployeesAsync(w.Assignments.Where(a => a.ReleasedAtUtc == null).Select(a => (int?)a.EmployeeId));
        await notifications.AddAsync(team, w, NotificationType.Returned, "İş düzeltme için iade edildi", $"{u.FullName}: {text}", key, u.UserId);
        return OpResult.Done("İş düzeltme için personele iade edildi.");
    });

    /// <summary>Ek bilgi iste: iş onay beklemede kalır; not ve bildirim oluşur.</summary>
    public Task<OpResult> RequestInfoAsync(UserContext u, int workId, string? question) => RunAsync(async () =>
    {
        var text = RequireReason(question, "İstenen bilgi");
        var w = await access.LoadVisibleAsync(u, workId);
        if (!access.CanApprove(u, w)) throw new ForbiddenException("Ek bilgiyi yalnız sorumlu şef isteyebilir.");
        if (w.Status != WorkStatus.CompletionApprovalPending) throw new DomainException("Ek bilgi yalnız onay bekleyen işte istenebilir.");

        var noteEntity = new WorkNote { WorkOrderId = w.Id, AuthorUserId = u.UserId, NoteText = $"[Şef ek bilgi istedi] {text}" };
        db.WorkNotes.Add(noteEntity);
        audit.Add(u, "InfoRequested", "WorkOrder", w.Id, $"Şef ek bilgi istedi: {text}", w.Id);
        Touch(w);
        await db.SaveChangesAsync();
        var team = await notifications.UserIdsOfEmployeesAsync(w.Assignments.Where(a => a.ReleasedAtUtc == null).Select(a => (int?)a.EmployeeId));
        await notifications.AddAsync(team, w, NotificationType.InfoRequested, "Şef ek bilgi istedi", text, $"note:{noteEntity.Id}", u.UserId);
        return OpResult.Done("Ek bilgi talebi personele iletildi. İş onay beklemeye devam ediyor.");
    });

    // ───────────────────────── Planlamacı eylemleri ─────────────────────────

    public Task<OpResult> CancelAsync(UserContext u, int workId, string? reason) => RunAsync(async () =>
    {
        var text = RequireReason(reason, "İptal gerekçesi");
        var w = await access.LoadVisibleAsync(u, workId);
        if (!access.CanCancelWork(u, w)) throw new ForbiddenException("Bu işi iptal etme yetkiniz yok.");
        if (w.Status == WorkStatus.Cancelled) return OpResult.NoOp("İş zaten iptal edilmiş.");

        var wasSent = w.Status != WorkStatus.Draft;
        var now = DateTime.UtcNow;
        Transition(w, WorkStatus.Cancelled, u, text);
        w.CancelledAtUtc = now;
        var released = w.Assignments.Where(a => a.ReleasedAtUtc == null).ToList();
        foreach (var a in released)
        {
            a.AssignmentStatus = AssignmentStatus.Released;
            a.ReleasedAtUtc = now;
            a.ReleaseReason = "İş iptal edildi";
        }
        Touch(w);
        if (wasSent)
        {
            var team = await notifications.UserIdsOfEmployeesAsync(released.Select(a => (int?)a.EmployeeId));
            team.Add((await SupervisorUserIdAsync(w))!);
            await notifications.AddAsync(team, w, NotificationType.Cancelled, "İş iptal edildi", text, $"wo:{w.Id}:cancelled", u.UserId);
        }
        return OpResult.Done("İş iptal edildi; ekipteki personel serbest bırakıldı.");
    });

    // ───────────────────────── Ortak yardımcılar ─────────────────────────

    /// <summary>Durum geçişi: matristen geçmeyen hiçbir değişiklik yapılmaz. Geçmiş ve denetim kaydı aynı anda yazılır.</summary>
    internal void Transition(WorkOrder w, WorkStatus to, UserContext u, string? reason)
    {
        if (!WorkflowRules.CanTransition(w.Status, to))
            throw new DomainException($"İş '{TrNames.Of(w.Status)}' durumundayken '{TrNames.Of(to)}' durumuna geçemez.");
        var from = w.Status;
        db.WorkStatusHistories.Add(new WorkStatusHistory
        {
            WorkOrderId = w.Id, FromStatus = from, ToStatus = to, ChangedByUserId = u.UserId, Reason = reason
        });
        audit.Add(u, "StatusChanged", "WorkOrder", w.Id,
            $"Durum: {TrNames.Of(from)} → {TrNames.Of(to)}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — {reason}"), w.Id);
        w.Status = to;
    }

    /// <summary>
    /// Ekip yanıtlarına göre genel durumu günceller: herkes kabul ettiyse Planlandı; ekipte kimse kalmadıysa Atama Bekliyor;
    /// planlı işe yeni kişi eklendiyse tekrar Kabul Bekliyor. Bir kişinin reddi tüm işi reddetmez.
    /// </summary>
    internal void Recompute(WorkOrder w, UserContext u)
    {
        if (w.Status is not (WorkStatus.AwaitingAcceptance or WorkStatus.Planned)) return;
        var active = w.Assignments.Where(a => a.ReleasedAtUtc == null).ToList();
        if (active.Count == 0)
            Transition(w, WorkStatus.AwaitingAssignment, u, "Ekipte kimse kalmadı; yeniden atama gerekiyor.");
        else if (w.Status == WorkStatus.AwaitingAcceptance && active.All(a => a.AssignmentStatus is AssignmentStatus.Accepted or AssignmentStatus.Working))
            Transition(w, WorkStatus.Planned, u, "Ekipteki herkes işi kabul etti.");
        else if (w.Status == WorkStatus.Planned && active.Any(a => a.AssignmentStatus == AssignmentStatus.Assigned))
            Transition(w, WorkStatus.AwaitingAcceptance, u, "Ekibe yeni personel eklendi; yanıtı bekleniyor.");
    }

    internal async Task<string?> SupervisorUserIdAsync(WorkOrder w) =>
        w.ResponsibleSupervisorEmployeeId is int id
            ? await db.Employees.Where(e => e.Id == id).Select(e => e.ApplicationUserId).FirstOrDefaultAsync()
            : null;

    internal static void Touch(WorkOrder w) => w.UpdatedAtUtc = DateTime.UtcNow;

    public static string RequireReason(string? text, string field)
    {
        var t = text?.Trim() ?? "";
        if (t.Length < MinReasonLength) throw new DomainException($"{field} zorunludur (en az {MinReasonLength} karakter).");
        if (t.Length > 2000) throw new DomainException($"{field} en fazla 2000 karakter olabilir.");
        return t;
    }

    /// <summary>İşlemi tek transaction'da yürütür; eşzamanlı değişiklikte anlaşılır mesaj verir.</summary>
    internal async Task<OpResult> RunAsync(Func<Task<OpResult>> op)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var result = await op();
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new DomainException(ConcurrencyMessage);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
