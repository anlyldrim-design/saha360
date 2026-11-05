using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Saha360.Infrastructure.FileStorage;
using Saha360.Infrastructure.Persistence;
using Saha360.Infrastructure.Services;

namespace Saha360.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        var cs = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("ConnectionStrings:Default ayarı yok. README'deki 'Veritabanı bağlantısı' bölümüne bakın.");
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(cs, sql => sql.CommandTimeout(60)));

        // Dosyalar varsayılan olarak web kökünün dışındaki App_Data/uploads klasöründe durur.
        var root = config["Storage:RootPath"];
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(contentRoot, "App_Data", "uploads");
        services.AddSingleton<IFileStorage>(new LocalFileStorage(root));

        services.AddAppServices();
        return services;
    }

    /// <summary>Uygulama servisleri (testler de aynı kaydı kullanır).</summary>
    public static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<PermissionService>();
        services.AddScoped<AccessService>();
        services.AddScoped<AuditService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<AvailabilityService>();
        services.AddScoped<WorkflowService>();
        services.AddScoped<AssignmentService>();
        services.AddScoped<WorkContentService>();
        services.AddScoped<WorkOrderService>();
        services.AddScoped<WorkQueryService>();
        services.AddScoped<DefinitionService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<MaterialService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ReportService>();
        services.AddScoped<ImportService>();
        services.AddScoped<DataSeeder>();
        return services;
    }
}
