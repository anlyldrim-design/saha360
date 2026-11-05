using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saha360.Domain.Entities;
using Saha360.Domain.Rules;
using Saha360.Domain.Security;
using Saha360.Infrastructure;
using Saha360.Infrastructure.FileStorage;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Persistence;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>
/// Her test için bellekte SQLite veritabanı, gerçek servisler ve küçük bir organizasyon:
/// planlamacı, iki şef, iki hesaplı personel, bir hesapsız personel ve kapsam dışı bir personel.
/// </summary>
public sealed class TestHost : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ServiceProvider _sp;
    private readonly IServiceScope _scope;

    public AppDbContext Db { get; }
    public MemoryFileStorage Files { get; }
    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public OrganizationUnit Unit { get; private set; } = null!;
    public JobRole LeadRole { get; private set; } = null!;
    public JobRole WorkerRole { get; private set; } = null!;
    public JobRole DriverRole { get; private set; } = null!;
    public JobRole EngineerRole { get; private set; } = null!;
    public MaterialCatalog Pipe { get; private set; } = null!;

    public UserContext Planner { get; private set; } = null!;
    /// <summary>Test Birimi'nin birim yöneticisi (personel, izin, ekip yönetimi onda).</summary>
    public UserContext Manager { get; private set; } = null!;
    public UserContext Supervisor { get; private set; } = null!;
    public UserContext OtherSupervisor { get; private set; } = null!;
    public UserContext Ali { get; private set; } = null!;
    public UserContext Fatma { get; private set; } = null!;
    public UserContext Outsider { get; private set; } = null!;
    public Employee NoAccount { get; private set; } = null!;
    public string ManagerUserId { get; private set; } = "";

    public TestHost()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn));
        services.AddIdentityCore<ApplicationUser>(o => { o.Password.RequireNonAlphanumeric = false; o.Password.RequireUppercase = false; })
            .AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
        services.AddSingleton<IFileStorage, MemoryFileStorage>();
        services.AddAppServices();
        _sp = services.BuildServiceProvider();
        _scope = _sp.CreateScope();
        Db = Get<AppDbContext>();
        Db.Database.EnsureCreated();
        Files = (MemoryFileStorage)Get<IFileStorage>();
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        await UserAdminService.EnsureRolesAsync(Get<RoleManager<IdentityRole>>());
        Unit = new OrganizationUnit { Name = "Test Birimi" };
        var other = new OrganizationUnit { Name = "Diğer Birim" };
        LeadRole = new JobRole { Name = "Şef", IsLeadRole = true, SortOrder = 1 };
        WorkerRole = new JobRole { Name = "İşçi", SortOrder = 2 };
        DriverRole = new JobRole { Name = "Şoför", SortOrder = 3, IsOpenToAll = true };
        EngineerRole = new JobRole { Name = "Mühendis", SortOrder = 4 };
        Pipe = new MaterialCatalog { Code = "BRU", Name = "Boru", Unit = "metre" };
        Db.AddRange(Unit, other, LeadRole, WorkerRole, DriverRole, EngineerRole, Pipe);
        await Db.SaveChangesAsync();

        Planner = await UserAsync("planlama", SystemRoles.Planner, null, Unit.Id);
        Manager = await UserAsync("mudur", SystemRoles.UnitManager, null, Unit.Id);
        ManagerUserId = Manager.UserId;
        Supervisor = await UserAsync("sef", SystemRoles.Supervisor, Emp("Şef", "Bir", Unit));
        OtherSupervisor = await UserAsync("sef2", SystemRoles.Supervisor, Emp("Şef", "İki", Unit));
        Ali = await UserAsync("ali", SystemRoles.Personnel, Emp("Ali", "Demir", Unit));
        Fatma = await UserAsync("fatma", SystemRoles.Personnel, Emp("Fatma", "Çelik", Unit));
        Outsider = await UserAsync("yabanci", SystemRoles.Personnel, Emp("Yalçın", "Yabancı", other));
        NoAccount = Emp("Hesapsız", "Personel", Unit);
        await Db.SaveChangesAsync();
    }

    private Employee Emp(string first, string last, OrganizationUnit unit)
    {
        var e = new Employee { FirstName = first, LastName = last, OrganizationUnit = unit, JobRole = WorkerRole };
        Db.Employees.Add(e);
        return e;
    }

    public async Task<UserContext> UserAsync(string userName, string role, Employee? emp, int? unitId = null)
    {
        var users = Get<UserManager<ApplicationUser>>();
        var u = new ApplicationUser { UserName = userName, FullName = userName.ToUpperInvariant(), OrganizationUnitId = unitId };
        var r = await users.CreateAsync(u, "Parola1234");
        if (!r.Succeeded) throw new InvalidOperationException(string.Join(" ", r.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(u, role);
        if (emp is not null) emp.ApplicationUserId = u.Id;
        await Db.SaveChangesAsync();
        return new UserContext(u.Id, u.FullName, new HashSet<string> { role }, emp?.Id, unitId);
    }

    public WorkOrderInput Input(string title, int startHoursFromNow = 24, int durationHours = 8) => new()
    {
        Title = title,
        ResponsibleUnitId = Unit.Id,
        ResponsibleSupervisorEmployeeId = Supervisor.EmployeeId,
        ManagerUserId = ManagerUserId,
        Instructions = "Güvenlik bandı çekilecek.",
        PlannedStartLocal = TrTime.NowLocal.AddHours(startHoursFromNow),
        PlannedEndLocal = TrTime.NowLocal.AddHours(startHoursFromNow + durationHours)
    };

    public Task<WorkOrder> CreateAsync(string title = "Test işi", int startHoursFromNow = 24, int durationHours = 8) =>
        Get<WorkOrderService>().CreateAsync(Planner, Input(title, startHoursFromNow, durationHours));

    public AssignmentInput A(UserContext u, JobRole? role = null) => new(u.EmployeeId!.Value, (role ?? WorkerRole).Id, null);
    public AssignmentInput A(Employee e, JobRole? role = null) => new(e.Id, (role ?? WorkerRole).Id, null);

    public Task<OpResult> AssignAsync(WorkOrder w, bool send, params AssignmentInput[] team) =>
        Get<AssignmentService>().SaveAsync(Planner, w.Id, team, send, null);

    public WorkflowService Flow => Get<WorkflowService>();

    public WorkOrder Reload(int id) { Db.ChangeTracker.Clear(); return Db.WorkOrders.Include(w => w.Assignments).AsNoTracking().Single(w => w.Id == id); }

    public int NotificationsFor(UserContext u) { Db.ChangeTracker.Clear(); return Db.Notifications.Count(n => n.RecipientUserId == u.UserId); }

    public void Dispose()
    {
        _scope.Dispose();
        _sp.Dispose();
        _conn.Dispose();
    }
}

public class MemoryFileStorage : IFileStorage
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Files[storageKey] = ms.ToArray();
    }

    public Stream? OpenRead(string storageKey) => Files.TryGetValue(storageKey, out var b) ? new MemoryStream(b) : null;
    public void Delete(string storageKey) => Files.Remove(storageKey);
}
