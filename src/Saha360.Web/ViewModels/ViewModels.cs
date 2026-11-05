using Saha360.Domain.Entities;
using Saha360.Infrastructure.Services;

namespace Saha360.Web.ViewModels;

public class WorkListViewModel
{
    public required WorkListQuery Query { get; init; }
    public required PagedResult<WorkListItem> Result { get; init; }
    public required Lookups Lookups { get; init; }
    public List<Option> Employees { get; init; } = [];
}

public class WorkFormViewModel
{
    public required WorkOrderInput Input { get; init; }
    public WorkOrder? Existing { get; init; }
    public required Lookups Lookups { get; init; }
    public List<MaterialCatalog> Materials { get; init; } = [];
}

public record PoolWorkOption(int Id, string WorkNumber, string Title, Saha360.Domain.Enums.WorkStatus Status, DateTime StartUtc, DateTime EndUtc);

public class PoolViewModel
{
    public required PoolQuery Query { get; init; }
    public List<PoolItem> Pool { get; init; } = [];
    public WorkDetail? Work { get; init; }
    public List<PoolWorkOption> Works { get; init; } = [];
    public required Lookups Lookups { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public List<JobRole> Groups { get; init; } = [];
}

public class ApprovalCard
{
    public required WorkListItem Work { get; init; }
    public string? CompletionNote { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public DateTime? ActualStartAtUtc { get; init; }
    public int PhotoCount { get; init; }
    public int NoteCount { get; init; }
    public int MaterialCount { get; init; }
    public List<string> Team { get; init; } = [];
}

public class EmployeeFormViewModel
{
    public int? Id { get; init; }
    public required EmployeeInput Input { get; init; }
    public required Lookups Lookups { get; init; }
    public List<Option> Supervisors { get; init; } = [];
    public List<EmployeeAvailability> Availabilities { get; init; } = [];
    public string? AccountUserName { get; init; }
    public string? AccountUserId { get; init; }
    public bool AccountIsActive { get; init; }
    public bool CanManageAccounts { get; init; }
    /// <summary>Kaydı düzenleyebilir mi (personel yönetimi izni + kendi birimi). Değilse form salt okunur.</summary>
    public bool CanEdit { get; init; }
    /// <summary>İzin / müsaitlik ekleyip kaldırabilir mi.</summary>
    public bool CanManageLeave { get; init; }
    public AccountRequest Account { get; init; } = new();
    public List<WorkListItem> RecentWorks { get; init; } = [];
}

/// <summary>Personel formundan birlikte açılacak uygulama hesabı.</summary>
public record AccountRequest(bool Create = false, string? UserName = null, string Role = Saha360.Domain.Security.SystemRoles.Personnel);

public class UserFormViewModel
{
    public string? Id { get; init; }
    public required UserInput Input { get; init; }
    public List<Option> Units { get; init; } = [];
    public List<Option> Employees { get; init; } = [];
    public List<Option> JobRoles { get; init; } = [];
}
