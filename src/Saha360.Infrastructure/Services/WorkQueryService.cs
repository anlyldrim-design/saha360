using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Domain.Enums;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.Persistence;

namespace Saha360.Infrastructure.Services;

public record HistoryEntry(DateTime AtUtc, string? ActorName, string Action, string Summary);
public record NoteEntry(int Id, string AuthorName, string Text, DateTime CreatedAtUtc);
public record AttachmentEntry(int Id, string FileName, string ContentType, long Size, string? Caption, AttachmentType Type, string UploadedBy,
    DateTime CreatedAtUtc, bool IsImage, bool CanDelete);
public record MaterialEntry(int Id, string Name, string Unit, decimal Quantity, string AddedBy, DateTime AddedAtUtc, string? Note);
public record AssignmentEntry(int Id, int EmployeeId, string EmployeeName, int JobRoleId, string JobRoleName, bool IsLeadRole,
    AssignmentStatus Status, string? TaskNote, DateTime AssignedAtUtc, DateTime? RespondedAtUtc, DateTime? StartedAtUtc,
    DateTime? ReleasedAtUtc, string? ReleaseReason, string? RejectionReason, bool HasAccount, DateTime? InstructionAckAtUtc,
    int? EmployeeTitleId = null);

/// <summary>İş detay sayfası için tüm veriler ve kullanıcının bu işte yapabilecekleri.</summary>
public class WorkDetail
{
    public required WorkOrder Work { get; init; }
    public string? CategoryName { get; init; }
    public string? LocationName { get; init; }
    public string UnitName { get; init; } = "";
    public string? SupervisorName { get; init; }
    public string? CreatedByName { get; init; }
    public string? ManagerName { get; init; }
    public List<AssignmentEntry> Assignments { get; init; } = [];

    /// <summary>Bu işin kayıtlı ekip alanları (ekrandaki sırayla).</summary>
    public List<int> TeamGroupRoleIds { get; init; } = [];
    public List<MaterialEntry> Materials { get; init; } = [];
    public List<AttachmentEntry> Attachments { get; init; } = [];
    public List<NoteEntry> Notes { get; init; } = [];
    public List<HistoryEntry> History { get; init; } = [];

    public AssignmentEntry? MyAssignment { get; init; }
    public bool CanManage { get; init; }
    public bool CanApprove { get; init; }
    public bool CanContribute { get; init; }
    public bool IsOverdue { get; init; }

    public bool CanEditWork { get; init; }
    public bool CanAssignWork { get; init; }
    public bool CanCancelWork { get; init; }
    public bool CanReopenWork { get; init; }

    public bool CanEdit => CanEditWork && WorkflowRules.CanEditDetails(Work.Status);
    public bool CanPlanTeam => CanAssignWork && WorkflowRules.CanEditAssignments(Work.Status);
    public bool CanCancel => CanCancelWork && WorkflowRules.CanTransition(Work.Status, WorkStatus.Cancelled);
    public bool CanReopen => CanReopenWork && Work.Status is WorkStatus.Completed or WorkStatus.Cancelled;
    public bool CanAccept => MyAssignment?.Status == AssignmentStatus.Assigned && MyAssignment.ReleasedAtUtc is null
                             && !WorkflowRules.IsFinal(Work.Status) && Work.Status != WorkStatus.Draft;
    public bool CanReject => MyAssignment is { ReleasedAtUtc: null, Status: AssignmentStatus.Assigned or AssignmentStatus.Accepted }
                             && Work.Status is WorkStatus.AwaitingAcceptance or WorkStatus.Planned;
    public bool CanStart => MyAssignment is { ReleasedAtUtc: null } m && m.Status != AssignmentStatus.Assigned
                            && (Work.Status is WorkStatus.Planned or WorkStatus.ReturnedBySupervisor
                                || (Work.Status == WorkStatus.InProgress && m.Status == AssignmentStatus.Accepted));
    public bool CanSubmit => MyAssignment is { ReleasedAtUtc: null } m && m.Status != AssignmentStatus.Assigned
                             && Work.Status is WorkStatus.InProgress or WorkStatus.ReturnedBySupervisor;
    public bool AwaitsMyApproval => CanApprove && Work.Status == WorkStatus.CompletionApprovalPending;
}

