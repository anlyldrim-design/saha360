namespace Saha360.Domain.Enums;

/// <summary>İşin genel durumu. Geçişler yalnızca <see cref="Rules.WorkflowRules"/> üzerinden yapılır.</summary>
public enum WorkStatus
{
    Draft = 0,
    AwaitingAssignment = 1,
    // 2 bilinçli olarak boş: şartnamedeki "Atandı / Gönderildi" adımı "Kabul Bekliyor" ile birleştirildi (README).
    AwaitingAcceptance = 3,
    Planned = 4,
    InProgress = 5,
    CompletionApprovalPending = 6,
    ReturnedBySupervisor = 7,
    Completed = 8,
    Cancelled = 9
}

public enum WorkPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3
}

/// <summary>Bir personelin bir işteki ataması. Kabul/ret iş bazında değil atama bazında tutulur.</summary>
public enum AssignmentStatus
{
    Assigned = 0,
    Accepted = 1,
    Rejected = 2,
    Working = 3,
    Finished = 4,
    Released = 5
}

public enum AttachmentType
{
    Photo = 0,
    Document = 1,
    BeforeWork = 2,
    AfterWork = 3
}

public enum AvailabilityType
{
    Leave = 0,
    Sick = 1,
    Unavailable = 2
}

public enum NotificationType
{
    Assigned = 0,
    WorkChanged = 1,
    AssignmentRejected = 2,
    WorkStarted = 3,
    CompletionSubmitted = 4,
    Approved = 5,
    Returned = 6,
    InfoRequested = 7,
    Cancelled = 8,
    AssignmentAccepted = 9,
    AssignmentRemoved = 10
}
