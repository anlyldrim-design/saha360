using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Şartname 14.2 — iş akışı.</summary>
public class WorkflowTests : IDisposable
{
    private readonly TestHost h = new();
    public void Dispose() => h.Dispose();

    private async Task<int> SentWorkAsync(params UserContext[] team)
    {
        var w = await h.CreateAsync();
        await h.AssignAsync(w, true, team.Select(t => h.A(t)).ToArray());
        return w.Id;
    }

    private async Task<int> PendingApprovalAsync()
    {
        var id = await SentWorkAsync(h.Ali);
        await h.Flow.AcceptAsync(h.Ali, id);
        await h.Flow.StartAsync(h.Ali, id);
        await h.Flow.SubmitCompletionAsync(h.Ali, id, "İş bitti, alan temizlendi.");
        return id;
    }

    [Fact]
    public async Task Draft_creates_no_notifications_and_is_invisible_to_personnel()
    {
        var w = await h.CreateAsync();
        await h.AssignAsync(w, send: false, h.A(h.Ali));
        Assert.Equal(0, h.NotificationsFor(h.Ali));
        Assert.False(await h.Get<AccessService>().VisibleWorks(h.Ali).AnyAsync(x => x.Id == w.Id));
    }

    [Fact]
    public async Task Sending_notifies_assignees_and_auto_accepts_people_without_account()
    {
        var w = await h.CreateAsync();
        await h.AssignAsync(w, true, h.A(h.Ali), h.A(h.NoAccount));
        Assert.Equal(1, h.NotificationsFor(h.Ali));
        var work = h.Reload(w.Id);
        Assert.Equal(WorkStatus.AwaitingAcceptance, work.Status);
        Assert.Equal(AssignmentStatus.Accepted, work.Assignments.Single(a => a.EmployeeId == h.NoAccount.Id).AssignmentStatus);
    }

    [Fact]
    public async Task All_accepted_moves_work_to_planned_and_records_instruction_ack()
    {
        var id = await SentWorkAsync(h.Ali, h.Fatma);
        await h.Flow.AcceptAsync(h.Ali, id);
        Assert.Equal(WorkStatus.AwaitingAcceptance, h.Reload(id).Status);
        await h.Flow.AcceptAsync(h.Fatma, id);
        var w = h.Reload(id);
        Assert.Equal(WorkStatus.Planned, w.Status);
        Assert.All(w.Assignments, a => Assert.NotNull(a.InstructionAcknowledgedAtUtc));
    }

    [Fact]
    public async Task Reject_requires_reason_keeps_history_and_notifies_supervisor()
    {
        var id = await SentWorkAsync(h.Ali, h.Fatma);
        await Assert.ThrowsAsync<DomainException>(() => h.Flow.RejectAsync(h.Ali, id, "  "));

        await h.Flow.RejectAsync(h.Ali, id, "O gün raporluyum.");
        var w = h.Reload(id);
        Assert.Equal(WorkStatus.AwaitingAcceptance, w.Status); // tek kişinin reddi işi reddetmez
        var a = w.Assignments.Single(x => x.EmployeeId == h.Ali.EmployeeId);
        Assert.Equal(AssignmentStatus.Rejected, a.AssignmentStatus);
        Assert.Equal("O gün raporluyum.", a.RejectionReason);
        Assert.True(h.NotificationsFor(h.Supervisor) >= 1);

        await h.Flow.RejectAsync(h.Fatma, id, "Başka işteyim.");
        Assert.Equal(WorkStatus.AwaitingAssignment, h.Reload(id).Status);
        Assert.Equal(2, h.Db.WorkAssignments.Count(x => x.WorkOrderId == id)); // satırlar silinmedi
    }

    [Fact]
    public async Task Completion_goes_to_supervisor_and_approval_notifies_manager()
    {
        var id = await PendingApprovalAsync();
        Assert.Equal(WorkStatus.CompletionApprovalPending, h.Reload(id).Status);
        Assert.True(h.NotificationsFor(h.Supervisor) >= 1);

        var r = await h.Flow.ApproveAsync(h.Supervisor, id, null);
        Assert.True(r.Changed);
        var w = h.Reload(id);
        Assert.Equal(WorkStatus.Completed, w.Status);
        Assert.NotNull(w.CompletedAtUtc);
        Assert.All(w.Assignments, a => Assert.NotNull(a.ReleasedAtUtc)); // ekip havuza döndü
        Assert.Equal(1, h.Db.Notifications.Count(n => n.RecipientUserId == h.ManagerUserId && n.NotificationType == NotificationType.Approved));
    }

    [Fact]
    public async Task Repeated_approval_is_a_no_op_without_duplicate_history_or_notifications()
    {
        var id = await PendingApprovalAsync();
        await h.Flow.ApproveAsync(h.Supervisor, id, null);
        var history = h.Db.WorkStatusHistories.Count(x => x.WorkOrderId == id);
        var notes = h.Db.Notifications.Count();

        var again = await h.Flow.ApproveAsync(h.Supervisor, id, null);
        Assert.False(again.Changed);
        Assert.Equal(history, h.Db.WorkStatusHistories.Count(x => x.WorkOrderId == id));
        Assert.Equal(notes, h.Db.Notifications.Count());
    }