public record MyWorkCard(int Id, string WorkNumber, string Title, string? Location, string? Instructions, DateTime PlannedStartAtUtc,
    DateTime PlannedEndAtUtc, string? SupervisorName, WorkStatus Status, AssignmentStatus MyStatus, string RoleName, bool IsOverdue,
    DateTime? ReleasedAtUtc, WorkPriority Priority);

public class WorkQueryService(AppDbContext db, AccessService access)
{
    public async Task<WorkDetail> GetDetailAsync(UserContext u, int workId)
    {
        var w = await access.VisibleWorks(u).AsNoTracking()
                    .Include(x => x.WorkCategory).Include(x => x.WorkLocation).Include(x => x.ResponsibleUnit)
                    .Include(x => x.ResponsibleSupervisorEmployee)
                    .Include(x => x.Assignments).ThenInclude(a => a.Employee)
                    .Include(x => x.Assignments).ThenInclude(a => a.JobRole)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(x => x.Id == workId)
                ?? throw new Domain.Exceptions.NotFoundException("İş bulunamadı veya görme yetkiniz yok.");

        var materials = await db.WorkMaterials.AsNoTracking().Where(m => m.WorkOrderId == w.Id).OrderBy(m => m.AddedAtUtc).ToListAsync();
        var attachments = await db.WorkAttachments.AsNoTracking().Where(a => a.WorkOrderId == w.Id && a.DeletedAtUtc == null)
            .OrderByDescending(a => a.CreatedAtUtc).ToListAsync();
        var notes = await db.WorkNotes.AsNoTracking().Where(n => n.WorkOrderId == w.Id).OrderByDescending(n => n.CreatedAtUtc).ToListAsync();
        var history = await db.AuditLogs.AsNoTracking().Where(a => a.WorkOrderId == w.Id).OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id).Take(300).ToListAsync();

