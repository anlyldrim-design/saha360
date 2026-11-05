using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Identity;
using Saha360.Infrastructure.Persistence;
using Saha360.Infrastructure.Services;

namespace Saha360.Web.Infrastructure;

/// <summary>Sayfa politikaları. Tek izinli politikaların adı iznin kendisidir (Permissions.X); burada bileşikler var.</summary>
public static class Policies
{
    /// <summary>Yalnız sistem yöneticisi rolü (Yetkiler ekranı): izinle devredilemez, yetki yükseltmeyi önler.</summary>
    public const string Admin = "Admin";
    /// <summary>Personel listesi, havuz, ekipler, organizasyon, izinler: bunlardan biriyle görülebilir.</summary>
    public const string OrgView = "OrgView";
    /// <summary>Tanımlar ekranını görme (değişiklik ayrıca "tanım yönetimi" ister).</summary>
    public const string DefinitionsView = "DefinitionsView";
    /// <summary>Malzeme kataloğunu görme (iş açan/planlayan da görür).</summary>
    public const string MaterialsView = "MaterialsView";
    /// <summary>Toplu yükleme ekranı: yükleyebileceği en az bir liste olmalı.</summary>
    public const string ImportAny = "ImportAny";

    public static readonly (string Name, string[] AnyOf)[] Composite =
    [
        (OrgView, [Permissions.PersonnelView, Permissions.PersonnelManage, Permissions.TeamsManage, Permissions.LeaveManage, Permissions.WorksAssign]),
        (DefinitionsView, [Permissions.DefinitionsManage, Permissions.WorksCreate, Permissions.WorksEdit, Permissions.PersonnelView, Permissions.PersonnelManage]),
        (MaterialsView, [Permissions.MaterialsManage, Permissions.WorksCreate, Permissions.WorksEdit, Permissions.WorksAssign, Permissions.ReportsView]),
        (ImportAny, [Permissions.DefinitionsManage, Permissions.PersonnelManage, Permissions.TeamsManage, Permissions.MaterialsManage, Permissions.UsersManage]),
    ];
}

/// <summary>"Şu izinlerden biri gerekir" şartı.</summary>
public class PermissionRequirement(params string[] anyOf) : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement
{
    public string[] AnyOf { get; } = anyOf;
}

/// <summary>Şartı, oturumdaki kullanıcının (veritabanındaki yetki tablosundan gelen) izinleriyle karşılaştırır.</summary>
public class PermissionHandler(CurrentUser current) : Microsoft.AspNetCore.Authorization.AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;
        try
        {
            var u = await current.GetAsync();
            if (requirement.AnyOf.Any(u.Has)) context.Succeed(requirement);
        }
        catch (ForbiddenException) { /* pasif hesap: şart sağlanmaz */ }
    }
}

/// <summary>
/// İsteği yapan kullanıcının bağlamı (istek başına bir kez): roller çerezden, izinler yetki tablosundan,
/// birim kapsamı kullanıcı hesabından ya da bağlı personel kaydından.
/// </summary>
public class CurrentUser(IHttpContextAccessor http, AppDbContext db, PermissionService permissions)
{
    private UserContext? _ctx;

    public async Task<UserContext> GetAsync()
    {
        if (_ctx is not null) return _ctx;
        var principal = http.HttpContext?.User;
        var id = principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new ForbiddenException("Oturum bulunamadı.");
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new { u.FullName, u.OrganizationUnitId, u.IsActive, u.AllUnits }).FirstOrDefaultAsync();
        if (user is null || !user.IsActive) throw new ForbiddenException("Hesabınız pasif.");
        var employee = await db.Employees.AsNoTracking().Where(e => e.ApplicationUserId == id)
            .Select(e => new { e.Id, e.OrganizationUnitId }).FirstOrDefaultAsync();
        var roles = principal!.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet();
        var granted = await permissions.ForRolesAsync(roles);
        _ctx = new UserContext(id, user.FullName, roles, employee?.Id, user.OrganizationUnitId ?? employee?.OrganizationUnitId,
            http.HttpContext?.Connection.RemoteIpAddress?.ToString(), granted, user.AllUnits);
        return _ctx;
    }
}

/// <summary>Pasif kullanıcı giriş yapamaz.</summary>
public class AppSignInManager(UserManager<ApplicationUser> userManager, IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory, IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger, IAuthenticationSchemeProvider schemes, IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user) => user.IsActive && await base.CanSignInAsync(user);
}

