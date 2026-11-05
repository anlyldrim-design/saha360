using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Security;
using Saha360.Infrastructure;
using Saha360.Infrastructure.FileStorage;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Persistence;
using Saha360.Infrastructure.Services;
using Saha360.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.User.RequireUniqueEmail = false;
        o.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-@";
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager<AppSignInManager>()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<TurkishIdentityErrorDescriber>();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/giris";
    o.LogoutPath = "/cikis";
    o.AccessDeniedPath = "/yetkisiz";
    o.Cookie.Name = "Saha360.Auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    // Oturum zaman aşımı: 8 saat hareketsizlikte düşer (vardiya süresi).
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
});
// Rol değişikliği ve pasife alma açık oturumlara en geç 1 dakikada yansır.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

// Sayfa yetkileri izinlerden okunur (Yetkiler ekranındaki tablo). Her izin bir politikadır; bileşik politikalar
// "şunlardan biri yeterli" anlamına gelir. Kayıt düzeyindeki kontroller (birim kapsamı vb.) servislerdedir.
builder.Services.AddAuthorization(o =>
{
    foreach (var p in Permissions.Keys)
        o.AddPolicy(p, b => b.AddRequirements(new PermissionRequirement(p)));
    foreach (var (name, anyOf) in Policies.Composite)
        o.AddPolicy(name, b => b.AddRequirements(new PermissionRequirement(anyOf)));
    o.AddPolicy(Policies.Admin, p => p.RequireRole(SystemRoles.SystemAdmin));
    // Varsayılan: giriş yapılmadan hiçbir sayfa açılmaz.
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionHandler>();

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    o.Filters.Add<AppExceptionFilter>();
    o.ModelBindingMessageProvider.SetValueMustNotBeNullAccessor(_ => "Bu alan zorunludur.");
    o.ModelBindingMessageProvider.SetAttemptedValueIsInvalidAccessor((v, f) => $"\"{v}\" geçerli bir değer değil.");
    o.ModelBindingMessageProvider.SetValueIsInvalidAccessor(v => $"\"{v}\" geçerli bir değer değil.");
    o.ModelBindingMessageProvider.SetMissingBindRequiredValueAccessor(f => $"{f} alanı zorunludur.");
});
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = FileValidator.MaxBytes * 6);
builder.Services.AddHealthChecks().AddCheck<DbHealthCheck>("veritabani");

// Giriş denemelerine IP başına sınır: kaba kuvvet denemelerini yavaşlatır (hesap kilidine ek olarak).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

// Oturum çerezlerini şifreleyen anahtarlar diskte: uygulama yeniden başlasa da oturumlar düşmez.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keysPath)) keysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("Saha360");

var app = builder.Build();

var tr = new CultureInfo("tr-TR");
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(tr),
    SupportedCultures = [tr],
    SupportedUICultures = [tr]
});

if (!app.Environment.IsDevelopment())
{
    // Üretimde ayrıntılı hata/stack trace kullanıcıya gösterilmez.
    app.UseExceptionHandler("/hata");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "same-origin";
    await next();
});

app.UseStatusCodePagesWithReExecute("/durum/{0}");
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/saglik").AllowAnonymous();
app.MapControllers();

// Veritabanı: geliştirmede otomatik migration; üretimde yalnız Database:MigrateOnStartup=true ise (README: kontrollü dağıtım).
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        await db.Database.MigrateAsync();

    var seeder = scope.ServiceProvider.GetRequiredService<DataSeeder>();
    await seeder.EnsureRolesAsync();
    // Yetki tablosu: eksik (rol, izin) çiftleri varsayılanla doldurulur (yeni sürümde eklenen izinler dahil).
    await scope.ServiceProvider.GetRequiredService<PermissionService>().EnsureDefaultsAsync();
    var boot = app.Configuration.GetSection("Bootstrap");
    await seeder.EnsureBootstrapAdminAsync(boot["AdminUserName"], boot["AdminPassword"], boot["AdminFullName"]);
    var seed = app.Configuration.GetSection("Seed");
    if (seed.GetValue<bool>("Demo") && !string.IsNullOrWhiteSpace(seed["Password"]))
        await seeder.SeedDemoAsync(seed["Password"]!);
}

app.Run();

public partial class Program;
