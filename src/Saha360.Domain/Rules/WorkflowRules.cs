using Saha360.Domain.Enums;

namespace Saha360.Domain.Rules;

/// <summary>
/// İş durumlarının tek merkezli geçiş matrisi. Hiçbir ekran veya servis bu tabloda olmayan bir geçiş yapamaz.
/// </summary>
public static class WorkflowRules
{
    private static readonly Dictionary<WorkStatus, WorkStatus[]> Allowed = new()
    {
        [WorkStatus.Draft] = [WorkStatus.AwaitingAcceptance, WorkStatus.AwaitingAssignment, WorkStatus.Cancelled],
        [WorkStatus.AwaitingAssignment] = [WorkStatus.AwaitingAcceptance, WorkStatus.Cancelled],
        [WorkStatus.AwaitingAcceptance] = [WorkStatus.Planned, WorkStatus.AwaitingAssignment, WorkStatus.Cancelled],
        [WorkStatus.Planned] = [WorkStatus.InProgress, WorkStatus.AwaitingAcceptance, WorkStatus.AwaitingAssignment, WorkStatus.Cancelled],
        [WorkStatus.InProgress] = [WorkStatus.CompletionApprovalPending, WorkStatus.Cancelled],
        [WorkStatus.CompletionApprovalPending] = [WorkStatus.Completed, WorkStatus.ReturnedBySupervisor],
        [WorkStatus.ReturnedBySupervisor] = [WorkStatus.InProgress, WorkStatus.CompletionApprovalPending, WorkStatus.Cancelled],
        // Yeniden açma (yalnız planlamacı/yönetici, gerekçeyle): ekip geri atanırsa Kabul Bekliyor, atanamazsa Atama Bekliyor.
        [WorkStatus.Completed] = [WorkStatus.AwaitingAcceptance, WorkStatus.AwaitingAssignment],
        [WorkStatus.Cancelled] = [WorkStatus.AwaitingAcceptance, WorkStatus.AwaitingAssignment]
    };

    public static bool CanTransition(WorkStatus from, WorkStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyCollection<WorkStatus> TargetsOf(WorkStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];

    /// <summary>Tamamlandı veya İptal: iş kapalıdır; değişiklik yapılamaz (yalnız gerekçeyle yeniden açılabilir).</summary>
    public static bool IsFinal(WorkStatus s) => s is WorkStatus.Completed or WorkStatus.Cancelled;

    /// <summary>Personele henüz gönderilmemiş durumlar (personel bu işi görmez, bildirim almaz).</summary>
    public static bool IsBeforeSend(WorkStatus s) => s is WorkStatus.Draft;

    /// <summary>Planlamacının ekip ekleyip çıkarabildiği durumlar.</summary>
    public static bool CanEditAssignments(WorkStatus s) => s is WorkStatus.Draft or WorkStatus.AwaitingAssignment
        or WorkStatus.AwaitingAcceptance or WorkStatus.Planned or WorkStatus.InProgress or WorkStatus.ReturnedBySupervisor;

    /// <summary>Plan tarihleri, başlık, açıklama gibi iş bilgilerinin düzenlenebildiği durumlar.</summary>
    public static bool CanEditDetails(WorkStatus s) => !IsFinal(s) && s != WorkStatus.CompletionApprovalPending;

    /// <summary>Saha çalışmasının sürdüğü durumlar: süresi geçse bile personel otomatik serbest kalmaz.</summary>
    public static bool IsStarted(WorkStatus s) => s is WorkStatus.InProgress or WorkStatus.CompletionApprovalPending
        or WorkStatus.ReturnedBySupervisor;

    /// <summary>Not, fotoğraf ve malzeme eklenebilen durumlar.</summary>
    public static bool AcceptsContent(WorkStatus s) => !IsFinal(s);

    /// <summary>Gecikme: planlanan bitiş geçmiş ve iş tamamlanmamış/iptal edilmemiş.</summary>
    public static bool IsOverdue(WorkStatus s, DateTime plannedEndUtc, DateTime nowUtc) => !IsFinal(s) && plannedEndUtc < nowUtc;

    /// <summary>Atama ekipte sayılıyor mu (kişiyi meşgul eder mi)?</summary>
    public static bool IsActiveAssignment(AssignmentStatus s) => s is AssignmentStatus.Assigned or AssignmentStatus.Accepted
        or AssignmentStatus.Working;
}

public static class AssignmentRules
{
    /// <summary>Personel bu görev alanına atanabilir mi? Kısıtlamasız alan herkese açık; diğerleri yalnız aynı unvana.</summary>
    public static bool CanTakeRole(int? employeeTitleId, int roleId, bool roleIsOpenToAll) =>
        roleIsOpenToAll || employeeTitleId == roleId;
}

public static class TimeRange
{
    /// <summary>Yarı açık aralıklar [başlangıç, bitiş) çakışıyor mu? Biri biterken öteki başlıyorsa çakışma sayılmaz.</summary>
    public static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) => aStart < bEnd && bStart < aEnd;
}
