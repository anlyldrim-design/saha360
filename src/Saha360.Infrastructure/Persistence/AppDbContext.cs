using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Entities;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Persistence.Configurations;

namespace Saha360.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<OrganizationUnit> OrganizationUnits => Set<OrganizationUnit>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<JobRole> JobRoles => Set<JobRole>();
    public DbSet<WorkCategory> WorkCategories => Set<WorkCategory>();
    public DbSet<WorkLocation> WorkLocations => Set<WorkLocation>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeAvailability> EmployeeAvailabilities => Set<EmployeeAvailability>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkAssignment> WorkAssignments => Set<WorkAssignment>();
    public DbSet<WorkTeamGroup> WorkTeamGroups => Set<WorkTeamGroup>();
    public DbSet<WorkStatusHistory> WorkStatusHistories => Set<WorkStatusHistory>();
    public DbSet<WorkNote> WorkNotes => Set<WorkNote>();
    public DbSet<WorkAttachment> WorkAttachments => Set<WorkAttachment>();
    public DbSet<WorkMaterial> WorkMaterials => Set<WorkMaterial>();
    public DbSet<MaterialCatalog> MaterialCatalog => Set<MaterialCatalog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    /// <summary>SQL Server'da rowversion sütunu veritabanınca üretilir; testlerde (SQLite) uygulama üretir.</summary>
    private bool UsesServerRowVersion => Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer";

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyAppConfigurations(UsesServerRowVersion);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Açık transaction bitene kadar verilen satırları kilitler (SQL Server: UPDLOCK + HOLDLOCK). Satırı değiştirmez,
    /// sürüm damgasını da oynatmaz. Aynı personeli aynı anda iki işe atamaya çalışan ikinci istek, birincinin bitmesini bekler.
    /// SQLite'ta (testler) yazma zaten tek seferde yapıldığı için gerekmez.
    /// </summary>
    internal async Task LockRowsAsync(string table, IReadOnlyCollection<int> ids)
    {
        if (!UsesServerRowVersion || ids.Count == 0) return;
        if (table is not ("WorkOrders" or "Employees")) throw new ArgumentException("Kilitlenemeyen tablo.", nameof(table));
        var placeholders = string.Join(",", ids.Select((_, i) => "{" + i + "}"));
        await Database.ExecuteSqlRawAsync(
            $"SELECT [Id] FROM [{table}] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [Id] IN ({placeholders})",
            ids.OrderBy(i => i).Cast<object>().ToArray());
    }

    private void StampRowVersions()
    {
        if (UsesServerRowVersion) return;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var prop = entry.Metadata.FindProperty("RowVersion");
            if (prop is null) continue;
            entry.Property("RowVersion").CurrentValue = Guid.NewGuid().ToByteArray();
        }
    }
}
