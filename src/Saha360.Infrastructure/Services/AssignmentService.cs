using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

/// <summary>
/// Ekip atamaları. Sürükle-bırak ekranda yalnızca taslağı değiştirir; kalıcı kayıt burada, tek seferde (atomik) yapılır.
/// Eşzamanlı planlama: önce işin, sonra ilgili personelin satırları kilitlenir (aynı kişiyi aynı anda iki işe atayan
/// iki planlamacıdan ikincisi birincinin bitmesini bekler, sonra çakışmayı görür).
/// </summary>
public class AssignmentService(AppDbContext db, AccessService access, AvailabilityService availability, WorkflowService workflow,
    AuditService audit, NotificationService notifications)
{
    /// <param name="desired">İşin ekibinin olması gereken son hali (taslak).</param>
    /// <param name="send">true ise kayıttan sonra iş personele gönderilir.</param>
    /// <param name="expectedVersion">Sayfa açıldığında işin UpdatedAtUtc değeri (Ticks). Arada değiştiyse kayıt reddedilir.</param>
    /// <param name="groupRoleIds">Bu işin ekip alanları (ekrandaki sırayla). Boş bırakılırsa mevcut alanlar korunur.
    /// Atanan herkesin alanı listede yoksa kendiliğinden eklenir.</param>
    public async Task<OpResult> SaveAsync(UserContext u, int workId, IReadOnlyList<AssignmentInput> desired, bool send, long? expectedVersion,
        IReadOnlyList<int>? groupRoleIds = null)
    {
        var dup = desired.GroupBy(d => d.EmployeeId).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new DomainException("Aynı personel bir işe iki kez eklenemez.");

        return await workflow.RunAsync(async () =>
        {
            // 1) Kilitler: iş satırı, sonra personel satırları (sıralı, kilitlenme önlenir).
            await db.LockRowsAsync("WorkOrders", [workId]);
            var w = await access.LoadVisibleAsync(u, workId);
            if (!access.CanAssignWork(u, w)) throw new ForbiddenException("Bu işin ekibini düzenleme yetkiniz yok.");
            if (!WorkflowRules.CanEditAssignments(w.Status))
                throw new DomainException($"İş '{TrNames.Of(w.Status)}' durumunda; ekip değiştirilemez.");
            if (expectedVersion is long v && v != w.UpdatedAtUtc.Ticks)
                throw new DomainException("Bu iş siz düzenlerken başka biri tarafından değiştirildi. Sayfayı yenileyip tekrar deneyin.");

            var current = w.Assignments.Where(a => a.ReleasedAtUtc == null).ToDictionary(a => a.EmployeeId);
            var lockIds = desired.Select(d => d.EmployeeId).Concat(current.Keys).Distinct().OrderBy(i => i).ToList();
            await db.LockRowsAsync("Employees", lockIds);

            // 2) Doğrulama
            var roles = await db.JobRoles.ToDictionaryAsync(r => r.Id);
            var requestedGroups = (groupRoleIds ?? []).Distinct().ToList();
            if (desired.Any(d => !roles.ContainsKey(d.JobRoleId)) || requestedGroups.Any(g => !roles.ContainsKey(g)))
                throw new DomainException("Seçilen ekip alanlarından biri geçersiz.");

            var newIds = desired.Where(d => !current.ContainsKey(d.EmployeeId)).Select(d => d.EmployeeId).ToList();
            var plannable = await access.ScopedEmployees(u).Where(e => newIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
            if (plannable.Count != newIds.Count) throw new DomainException("Seçilen personelden biri bulunamadı veya kapsamınız dışında.");

            // Unvan kısıtı: yeni atanan veya alanı değişen herkes için. Kısıtlamasız alanlar (ör. Şoför) herkese açık.
            var titleProblems = new List<string>();
            foreach (var d in desired)
            {
                var isNew = !current.TryGetValue(d.EmployeeId, out var existing);
                if (!isNew && existing!.JobRoleId == d.JobRoleId) continue;
                var role = roles[d.JobRoleId];
                if (!role.IsActive) throw new DomainException($"'{role.Name}' alanı pasif; yeni atama yapılamaz.");
                var emp = isNew ? plannable[d.EmployeeId] : existing!.Employee;
                if (!AssignmentRules.CanTakeRole(emp.JobRoleId, role.Id, role.IsOpenToAll))
                {
                    var title = emp.JobRoleId is int t && roles.TryGetValue(t, out var tr) ? tr.Name : "unvanı yok";
                    titleProblems.Add($"{emp.FullName} ({title}) '{role.Name}' alanına atanamaz");
                }
            }
            if (titleProblems.Count > 0)
                throw new DomainException("Personel yalnız kendi unvanının alanına veya 'herkes atanabilir' alanlara atanabilir. "
                                          + string.Join(" · ", titleProblems) + ".");

            var eval = await availability.EvaluateAsync(newIds, w.PlannedStartAtUtc, w.PlannedEndAtUtc, w.Id);
            var problems = newIds.Where(id => eval[id].State != PoolState.Available)
                .Select(id => $"{plannable[id].FullName}: {eval[id].Reason}").ToList();
            if (problems.Count > 0)
                throw new DomainException("Uygun olmayan personel atanamaz. " + string.Join(" · ", problems));

            // 3) Fark uygulanır: çıkarılanlar serbest bırakılır (satır silinmez), rolü değişenler güncellenir, yeniler eklenir.
            var now = DateTime.UtcNow;
            var sent = !WorkflowRules.IsBeforeSend(w.Status);
            var desiredMap = desired.ToDictionary(d => d.EmployeeId);
            var removed = new List<WorkAssignment>();
            foreach (var (empId, a) in current)
            {
                if (desiredMap.TryGetValue(empId, out var d))
                {
                    if (a.JobRoleId != d.JobRoleId)
                    {
                        audit.Add(u, "AssignmentRoleChanged", "WorkAssignment", a.Id,
                            $"{a.Employee.FullName}: görev rolü {roles[d.JobRoleId].Name} olarak değişti.", w.Id);
                        a.JobRoleId = d.JobRoleId;
                    }
                    a.TaskNote = Clean(d.TaskNote, 500);
                    continue;
                }
                if (a.AssignmentStatus == AssignmentStatus.Working)
                    throw new DomainException($"{a.Employee.FullName} bu işte çalışmaya başladı; ekipten çıkarılamaz.");
                a.AssignmentStatus = AssignmentStatus.Released;
                a.ReleasedAtUtc = now;
                a.ReleaseReason = "Planlamacı ekipten çıkardı";
                removed.Add(a);
                audit.Add(u, "AssignmentRemoved", "WorkAssignment", a.Id, $"{a.Employee.FullName} ekipten çıkarıldı.", w.Id);
            }

            var added = new List<WorkAssignment>();
            foreach (var d in desired.Where(d => !current.ContainsKey(d.EmployeeId)))
            {
                var emp = plannable[d.EmployeeId];
                var a = new WorkAssignment
                {
                    WorkOrderId = w.Id, EmployeeId = emp.Id, Employee = emp, JobRoleId = d.JobRoleId, TaskNote = Clean(d.TaskNote, 500),
                    AssignedByUserId = u.UserId, AssignedAtUtc = now
                };
                w.Assignments.Add(a);
                added.Add(a);
                audit.Add(u, "AssignmentAdded", "WorkOrder", w.Id, $"{emp.FullName} ekibe eklendi ({roles[d.JobRoleId].Name}).", w.Id);
            }

            await SaveGroupsAsync(u, w, groupRoleIds is null ? null : requestedGroups, desired, roles);
            WorkflowService.Touch(w);
            await db.SaveChangesAsync();

            if (!sent)
            {
                if (!send) return OpResult.Done("Taslak kaydedildi. Personel henüz bilgilendirilmedi.");
                await SendInternalAsync(u, w);
                return OpResult.Done("Atamalar kaydedildi ve iş personele gönderildi.");
            }

            // Gönderilmiş işe sonradan eklenen kişiye hemen bildirim gider; hesabı olmayan kişi adına kabul sayılır.
            foreach (var a in added) await AnnounceAsync(u, w, a);
            foreach (var a in removed.Where(r => r.Employee.ApplicationUserId is not null))
                await notifications.AddAsync([a.Employee.ApplicationUserId], w, NotificationType.AssignmentRemoved,
                    "Ekipten çıkarıldınız", w.Title, $"as:{a.Id}:removed", u.UserId);
            if (w.Status == WorkStatus.AwaitingAssignment && w.Assignments.Any(a => a.ReleasedAtUtc == null))
                workflow.Transition(w, WorkStatus.AwaitingAcceptance, u, "Yeniden atama yapıldı.");
            workflow.Recompute(w, u);
            return OpResult.Done("Atamalar kaydedildi; değişiklikten etkilenen personel bilgilendirildi.");
        });
    }

    /// <summary>
    /// Tamamlanmış veya iptal edilmiş işi yeniden açar (planlamacı/yönetici, gerekçe zorunlu). İstenirse plan tarihleri
    /// değişir ve önceki ekip aynı görev alanlarıyla yeniden atanır; o aralıkta uygun olmayanlar atlanır ve bildirilir.
    /// Geri atanan olursa iş Kabul Bekliyor'a, olmazsa Atama Bekliyor'a geçer. Eski atama satırları geçmişte kalır.
    /// </summary>
    public Task<OpResult> ReopenAsync(UserContext u, int workId, string? reason, DateTime? newStartLocal, DateTime? newEndLocal, bool restoreTeam)
    {
        var text = WorkflowService.RequireReason(reason, "Yeniden açma gerekçesi");
        return workflow.RunAsync(async () =>
        {
            await db.LockRowsAsync("WorkOrders", [workId]);
            var w = await access.LoadVisibleAsync(u, workId);
            if (!access.CanReopenWork(u, w)) throw new ForbiddenException("Bu işi yeniden açma yetkiniz yok.");
            if (w.Status is not (WorkStatus.Completed or WorkStatus.Cancelled))
                throw new DomainException("Yalnız tamamlanmış veya iptal edilmiş iş yeniden açılabilir.");

            if (newStartLocal is DateTime ns && newEndLocal is DateTime ne)
            {
                var s = TrTime.ToUtc(ns);
                var e = TrTime.ToUtc(ne);
                if (e <= s) throw new DomainException("Planlanan bitiş, başlangıçtan sonra olmalı.");
                w.PlannedStartAtUtc = s;
                w.PlannedEndAtUtc = e;
            }

            // Önceki ekip: tamamlanınca "Tamamladı" ya da iptalde serbest bırakılan son atamalar (kişi başına bir).
            var wasCompleted = w.Status == WorkStatus.Completed;
            var previous = w.Assignments
                .Where(a => wasCompleted ? a.AssignmentStatus == AssignmentStatus.Finished : a.ReleaseReason == "İş iptal edildi")
                .GroupBy(a => a.EmployeeId).Select(g => g.OrderByDescending(a => a.ReleasedAtUtc).First())
                .ToList();

            var restored = new List<WorkAssignment>();
            var skipped = new List<string>();
            if (restoreTeam && previous.Count > 0)
            {
                var ids = previous.Select(a => a.EmployeeId).ToList();
                await db.LockRowsAsync("Employees", ids);
                var eval = await availability.EvaluateAsync(ids, w.PlannedStartAtUtc, w.PlannedEndAtUtc, w.Id);
                var now = DateTime.UtcNow;
                foreach (var old in previous)
                {
                    var ev = eval[old.EmployeeId];
                    if (ev.State != PoolState.Available) { skipped.Add($"{old.Employee.FullName} ({ev.Reason ?? "uygun değil"})"); continue; }
                    var a = new WorkAssignment
                    {
                        WorkOrderId = w.Id, EmployeeId = old.EmployeeId, Employee = old.Employee, JobRoleId = old.JobRoleId,
                        TaskNote = old.TaskNote, AssignedByUserId = u.UserId, AssignedAtUtc = now
                    };
                    w.Assignments.Add(a);
                    restored.Add(a);
                }
            }

            workflow.Transition(w, restored.Count > 0 ? WorkStatus.AwaitingAcceptance : WorkStatus.AwaitingAssignment, u, "Yeniden açıldı: " + text);
            w.CompletedAtUtc = null;
            w.CancelledAtUtc = null;
            w.CompletionSubmittedAtUtc = null;
            WorkflowService.Touch(w);
            audit.Add(u, "WorkReopened", "WorkOrder", w.Id,
                $"İş yeniden açıldı ({TrTime.Range(w.PlannedStartAtUtc, w.PlannedEndAtUtc)}). Geri atanan: {restored.Count}"
                + (skipped.Count > 0 ? $" · atanamayan: {string.Join(", ", skipped)}" : ""), w.Id);
            await db.SaveChangesAsync();

            foreach (var a in restored) await AnnounceAsync(u, w, a);
            var supervisor = await workflow.SupervisorUserIdAsync(w);
            await notifications.AddAsync([supervisor, w.ManagerUserId, w.CreatedByUserId], w, NotificationType.WorkChanged, "İş yeniden açıldı",
                text, $"wo:{w.Id}:reopened:{w.UpdatedAtUtc.Ticks}", u.UserId);
            workflow.Recompute(w, u);

            var msg = restored.Count > 0
                ? $"İş yeniden açıldı; {restored.Count} kişi tekrar atandı, yanıtları bekleniyor."
                : "İş yeniden açıldı; ekip ataması bekliyor. Havuzdan personel atayın.";
            if (skipped.Count > 0) msg += " Atanamayan (başka işte veya uygun değil): " + string.Join(", ", skipped) + ".";
            return OpResult.Done(msg);
        });
    }

    /// <summary>İşin ekip alanlarını günceller. Atanmış kişinin alanı hiçbir zaman listeden düşmez.</summary>
    private async Task SaveGroupsAsync(UserContext u, WorkOrder w, List<int>? requested, IReadOnlyList<AssignmentInput> desired,
        Dictionary<int, JobRole> roles)
    {
        var existing = await db.WorkTeamGroups.Where(g => g.WorkOrderId == w.Id).OrderBy(g => g.SortOrder).ToListAsync();
        var order = (requested ?? existing.Select(g => g.JobRoleId).ToList()).ToList();
        foreach (var roleId in desired.Select(d => d.JobRoleId).Distinct().Where(r => !order.Contains(r)))
            order.Add(roleId);

        var before = existing.Select(g => g.JobRoleId).ToList();
        foreach (var g in existing.Where(g => !order.Contains(g.JobRoleId))) db.WorkTeamGroups.Remove(g);
        for (var i = 0; i < order.Count; i++)
        {
            var g = existing.FirstOrDefault(x => x.JobRoleId == order[i]);
            if (g is null)
            {
                if (!roles[order[i]].IsActive) throw new DomainException($"'{roles[order[i]].Name}' alanı pasif; işe eklenemez.");
                db.WorkTeamGroups.Add(new WorkTeamGroup { WorkOrderId = w.Id, JobRoleId = order[i], SortOrder = i });
            }
            else g.SortOrder = i;
        }
        if (!before.SequenceEqual(order))
            audit.Add(u, "TeamGroupsChanged", "WorkOrder", w.Id,
                "Ekip alanları: " + (order.Count == 0 ? "—" : string.Join(", ", order.Select(r => roles[r].Name))), w.Id);
    }

    /// <summary>Taslak işi personele gönderir: Taslak/Atama Bekliyor → Kabul Bekliyor ve bildirimler.</summary>
    private async Task SendInternalAsync(UserContext u, WorkOrder w)
    {
        var active = w.Assignments.Where(a => a.ReleasedAtUtc == null).ToList();
        if (active.Count == 0) throw new DomainException("Göndermeden önce en az bir personel atayın.");
        if (w.ResponsibleSupervisorEmployeeId is null)
            throw new DomainException("Göndermeden önce işin sorumlu şefini seçin (tamamlanma onayını o verecek).");

        workflow.Transition(w, WorkStatus.AwaitingAcceptance, u, "İş personele gönderildi.");
        foreach (var a in active.Where(a => a.AssignmentStatus == AssignmentStatus.Assigned))
            await AnnounceAsync(u, w, a);
        workflow.Recompute(w, u);
    }

    private async Task AnnounceAsync(UserContext u, WorkOrder w, WorkAssignment a)
    {
        if (a.Employee.ApplicationUserId is null)
        {
            a.AssignmentStatus = AssignmentStatus.Accepted;
            a.RespondedAtUtc = DateTime.UtcNow;
            audit.Add(u, "AssignmentAutoAccepted", "WorkAssignment", a.Id,
                $"{a.Employee.FullName} için uygulama hesabı yok; planlamacı adına kabul edildi sayıldı.", w.Id);
            return;
        }
        await notifications.AddAsync([a.Employee.ApplicationUserId], w, NotificationType.Assigned, "Yeni iş atandı",
            $"{w.Title} · {TrTime.Range(w.PlannedStartAtUtc, w.PlannedEndAtUtc)}", $"as:{a.Id}:assigned", u.UserId);
    }

    private static string? Clean(string? s, int max)
    {
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > max ? t[..max] : t;
    }
}