        var userIds = materials.Select(m => m.AddedByUserId).Concat(attachments.Select(a => a.UploadedByUserId))
            .Concat(notes.Select(n => n.AuthorUserId)).Concat(history.Select(h => h.ActorUserId ?? ""))
            .Append(w.CreatedByUserId).Append(w.ManagerUserId ?? "").Where(x => x != "").Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName);
        string Name(string? id) => id is not null && names.TryGetValue(id, out var n) ? n : "—";

        var canManage = access.CanManage(u, w);
        var assignments = w.Assignments
            .OrderBy(a => a.ReleasedAtUtc is null ? 0 : 1).ThenByDescending(a => a.JobRole.IsLeadRole).ThenBy(a => a.JobRole.SortOrder)
            .ThenBy(a => a.Employee.FirstName)
            .Select(a => new AssignmentEntry(a.Id, a.EmployeeId, a.Employee.FullName, a.JobRoleId, a.JobRole.Name, a.JobRole.IsLeadRole,
                a.AssignmentStatus, a.TaskNote, a.AssignedAtUtc, a.RespondedAtUtc, a.StartedAtUtc, a.ReleasedAtUtc, a.ReleaseReason,
                a.RejectionReason, a.Employee.ApplicationUserId is not null, a.InstructionAcknowledgedAtUtc, a.Employee.JobRoleId))
            .ToList();
        var teamGroups = await db.WorkTeamGroups.AsNoTracking().Where(g => g.WorkOrderId == w.Id).OrderBy(g => g.SortOrder)
            .Select(g => g.JobRoleId).ToListAsync();
        var mine = u.EmployeeId is int me
            ? assignments.FirstOrDefault(a => a.EmployeeId == me && a.ReleasedAtUtc is null) ?? assignments.FirstOrDefault(a => a.EmployeeId == me)
            : null;

        return new WorkDetail
        {
            Work = w,
            CategoryName = w.WorkCategory?.Name,
            LocationName = w.WorkLocation?.Name,
            UnitName = w.ResponsibleUnit.Name,
            SupervisorName = w.ResponsibleSupervisorEmployee?.FullName,
            CreatedByName = Name(w.CreatedByUserId),
            ManagerName = w.ManagerUserId is null ? null : Name(w.ManagerUserId),
            Assignments = assignments,
            Materials = materials.Select(m => new MaterialEntry(m.Id, m.MaterialNameSnapshot, m.UnitSnapshot, m.Quantity, Name(m.AddedByUserId),
                m.AddedAtUtc, m.Note)).ToList(),
            Attachments = attachments.Select(a => new AttachmentEntry(a.Id, a.OriginalFileName, a.ContentType, a.FileSizeBytes, a.Caption,
                a.AttachmentType, Name(a.UploadedByUserId), a.CreatedAtUtc, a.IsImage,
                canManage || (a.UploadedByUserId == u.UserId && WorkflowRules.AcceptsContent(w.Status)))).ToList(),
            Notes = notes.Select(n => new NoteEntry(n.Id, Name(n.AuthorUserId), n.NoteText, n.CreatedAtUtc)).ToList(),
            History = history.Select(h => new HistoryEntry(h.OccurredAtUtc, h.ActorUserId is null ? "Sistem" : Name(h.ActorUserId), h.Action, h.Summary)).ToList(),
            MyAssignment = mine,
            TeamGroupRoleIds = teamGroups,
            CanManage = canManage,
            CanEditWork = access.CanEditWork(u, w),
            CanAssignWork = access.CanAssignWork(u, w),
            CanCancelWork = access.CanCancelWork(u, w),
            CanReopenWork = access.CanReopenWork(u, w),
            CanApprove = access.CanApprove(u, w),
            CanContribute = access.CanContribute(u, w),
            IsOverdue = WorkflowRules.IsOverdue(w.Status, w.PlannedEndAtUtc, DateTime.UtcNow)
        };
    }

    /// <summary>Personelin "İşlerim" ekranı: taslaklar görünmez.</summary>
    public async Task<List<MyWorkCard>> MyWorksAsync(UserContext u)
    {
        if (u.EmployeeId is not int me) return [];
        var now = DateTime.UtcNow;
        var since = now.AddDays(-60);
        var rows = await db.WorkAssignments.AsNoTracking()
            .Where(a => a.EmployeeId == me && a.WorkOrder.Status != WorkStatus.Draft
                        && (a.ReleasedAtUtc == null || a.ReleasedAtUtc > since))
            .OrderBy(a => a.WorkOrder.PlannedStartAtUtc)
            .Select(a => new
            {
                a.WorkOrder.Id, a.WorkOrder.WorkNumber, a.WorkOrder.Title,
                Location = a.WorkOrder.WorkLocation != null
                    ? a.WorkOrder.WorkLocation.Name + (a.WorkOrder.LocationText != null ? " · " + a.WorkOrder.LocationText : "")
                    : a.WorkOrder.LocationText,
                a.WorkOrder.Instructions, a.WorkOrder.PlannedStartAtUtc, a.WorkOrder.PlannedEndAtUtc,
                Supervisor = a.WorkOrder.ResponsibleSupervisorEmployee != null
                    ? a.WorkOrder.ResponsibleSupervisorEmployee.FirstName + " " + a.WorkOrder.ResponsibleSupervisorEmployee.LastName : null,
                a.WorkOrder.Status, a.AssignmentStatus, Role = a.JobRole.Name, a.ReleasedAtUtc, a.WorkOrder.Priority
            })
            .ToListAsync();
        // Aynı işte birden fazla geçmiş atama olabilir; en güncelini göster.
        return rows.GroupBy(r => r.Id).Select(g => g.OrderBy(r => r.ReleasedAtUtc is null ? 0 : 1).ThenByDescending(r => r.ReleasedAtUtc).First())
            .Select(r => new MyWorkCard(r.Id, r.WorkNumber, r.Title, r.Location, r.Instructions, r.PlannedStartAtUtc, r.PlannedEndAtUtc,
                r.Supervisor, r.Status, r.AssignmentStatus, r.Role, WorkflowRules.IsOverdue(r.Status, r.PlannedEndAtUtc, now), r.ReleasedAtUtc,
                r.Priority))
            .ToList();
    }
}
