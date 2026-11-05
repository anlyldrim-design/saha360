using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Services;
using Saha360.Web.Infrastructure;

namespace Saha360.Web.Controllers;

public class ImportPageViewModel
{
    /// <summary>Boşsa tüm listeler (tek Excel); doluysa yalnız bu liste (ör. Kullanıcılar).</summary>
    public ImportService.Sheet? Sheet { get; init; }
    public string? Slug { get; init; }
    public string? BackUrl { get; init; }
    public string? BackLabel { get; init; }
    public List<ImportService.Sheet> Allowed { get; init; } = [];
    public ImportResult? Result { get; init; }
    public string? FileName { get; init; }
}

/// <summary>
/// Excel ile toplu yükleme. /toplu-yukleme: tüm listeler tek dosyada (bir birimi baştan kurmak için).
/// /toplu-yukleme/{liste}: yalnız o listenin şablonu ve yüklemesi (Kullanıcılar, Personel, Malzemeler, Tanımlar sekmeleri…).
/// Kim neyi yükleyebilir, sunucuda liste bazında denetlenir.
/// </summary>
[Authorize(Policy = Policies.ImportAny)]
public class ImportController(CurrentUser current, ImportService import, ILogger<ImportController> logger) : Controller
{
    private static readonly Dictionary<string, (string Url, string Label)> Back = new()
    {
        ["birimler"] = ("/tanimlar?tab=birimler", "Birimler"),
        ["unvanlar"] = ("/tanimlar?tab=roller", "Görev rolleri / unvanlar"),
        ["kategoriler"] = ("/tanimlar?tab=kategoriler", "İş kategorileri"),
        ["bolgeler"] = ("/tanimlar?tab=bolgeler", "Bölgeler"),
        ["malzemeler"] = ("/malzemeler", "Malzeme kataloğu"),
        ["ekipler"] = ("/tanimlar/ekipler", "Ekipler ve şefler"),
        ["personel"] = ("/personel", "Personel listesi"),
        ["kullanicilar"] = ("/kullanicilar", "Kullanıcılar"),
    };

    [HttpGet("/toplu-yukleme")]
    public async Task<IActionResult> Index() => View(await VmAsync(null, null, null));

    [HttpGet("/toplu-yukleme/{liste}")]
    public async Task<IActionResult> Single(string liste) => View("Index", await VmAsync(liste, null, null));

    /// <summary>Boş şablon ya da mevcut kayıtlarla dolu şablon. liste verilirse yalnız o listenin sayfası.</summary>
    [HttpGet("/toplu-yukleme/sablon")]
    public async Task<IActionResult> Template(bool mevcut = false, string? liste = null)
    {
        var u = await current.GetAsync();
        var sheet = SheetOf(liste);
        var bytes = await import.BuildTemplateAsync(u, mevcut, sheet?.Name);
        var part = liste is null ? "toplu-yukleme" : liste;
        var name = mevcut ? $"Saha360-{part}-kayitlar-{TrTime.NowLocal:yyyyMMdd-HHmm}.xlsx" : $"Saha360-{part}-sablonu.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    [HttpPost("/toplu-yukleme"), RequestSizeLimit(ImportService.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, string? liste)
    {
        var u = await current.GetAsync();
        var sheet = SheetOf(liste);
        var here = liste is null ? "/toplu-yukleme" : $"/toplu-yukleme/{liste}";
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Yüklenecek Excel dosyasını seçin.";
            return Redirect(here);
        }
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Yalnız .xlsx (Excel) dosyası yüklenebilir. Şablonu indirip doldurun.";
            return Redirect(here);
        }
        ImportResult result;
        try
        {
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;
            result = await import.ImportAsync(u, ms, file.Length, sheet?.Name);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Toplu yükleme veritabanı hatası");
            throw new DomainException("Kayıtlar kaydedilemedi (yinelenen veya geçersiz değer). Hiçbir değişiklik yapılmadı; dosyayı kontrol edip tekrar deneyin.");
        }
        return View("Index", await VmAsync(liste, result, Path.GetFileName(file.FileName)));
    }

    private static ImportService.Sheet? SheetOf(string? slug)
    {
        if (slug is null) return null;
        if (!ImportService.Slugs.TryGetValue(slug, out var name)) throw new NotFoundException("Böyle bir toplu yükleme listesi yok.");
        return ImportService.Sheets.First(s => s.Name == name);
    }

    private async Task<ImportPageViewModel> VmAsync(string? slug, ImportResult? result, string? fileName)
    {
        var u = await current.GetAsync();
        var sheet = SheetOf(slug);
        var allowed = ImportService.AllowedSheets(u).ToList();
        if (sheet is not null && !ImportService.CanImport(u, sheet.Name)) throw new ForbiddenException("Bu listeyi toplu yükleme yetkiniz yok.");
        if (allowed.Count == 0) throw new ForbiddenException("Toplu yükleme yetkiniz yok.");
        return new ImportPageViewModel
        {
            Sheet = sheet, Slug = slug, Allowed = allowed, Result = result, FileName = fileName,
            BackUrl = slug is null ? null : Back[slug].Url, BackLabel = slug is null ? null : Back[slug].Label
        };
    }
}