    [Fact]
    public async Task Personnel_and_other_supervisor_cannot_approve()
    {
        var id = await PendingApprovalAsync();
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Flow.ApproveAsync(h.Ali, id, null));
        // Diğer şef işi göremez bile (bulunamadı).
        await Assert.ThrowsAnyAsync<Exception>(() => h.Flow.ApproveAsync(h.OtherSupervisor, id, null));
        Assert.Equal(WorkStatus.CompletionApprovalPending, h.Reload(id).Status);
    }

    [Fact]
    public async Task Return_requires_reason_and_allows_resubmission()
    {
        var id = await PendingApprovalAsync();
        await Assert.ThrowsAsync<DomainException>(() => h.Flow.ReturnAsync(h.Supervisor, id, ""));
        await h.Flow.ReturnAsync(h.Supervisor, id, "Fotoğraf eksik, ekleyin.");
        Assert.Equal(WorkStatus.ReturnedBySupervisor, h.Reload(id).Status);
        Assert.True(h.NotificationsFor(h.Ali) >= 2);

        await h.Flow.SubmitCompletionAsync(h.Ali, id, "Fotoğraflar eklendi.");
        Assert.Equal(WorkStatus.CompletionApprovalPending, h.Reload(id).Status);
    }

    [Fact]
    public async Task Request_info_keeps_work_pending_and_adds_note()
    {
        var id = await PendingApprovalAsync();
        await h.Flow.RequestInfoAsync(h.Supervisor, id, "Kaç metre boru kullanıldı?");
        Assert.Equal(WorkStatus.CompletionApprovalPending, h.Reload(id).Status);
        Assert.Contains(h.Db.WorkNotes.Where(n => n.WorkOrderId == id), n => n.NoteText.Contains("Kaç metre"));
    }

    [Fact]
    public async Task Cannot_start_before_team_accepts_or_skip_states()
    {
        var id = await SentWorkAsync(h.Ali, h.Fatma);
        await Assert.ThrowsAsync<DomainException>(() => h.Flow.StartAsync(h.Ali, id));          // henüz kabul etmedi
        await h.Flow.AcceptAsync(h.Ali, id);
        await Assert.ThrowsAsync<DomainException>(() => h.Flow.StartAsync(h.Ali, id));          // Fatma yanıt vermedi
        await Assert.ThrowsAsync<DomainException>(() => h.Flow.SubmitCompletionAsync(h.Ali, id, "Bitti diyorum.")); // başlamadan tamamlanamaz
    }

    [Fact]
    public async Task Cancel_requires_reason_and_releases_team()
    {
        var id = await SentWorkAsync(h.Ali);
        await Assert.ThrowsAsync<DomainException>(() => h.Flow.CancelAsync(h.Planner, id, null));
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Flow.CancelAsync(h.Ali, id, "Ben iptal ediyorum"));
        await h.Flow.CancelAsync(h.Planner, id, "Hava muhalefeti nedeniyle.");
        var w = h.Reload(id);
        Assert.Equal(WorkStatus.Cancelled, w.Status);
        Assert.All(w.Assignments, a => Assert.NotNull(a.ReleasedAtUtc));
    }

    [Fact]
    public async Task Completed_work_can_be_reopened_with_previous_team()
    {
        var id = await PendingApprovalAsync();
        await h.Flow.ApproveAsync(h.Supervisor, id, null);
        var svc = h.Get<AssignmentService>();
        await Assert.ThrowsAsync<DomainException>(() => svc.ReopenAsync(h.Planner, id, "", null, null, true));          // gerekçe zorunlu
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ReopenAsync(h.Supervisor, id, "Tekrar arıza", null, null, true)); // planlamacı/yönetici

        var start = Domain.Rules.TrTime.NowLocal.AddDays(2);
        var r = await svc.ReopenAsync(h.Planner, id, "Aynı noktada arıza tekrarladı.", start, start.AddHours(4), restoreTeam: true);
        Assert.True(r.Changed);
        var w = h.Reload(id);
        Assert.Equal(WorkStatus.AwaitingAcceptance, w.Status);
        Assert.Null(w.CompletedAtUtc);
        var active = Assert.Single(w.Assignments, a => a.ReleasedAtUtc == null);
        Assert.Equal(h.Ali.EmployeeId, active.EmployeeId);
        Assert.Equal(AssignmentStatus.Assigned, active.AssignmentStatus);
        Assert.Contains(w.Assignments, a => a.AssignmentStatus == AssignmentStatus.Finished);   // eski satır geçmişte kaldı

        // Ali tekrar kabul edip işi baştan yürütebilir.
        await h.Flow.AcceptAsync(h.Ali, id);
        Assert.Equal(WorkStatus.Planned, h.Reload(id).Status);
    }

    [Fact]
    public async Task Reopen_skips_people_busy_elsewhere_and_cancelled_work_can_reopen()
    {
        var id = await SentWorkAsync(h.Ali);
        await h.Flow.CancelAsync(h.Planner, id, "Hava muhalefeti nedeniyle.");
        var other = await h.CreateAsync("Başka iş", 24, 8);         // Ali aynı saatte başka işe atanır
        await h.AssignAsync(other, false, h.A(h.Ali));

        var r = await h.Get<AssignmentService>().ReopenAsync(h.Planner, id, "Hava düzeldi, yeniden planlandı.", null, null, true);
        Assert.Contains("Atanamayan", r.Message);
        var w = h.Reload(id);
        Assert.Equal(WorkStatus.AwaitingAssignment, w.Status);
        Assert.Null(w.CancelledAtUtc);
        Assert.DoesNotContain(w.Assignments, a => a.ReleasedAtUtc == null);
    }

    [Fact]
    public async Task Every_transition_is_recorded_in_history()
    {
        var id = await PendingApprovalAsync();
        await h.Flow.ApproveAsync(h.Supervisor, id, null);
        var path = h.Db.WorkStatusHistories.Where(x => x.WorkOrderId == id).OrderBy(x => x.Id).Select(x => x.ToStatus).ToList();
        Assert.Equal([WorkStatus.Draft, WorkStatus.AwaitingAcceptance, WorkStatus.Planned, WorkStatus.InProgress,
            WorkStatus.CompletionApprovalPending, WorkStatus.Completed], path);
    }
}
