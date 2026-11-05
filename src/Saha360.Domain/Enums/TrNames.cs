namespace Saha360.Domain.Enums;

/// <summary>Enum değerlerinin Türkçe görünen adları. Arayüz ve raporlar yalnızca bunu kullanır.</summary>
public static class TrNames
{
    public static string Of(WorkStatus s) => s switch
    {
        WorkStatus.Draft => "Taslak",
        WorkStatus.AwaitingAssignment => "Atama Bekliyor",
        WorkStatus.AwaitingAcceptance => "Kabul Bekliyor",
        WorkStatus.Planned => "Planlandı",
        WorkStatus.InProgress => "Devam Ediyor",
        WorkStatus.CompletionApprovalPending => "Tamamlanma Onayı Bekliyor",
        WorkStatus.ReturnedBySupervisor => "Şef Tarafından İade Edildi",
        WorkStatus.Completed => "Tamamlandı",
        WorkStatus.Cancelled => "İptal Edildi",
        _ => s.ToString()
    };

    public static string Of(WorkPriority p) => p switch
    {
        WorkPriority.Low => "Düşük",
        WorkPriority.Normal => "Normal",
        WorkPriority.High => "Yüksek",
        WorkPriority.Urgent => "Acil",
        _ => p.ToString()
    };

    public static string Of(AssignmentStatus s) => s switch
    {
        AssignmentStatus.Assigned => "Yanıt bekleniyor",
        AssignmentStatus.Accepted => "Kabul etti",
        AssignmentStatus.Rejected => "Reddetti",
        AssignmentStatus.Working => "Çalışıyor",
        AssignmentStatus.Finished => "Tamamladı",
        AssignmentStatus.Released => "Atamadan çıkarıldı",
        _ => s.ToString()
    };

    public static string Of(AttachmentType t) => t switch
    {
        AttachmentType.Photo => "Fotoğraf",
        AttachmentType.Document => "Belge",
        AttachmentType.BeforeWork => "İş öncesi",
        AttachmentType.AfterWork => "İş sonrası",
        _ => t.ToString()
    };

    public static string Of(AvailabilityType t) => t switch
    {
        AvailabilityType.Leave => "İzinli",
        AvailabilityType.Sick => "Raporlu",
        AvailabilityType.Unavailable => "Müsait değil",
        _ => t.ToString()
    };

    /// <summary>Bootstrap rozet sınıfı. Renk tek başına anlam taşımaz; rozetin içinde her zaman metin vardır.</summary>
    public static string Badge(WorkStatus s) => s switch
    {
        WorkStatus.Draft => "text-bg-secondary",
        WorkStatus.AwaitingAssignment => "text-bg-warning",
        WorkStatus.AwaitingAcceptance => "text-bg-info",
        WorkStatus.Planned => "text-bg-primary",
        WorkStatus.InProgress => "text-bg-primary",
        WorkStatus.CompletionApprovalPending => "text-bg-warning",
        WorkStatus.ReturnedBySupervisor => "text-bg-danger",
        WorkStatus.Completed => "text-bg-success",
        WorkStatus.Cancelled => "text-bg-dark",
        _ => "text-bg-secondary"
    };

    public static string Badge(WorkPriority p) => p switch
    {
        WorkPriority.Low => "text-bg-light border",
        WorkPriority.Normal => "text-bg-secondary",
        WorkPriority.High => "text-bg-warning",
        WorkPriority.Urgent => "text-bg-danger",
        _ => "text-bg-secondary"
    };

    public static string Badge(AssignmentStatus s) => s switch
    {
        AssignmentStatus.Assigned => "text-bg-info",
        AssignmentStatus.Accepted => "text-bg-primary",
        AssignmentStatus.Rejected => "text-bg-danger",
        AssignmentStatus.Working => "text-bg-primary",
        AssignmentStatus.Finished => "text-bg-success",
        AssignmentStatus.Released => "text-bg-secondary",
        _ => "text-bg-secondary"
    };
}
