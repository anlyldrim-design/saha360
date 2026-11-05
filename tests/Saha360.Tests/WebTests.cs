using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saha360.Domain.Entities;
using Saha360.Domain.Security;
using Saha360.Infrastructure.FileStorage;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Persistence;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Uygulamanın gerçek HTTP hattı üzerinden yetki kontrolleri (menü gizlemek yetmez; adres elle yazılsa da engellenir).</summary>
public class WebTests : IClassFixture<WebTests.AppFactory>
{
    private readonly AppFactory f;
    public WebTests(AppFactory factory) => f = factory;

    public class AppFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _conn = new("DataSource=:memory:");
        public Dictionary<string, string> UserIds { get; } = [];
        public int WorkId { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _conn.Open();
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                                               || d.ServiceType == typeof(IDbContextOptionsConfiguration<AppDbContext>)).ToList())
                    s.Remove(d);
                s.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn));
                s.AddSingleton<IFileStorage, MemoryFileStorage>();
                s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
                s.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = "Test");
            });
        }

        public async Task InitAsync()
        {
            if (UserIds.Count > 0) return;
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            await UserAdminService.EnsureRolesAsync(scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>());
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var unit = new OrganizationUnit { Name = "Birim" };
            db.Add(unit);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PermissionService>().EnsureDefaultsAsync();
            foreach (var (name, role) in new[] { ("admin", SystemRoles.SystemAdmin), ("planlama", SystemRoles.Planner), ("personel", SystemRoles.Personnel) })
            {
                var u = new ApplicationUser { UserName = name, FullName = name, OrganizationUnitId = role == SystemRoles.Planner ? unit.Id : null };
                await users.CreateAsync(u, "Parola1234");
                await users.AddToRoleAsync(u, role);
                UserIds[name] = u.Id;
                if (role == SystemRoles.Personnel)
                    db.Employees.Add(new Employee { FirstName = "Saha", LastName = "Personeli", OrganizationUnitId = unit.Id, ApplicationUserId = u.Id });
            }
            await db.SaveChangesAsync();
            var planner = new UserContext(UserIds["planlama"], "planlama", new HashSet<string> { SystemRoles.Planner }, null, unit.Id);
            var w = await scope.ServiceProvider.GetRequiredService<WorkOrderService>().CreateAsync(planner, new WorkOrderInput
            {
                Title = "Gizli taslak", ResponsibleUnitId = unit.Id,
                PlannedStartLocal = DateTime.Now.AddDays(1), PlannedEndLocal = DateTime.Now.AddDays(1).AddHours(2)
            });
            WorkId = w.Id;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _conn.Dispose();
        }
    }

    /// <summary>"X-Test-User: kullanıcıId|rol" başlığıyla oturum açmış sayar; başlık yoksa anonimdir.</summary>
    public class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var h = Request.Headers["X-Test-User"].ToString();
            if (string.IsNullOrEmpty(h)) return Task.FromResult(AuthenticateResult.NoResult());
            var parts = h.Split('|');
            var id = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, parts[0]), new Claim(ClaimTypes.Role, parts[1])], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), "Test")));
        }
    }

    private async Task<HttpClient> ClientAsync(string? user = null, string? role = null)
    {
        await f.InitAsync();
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (user is not null) c.DefaultRequestHeaders.Add("X-Test-User", $"{f.UserIds[user]}|{role}");
        return c;
    }

    [Fact]
    public async Task Anonymous_user_is_sent_to_login()
    {
        var c = await ClientAsync();
        var r = await c.GetAsync("/isler");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/giris", r.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/giris")).StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_public()
    {
        var c = await ClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/saglik")).StatusCode);
    }

    [Theory]
    [InlineData("/kullanicilar")]
    [InlineData("/sistem")]
    [InlineData("/raporlar")]
    [InlineData("/havuz")]
    [InlineData("/isler/yeni")]
    [InlineData("/personel")]
    [InlineData("/organizasyon")]
    [InlineData("/yetkiler")]
    public async Task Personnel_cannot_open_management_pages_by_url(string url)
    {
        var c = await ClientAsync("personel", SystemRoles.Personnel);
        var r = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/yetkisiz", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Personnel_cannot_post_approval()
    {
        var c = await ClientAsync("personel", SystemRoles.Personnel);
        var r = await c.PostAsync($"/isler/{f.WorkId}/onayla", new FormUrlEncodedContent([]));
        Assert.False(r.IsSuccessStatusCode);
        Assert.False(r.StatusCode == HttpStatusCode.Redirect && !r.Headers.Location!.ToString().Contains("/yetkisiz"), "İstek kabul edilmemeli");
    }

    /// <summary>Planlamacı personeli görür ama ekleyemez / izin giremez; Yetkiler ekranı yalnız sistem yöneticisinde.</summary>
    [Theory]
    [InlineData("/personel/yeni")]
    [InlineData("/yetkiler")]
    [InlineData("/kullanicilar")]
    public async Task Planner_cannot_open_personnel_admin_pages(string url)
    {
        var c = await ClientAsync("planlama", SystemRoles.Planner);
        var r = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/yetkisiz", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Personnel_cannot_see_draft_work_detail()
    {
        var c = await ClientAsync("personel", SystemRoles.Personnel);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/isler/{f.WorkId}")).StatusCode);
    }

    [Fact]
    public async Task Planner_and_admin_pages_render()
    {
        var p = await ClientAsync("planlama", SystemRoles.Planner);
        foreach (var url in new[] { "/", "/isler", $"/isler/{f.WorkId}", $"/havuz?isId={f.WorkId}", "/isler/yeni", "/personel", "/organizasyon", "/raporlar", "/malzemeler", "/tanimlar", "/tanimlar/ekipler", "/bildirimler" })
        {
            var r = await p.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} → {(int)r.StatusCode}");
        }
        var a = await ClientAsync("admin", SystemRoles.SystemAdmin);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/kullanicilar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/sistem")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/yetkiler")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/organizasyon")).StatusCode);
    }

    /// <summary>
    /// Aktif anahtarı: tarayıcı işaretli kutuyu ve gizli "false" alanını sayfadaki sırayla gönderir, sunucu ilk değeri okur.
    /// Kutu gizli alandan önce gelmezse kayıt hep "pasif" olur (gerçekte yaşanan hata).
    /// </summary>
    [Theory]
    [InlineData("admin", SystemRoles.SystemAdmin, "/personel/yeni")]
    [InlineData("admin", SystemRoles.SystemAdmin, "/kullanicilar/yeni")]
    public async Task Active_switch_comes_before_hidden_false_field(string user, string role, string url)
    {
        var c = await ClientAsync(user, role);
        var html = await c.GetStringAsync(url);
        var checkbox = html.IndexOf("type=\"checkbox\" role=\"switch\" id=\"IsActive\"", StringComparison.Ordinal);
        var hidden = html.IndexOf("type=\"hidden\" name=\"IsActive\" value=\"false\"", StringComparison.Ordinal);
        Assert.True(checkbox > 0 && hidden > 0, "Aktif alanları bulunamadı");
        Assert.True(checkbox < hidden, "İşaret kutusu gizli false alanından önce gelmeli");
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_rejected()
    {
        var c = await ClientAsync("planlama", SystemRoles.Planner);
        var r = await c.PostAsync($"/isler/{f.WorkId}/iptal", new FormUrlEncodedContent([new("reason", "Sahte istek ile iptal")]));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
