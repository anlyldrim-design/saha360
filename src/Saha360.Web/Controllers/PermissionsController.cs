using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Saha360.Infrastructure.Services;
using Saha360.Web.Infrastructure;

namespace Saha360.Web.Controllers;

/// <summary>
/// Yetkiler: hangi rol neyi yapabilir. Yalnız sistem yöneticisi rolü açar (izinle devredilemez; kimse kendine yetki veremesin).
/// </summary>
[Authorize(Policy = Policies.Admin)]
public class PermissionsController(CurrentUser current, PermissionService permissions) : Controller
{
    [HttpGet("/yetkiler")]
    public async Task<IActionResult> Index() => View(await permissions.MatrixAsync());

    [HttpPost("/yetkiler")]
    public async Task<IActionResult> Save(string[]? granted)
    {
        await permissions.SaveAsync(await current.GetAsync(), granted ?? []);
        TempData["Ok"] = "Yetkiler kaydedildi. Değişiklik kullanıcıların bir sonraki sayfa açılışında geçerli olur.";
        return Redirect("/yetkiler");
    }

    /// <summary>Varsayılan tabloya dön (ilk kurulumdaki önerilen yetkiler).</summary>
    [HttpPost("/yetkiler/varsayilan")]
    public async Task<IActionResult> Reset()
    {
        var pairs = Saha360.Domain.Security.Permissions.All
            .SelectMany(p => p.DefaultRoles.Where(PermissionService.EditableRoles.Contains).Select(r => r + "|" + p.Key)).ToArray();
        await permissions.SaveAsync(await current.GetAsync(), pairs);
        TempData["Ok"] = "Yetkiler varsayılana döndürüldü.";
        return Redirect("/yetkiler");
    }
}