/// <summary>
/// İş kuralı hataları kullanıcıya anlaşılır mesajla döner: form gönderiminde geldiği sayfaya geri yönlendirilir,
/// AJAX isteğinde JSON döner. Yetki ve bulunamadı hataları 403/404 sayfasına gider.
/// </summary>
public class AppExceptionFilter(ITempDataDictionaryFactory tempData) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var http = context.HttpContext;
        var isAjax = http.Request.Headers.Accept.Any(a => a?.Contains("application/json") == true)
                     || http.Request.Headers["X-Requested-With"] == "XMLHttpRequest";
        switch (context.Exception)
        {
            case DomainException e:
                if (isAjax) context.Result = new BadRequestObjectResult(new { error = e.Message });
                else
                {
                    tempData.GetTempData(http)["Error"] = e.Message;
                    context.Result = new RedirectResult(SafeReferer(http));
                }
                context.ExceptionHandled = true;
                break;
            case ForbiddenException e:
                context.Result = isAjax ? new ObjectResult(new { error = e.Message }) { StatusCode = 403 } : Status(403, e.Message);
                context.ExceptionHandled = true;
                break;
            case NotFoundException e:
                context.Result = isAjax ? new NotFoundObjectResult(new { error = e.Message }) : Status(404, e.Message);
                context.ExceptionHandled = true;
                break;
        }
    }

    private static ViewResult Status(int code, string message) => new()
    {
        ViewName = "~/Views/Shared/Status.cshtml",
        StatusCode = code,
        ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(),
            new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary()) { ["Code"] = code, ["Message"] = message }
    };

    /// <summary>Yalnız aynı sitenin adresine geri döner (açık yönlendirme yapılmaz).</summary>
    public static string SafeReferer(HttpContext http)
    {
        var referer = http.Request.Headers.Referer.ToString();
        if (Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == http.Request.Host.Host && uri.Port == (http.Request.Host.Port ?? uri.Port))
            return uri.PathAndQuery;
        return "/";
    }
}

/// <summary>action adresi elle yazılmış POST formlarına da CSRF jetonu ekler.</summary>
[HtmlTargetElement("form", Attributes = "method")]
public class AntiforgeryFormTagHelper(IHtmlGenerator generator) : TagHelper
{
    public override int Order => 1000;

    [ViewContext, HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var method = output.Attributes["method"]?.Value?.ToString();
        if (!string.Equals(method, "post", StringComparison.OrdinalIgnoreCase)) return;
        if (context.AllAttributes.ContainsName("asp-antiforgery") || !context.AllAttributes.ContainsName("action")) return;
        var token = generator.GenerateAntiforgery(ViewContext);
        if (token is not null) output.PostContent.AppendHtml(token);
    }
}

public class DbHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Veritabanına bağlanılamıyor.");
}

public class TurkishIdentityErrorDescriber : IdentityErrorDescriber
{
    private static IdentityError E(string code, string msg) => new() { Code = code, Description = msg };

    public override IdentityError DuplicateEmail(string email) => E(nameof(DuplicateEmail), $"{email} adresiyle zaten bir hesap var.");
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), $"\"{userName}\" kullanıcı adı alınmış.");
    public override IdentityError InvalidEmail(string? email) => E(nameof(InvalidEmail), "E-posta adresi geçersiz.");
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), "Kullanıcı adı yalnızca İngilizce harf, rakam, nokta, tire, @ ve alt çizgi içerebilir.");
    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), $"Parola en az {length} karakter olmalı.");
    public override IdentityError PasswordRequiresDigit() => E(nameof(PasswordRequiresDigit), "Parola en az bir rakam içermeli.");
    public override IdentityError PasswordRequiresLower() => E(nameof(PasswordRequiresLower), "Parola en az bir küçük harf içermeli.");
    public override IdentityError PasswordRequiresUpper() => E(nameof(PasswordRequiresUpper), "Parola en az bir büyük harf içermeli.");
    public override IdentityError PasswordRequiresNonAlphanumeric() => E(nameof(PasswordRequiresNonAlphanumeric), "Parola en az bir sembol içermeli.");
    public override IdentityError PasswordMismatch() => E(nameof(PasswordMismatch), "Mevcut parola yanlış.");
    public override IdentityError DefaultError() => E(nameof(DefaultError), "Beklenmeyen bir hata oluştu.");
}
