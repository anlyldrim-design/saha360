using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saha360.Domain.Entities;
using Saha360.Infrastructure.Identity;

namespace Saha360.Infrastructure.Persistence.Configurations;

/// <summary>
/// Tablo, ilişki, indeks ve kısıt tanımları. Geçmişi olan kayıtlarda cascade delete kullanılmaz:
/// tüm ilişkiler Restrict; kayıtlar pasife alınır veya serbest bırakılır.
/// </summary>
internal static class ModelConfiguration
{
    public const int UserIdLength = 450;

    public static void ApplyAppConfigurations(this ModelBuilder b, bool serverRowVersion)
    {
        b.Entity<ApplicationUser>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(150).IsRequired();
            e.HasOne<OrganizationUnit>().WithMany().HasForeignKey(x => x.OrganizationUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        Definition<OrganizationUnit>(b, "OrganizationUnits", e => e.Property(x => x.Code).HasMaxLength(30));
        Definition<JobRole>(b, "JobRoles");
        Definition<WorkCategory>(b, "WorkCategories");
        Definition<WorkLocation>(b, "WorkLocations");

        b.Entity<Team>(e =>
        {
            e.ToTable("Teams");
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => new { x.OrganizationUnitId, x.Name }).IsUnique();
            e.HasOne(x => x.OrganizationUnit).WithMany().HasForeignKey(x => x.OrganizationUnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SupervisorEmployee).WithMany().HasForeignKey(x => x.SupervisorEmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Employee>(e =>
        {
            e.ToTable("Employees");
            e.Property(x => x.EmployeeNumber).HasMaxLength(30);
            e.Property(x => x.FirstName).HasMaxLength(80).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(80).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.ApplicationUserId).HasMaxLength(UserIdLength);
            e.Ignore(x => x.FullName);
            RowVersion(e.Property(x => x.RowVersion), serverRowVersion);
            e.HasIndex(x => x.EmployeeNumber).IsUnique().HasFilter("[EmployeeNumber] IS NOT NULL");
            e.HasIndex(x => x.ApplicationUserId).IsUnique().HasFilter("[ApplicationUserId] IS NOT NULL");
            e.HasIndex(x => new { x.IsActive, x.LastName, x.FirstName });
            e.HasOne(x => x.OrganizationUnit).WithMany().HasForeignKey(x => x.OrganizationUnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.JobRole).WithMany().HasForeignKey(x => x.JobRoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SupervisorEmployee).WithMany().HasForeignKey(x => x.SupervisorEmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithOne().HasForeignKey<Employee>(x => x.ApplicationUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<EmployeeAvailability>(e =>
        {
            e.ToTable("EmployeeAvailabilities");
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CreatedByUserId).HasMaxLength(UserIdLength).IsRequired();
            e.HasIndex(x => new { x.EmployeeId, x.StartAtUtc, x.EndAtUtc });
            e.HasOne(x => x.Employee).WithMany(x => x.Availabilities).HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrder>(e =>
        {
            e.ToTable("WorkOrders");
            e.Property(x => x.WorkNumber).HasMaxLength(30).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Instructions).HasMaxLength(1000);
            e.Property(x => x.LocationText).HasMaxLength(300);
            e.Property(x => x.CompletionNote).HasMaxLength(2000);
            e.Property(x => x.CreatedByUserId).HasMaxLength(UserIdLength).IsRequired();
            e.Property(x => x.ManagerUserId).HasMaxLength(UserIdLength);
            RowVersion(e.Property(x => x.RowVersion), serverRowVersion);
            e.HasIndex(x => x.WorkNumber).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.PlannedStartAtUtc);
            e.HasIndex(x => new { x.Status, x.PlannedEndAtUtc });
            e.HasIndex(x => x.ResponsibleUnitId);
            e.HasIndex(x => x.ResponsibleSupervisorEmployeeId);
            e.HasOne(x => x.WorkCategory).WithMany().HasForeignKey(x => x.WorkCategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.WorkLocation).WithMany().HasForeignKey(x => x.WorkLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ResponsibleUnit).WithMany().HasForeignKey(x => x.ResponsibleUnitId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ResponsibleSupervisorEmployee).WithMany().HasForeignKey(x => x.ResponsibleSupervisorEmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ManagerUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkAssignment>(e =>
        {
            e.ToTable("WorkAssignments");
            e.Property(x => x.TaskNote).HasMaxLength(500);
            e.Property(x => x.RejectionReason).HasMaxLength(1000);
            e.Property(x => x.ReleaseReason).HasMaxLength(500);
            e.Property(x => x.AssignedByUserId).HasMaxLength(UserIdLength).IsRequired();
            e.Ignore(x => x.IsActive);
            RowVersion(e.Property(x => x.RowVersion), serverRowVersion);
            // Bir personel aynı işte aynı anda yalnızca bir kez ekipte olabilir (geçmiş satırlar serbest).
            e.HasIndex(x => new { x.WorkOrderId, x.EmployeeId }).IsUnique().HasFilter("[ReleasedAtUtc] IS NULL");
            e.HasIndex(x => new { x.EmployeeId, x.ReleasedAtUtc });
            e.HasOne(x => x.WorkOrder).WithMany(x => x.Assignments).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.JobRole).WithMany().HasForeignKey(x => x.JobRoleId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkTeamGroup>(e =>
        {
            e.ToTable("WorkTeamGroups");
            e.HasIndex(x => new { x.WorkOrderId, x.JobRoleId }).IsUnique();
            e.HasOne<WorkOrder>().WithMany(x => x.TeamGroups).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.JobRole).WithMany().HasForeignKey(x => x.JobRoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkStatusHistory>(e =>
        {
            e.ToTable("WorkStatusHistories");
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.ChangedByUserId).HasMaxLength(UserIdLength).IsRequired();
            e.HasIndex(x => new { x.WorkOrderId, x.ChangedAtUtc });
            e.HasOne<WorkOrder>().WithMany(x => x.StatusHistory).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkNote>(e =>
        {
            e.ToTable("WorkNotes");
            e.Property(x => x.NoteText).HasMaxLength(4000).IsRequired();
            e.Property(x => x.AuthorUserId).HasMaxLength(UserIdLength).IsRequired();
            e.HasIndex(x => new { x.WorkOrderId, x.CreatedAtUtc });
            e.HasOne<WorkOrder>().WithMany(x => x.Notes).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkAttachment>(e =>
        {
            e.ToTable("WorkAttachments");
            e.Property(x => x.StorageKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Caption).HasMaxLength(500);
            e.Property(x => x.UploadedByUserId).HasMaxLength(UserIdLength).IsRequired();
            e.Property(x => x.DeletedByUserId).HasMaxLength(UserIdLength);
            e.Ignore(x => x.IsImage);
            e.HasIndex(x => x.StorageKey).IsUnique();
            e.HasIndex(x => new { x.WorkOrderId, x.DeletedAtUtc });
            e.HasOne<WorkOrder>().WithMany(x => x.Attachments).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkMaterial>(e =>
        {
            e.ToTable("WorkMaterials");
            e.Property(x => x.MaterialNameSnapshot).HasMaxLength(200).IsRequired();
            e.Property(x => x.UnitSnapshot).HasMaxLength(30).IsRequired();
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.AddedByUserId).HasMaxLength(UserIdLength).IsRequired();
            e.HasIndex(x => new { x.WorkOrderId, x.MaterialCatalogId });
            e.HasOne<WorkOrder>().WithMany(x => x.Materials).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.MaterialCatalog).WithMany().HasForeignKey(x => x.MaterialCatalogId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MaterialCatalog>(e =>
        {
            e.ToTable("MaterialCatalog");
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.Unit).HasMaxLength(30).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => new { x.IsActive, x.Name });
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("Notifications");
            e.Property(x => x.RecipientUserId).HasMaxLength(UserIdLength).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            e.Property(x => x.DeduplicationKey).HasMaxLength(150);
            e.HasIndex(x => new { x.RecipientUserId, x.ReadAtUtc, x.CreatedAtUtc });
            e.HasIndex(x => new { x.RecipientUserId, x.DeduplicationKey }).IsUnique().HasFilter("[DeduplicationKey] IS NOT NULL");
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("RolePermissions");
            e.Property(x => x.RoleName).HasMaxLength(64).IsRequired();
            e.Property(x => x.Permission).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.RoleName, x.Permission }).IsUnique();
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.Property(x => x.ActorUserId).HasMaxLength(UserIdLength);
            e.Property(x => x.Action).HasMaxLength(80).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(80).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(80).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(1000).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasIndex(x => new { x.WorkOrderId, x.OccurredAtUtc });
            e.HasIndex(x => x.OccurredAtUtc);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });
    }

    private static void Definition<T>(ModelBuilder b, string table, Action<EntityTypeBuilder<T>>? extra = null) where T : DefinitionEntity
    {
        b.Entity<T>(e =>
        {
            e.ToTable(table);
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            extra?.Invoke(e);
        });
    }

    private static void RowVersion(PropertyBuilder<byte[]> p, bool server)
    {
        if (server) p.IsRowVersion();
        else p.IsConcurrencyToken();
    }
}
