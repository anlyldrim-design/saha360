using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Enums;
using Saha360.Infrastructure.Persistence;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Services;
using Saha360.Web.Infrastructure;

namespace Saha360.Web.Controllers;

public class HomeController(CurrentUser current, DashboardService dashboard, WorkQueryService queries) : Controller
{
    [HttpGet("/")]
    public async Task<IActionResult> Index()
    {
        var u = await current.GetAsync();
        // Hiçbir yönetim izni olmayan saha personelinin ana ekranı "İşlerim"dir.
        if (u.Perms.Count == 0 && !u.IsSupervisor) return Redirect("/islerim");
        ViewData["MyWorks"] = u.EmployeeId is null ? null : await queries.MyWorksAsync(u);
        return View(await dashboard.GetAsync(u));
    }

    // Hata sayfaları her yöntemle açılır: başarısız bir POST da yeniden yürütüldüğünde POST olarak gelir.
    [AllowAnonymous, Route("/durum/{code:int}"), IgnoreAntiforgeryToken]
    public IActionResult StatusPage(int code)
    {
        ViewData["Code"] = code;
        ViewData["Message"] = code switch
        {
            404 => "Aradığınız sayfa veya kayıt bulunamadı.",
            403 => "Bu sayfayı görme yetkiniz yok.",
            429 => "Çok fazla deneme yaptınız. Lütfen biraz bekleyin.",
            _ => "Bir sorun oluştu."
        };
        return View("Status");
    }

    [AllowAnonymous, Route("/hata"), IgnoreAntiforgeryToken]
    public IActionResult Error()
    {
        ViewData["Code"] = 500;
        ViewData["Message"] = "Beklenmeyen bir hata oluştu. Sorun sürerse sistem yöneticisine bildirin.";
        Response.StatusCode = 500;
        return View("Status");
    }
}

public class NotificationsController(CurrentUser current, NotificationService notifications) : Controller
{
    [HttpGet("/bildirimler")]
    public async Task<IActionResult> Index(bool okunmamis = false, int page = 1)
    {
        var u = await current.GetAsync();
        ViewData["OnlyUnread"] = okunmamis;
        return View(await notifications.ListAsync(u.UserId, okunmamis, page));
    }

    /// <summary>Bildirimi okundu işaretler ve iş detayına gider. İş detayı yine yetki kontrolünden geçer.</summary>
    [HttpPost("/bildirimler/{id:long}/ac")]
    public async Task<IActionResult> Open(long id)
    {
        var u = await current.GetAsync();
        var workId = await notifications.MarkReadAsync(u.UserId, id);
        return Redirect(workId is int w ? $"/isler/{w}" : "/bildirimler");
    }

    [HttpPost("/bildirimler/tumu-okundu")]
    public async Task<IActionResult> MarkAll()
    {
        var u = await current.GetAsync();
        var n = await notifications.MarkAllReadAsync(u.UserId);
        TempData["Ok"] = n == 0 ? "Okunmamış bildirim yok." : $"{n} bildirim okundu olarak işaretlendi.";
        return Redirect("/bildirimler");
    }
}

[Authorize(Policy = Permissions.AuditView)]
public class SystemController(AppDbContext db, IConfiguration config, IWebHostEnvironment env) : Controller
{
    [HttpGet("/sistem")]
    public async Task<IActionResult> Index(string? q, int page = 1)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(a => a.Summary.Contains(q) || a.Action.Contains(q));
        page = Math.Max(1, page);
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).Skip((page - 1) * 50).Take(50).ToListAsync();
        var ids = items.Select(i => i.ActorUserId).Where(x => x != null).Distinct().ToList();
        ViewData["Names"] = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.FullName);
        ViewData["Info"] = new Dictionary<string, string>
        {
            ["Ortam"] = env.EnvironmentName,
            [".NET"] = Environment.Version.ToString(),
            ["Veritabanı"] = db.Database.GetDbConnection().Database,
            ["Dosya deposu"] = string.IsNullOrWhiteSpace(config["Storage:RootPath"]) ? "App_Data/uploads (varsayılan)" : config["Storage:RootPath"]!,
            ["Personel"] = (await db.Employees.CountAsync()).ToString(),
            ["İş"] = (await db.WorkOrders.CountAsync()).ToString(),
            ["Açık iş"] = (await db.WorkOrders.CountAsync(w => w.Status != WorkStatus.Completed && w.Status != WorkStatus.Cancelled)).ToString()
        };
        return View(new PagedResult<Saha360.Domain.Entities.AuditLog> { Items = items, Page = page, PageSize = 50, Total = total });
    }
}
