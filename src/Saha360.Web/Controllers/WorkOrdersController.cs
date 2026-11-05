using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Domain.Security;
using Saha360.Infrastructure.FileStorage;
using Saha360.Infrastructure.Services;
using Saha360.Web.Infrastructure;
using Saha360.Web.ViewModels;

namespace Saha360.Web.Controllers;

/// <summary>İş listesi, oluşturma/düzenleme, detay ve iş akışı eylemleri. Tüm kurallar servislerde; burası yalnız yönlendirir.</summary>
public class WorkOrdersController(CurrentUser current, WorkOrderService works, WorkQueryService queries, WorkflowService workflow,
    WorkContentService content, DefinitionService definitions, EmployeeService employees, MaterialService materials, AccessService access,
    AssignmentService assignments) : Controller
{
    [HttpGet("/isler")]
    public async Task<IActionResult> Index([FromQuery] WorkListQuery query)
    {
        var u = await current.GetAsync();
        var vm = new WorkListViewModel
        {
            Query = query,
            Result = await works.ListAsync(u, query),
            Lookups = await definitions.LookupsAsync(u),
            Employees = u.CanSeeOrganization ? await employees.AllActiveAsync(u) : []
        };
        return View(vm);
    }

    /// <summary>Listedeki süzgeçle aynı kapsamda CSV (yetki dışı iş dosyaya giremez).</summary>
    [HttpGet("/isler/disa-aktar"), Authorize(Policy = Permissions.ReportsExport)]
    public async Task<IActionResult> Export([FromQuery] WorkListQuery query)
    {
        var u = await current.GetAsync();
        query.Page = 1;
        query.PageSize = 100;
        var all = new List<WorkListItem>();
        while (true)
        {
            var page = await works.ListAsync(u, query);
            all.AddRange(page.Items);
            if (query.Page >= page.TotalPages || all.Count >= 10_000) break;
            query.Page++;
        }
        var table = new ReportTable
        {
            Definition = new ReportDefinition("isler", "İş listesi", "", "Planlanan zaman aralığı"),
            Columns = ["İş no", "Başlık", "Kategori", "Konum", "Planlanan başlangıç", "Planlanan bitiş", "Atanan personel", "Sorumlu şef",
                "Öncelik", "Durum", "Gecikme", "Son güncelleme"],
            Rows = all.Select(w => new[] { w.WorkNumber, w.Title, w.Category ?? "", w.Location ?? "", TrTime.Format(w.PlannedStartAtUtc),
                TrTime.Format(w.PlannedEndAtUtc), w.AssignedCount.ToString(), w.SupervisorName ?? "", TrNames.Of(w.Priority), TrNames.Of(w.Status),
                w.IsOverdue ? "Gecikmiş" : "", TrTime.Format(w.UpdatedAtUtc) }).ToList(),
            FromLocal = query.From ?? DateTime.MinValue, ToLocal = query.To ?? DateTime.MinValue
        };
        return File(ReportService.ToCsv(table), "text/csv; charset=utf-8", $"isler-{TrTime.NowLocal:yyyyMMdd-HHmm}.csv");
    }

    // ───────────────────────── Oluşturma / düzenleme ─────────────────────────

    [HttpGet("/isler/yeni"), Authorize(Policy = Permissions.WorksCreate)]
    public async Task<IActionResult> Create()
    {
        var u = await current.GetAsync();
        var lookups = await definitions.LookupsAsync(u);
        var start = TrTime.TodayLocal.AddDays(1).AddHours(8);
        var input = new WorkOrderInput
        {
            PlannedStartLocal = start, PlannedEndLocal = start.AddHours(9),
            ResponsibleUnitId = u.UnitId ?? (lookups.Units.Count == 1 ? lookups.Units[0].Id : null),
            ManagerUserId = u.UserId
        };
        return View("Form", await FormVm(u, input, null));
    }

    [HttpPost("/isler/yeni"), Authorize(Policy = Permissions.WorksCreate)]
    public async Task<IActionResult> Create(WorkOrderInput input, string[]? materialId, string[]? materialQty, List<IFormFile>? photos,
        string[]? photoCaption, string? next)
    {
        var u = await current.GetAsync();
        List<MaterialLineInput> lines;
        try { lines = ParseMaterials(materialId, materialQty); }
        catch (DomainException e) { ModelState.AddModelError("", e.Message); lines = []; }
        if (!ModelState.IsValid) return View("Form", await FormVm(u, input, null));

        var uploads = new List<UploadInput>();
        try
        {
            for (var i = 0; i < (photos?.Count ?? 0); i++)
            {
                var f = photos![i];
                if (f.Length == 0) continue;
                var ms = new MemoryStream();
                await f.CopyToAsync(ms);
                ms.Position = 0;
                uploads.Add(new UploadInput(ms, f.FileName, f.Length, photoCaption?.ElementAtOrDefault(i), AttachmentType.Photo));
            }
            var w = await works.CreateAsync(u, input, lines, uploads);
            TempData["Ok"] = $"{w.WorkNumber} taslak olarak oluşturuldu.";
            return Redirect(next == "ekip" ? $"/havuz?isId={w.Id}" : $"/isler/{w.Id}");
        }
        catch (DomainException e)
        {
            ModelState.AddModelError("", e.Message);
            return View("Form", await FormVm(u, input, null));
        }
        finally
        {
            foreach (var up in uploads) await up.Content.DisposeAsync();
        }
    }

    [HttpGet("/isler/{id:int}/duzenle"), Authorize(Policy = Permissions.WorksEdit)]
    public async Task<IActionResult> Edit(int id)
    {
        var u = await current.GetAsync();
        var w = await access.LoadVisibleAsync(u, id, withAssignments: false, track: false);
        if (!access.CanManage(u, w)) throw new ForbiddenException();
        if (!WorkflowRules.CanEditDetails(w.Status)) throw new DomainException($"İş '{TrNames.Of(w.Status)}' durumunda; düzenlenemez.");
        var input = new WorkOrderInput
        {
            Title = w.Title, Description = w.Description, Instructions = w.Instructions, WorkCategoryId = w.WorkCategoryId, Priority = w.Priority,
            WorkLocationId = w.WorkLocationId, LocationText = w.LocationText, PlannedStartLocal = TrTime.ToLocal(w.PlannedStartAtUtc),
            PlannedEndLocal = TrTime.ToLocal(w.PlannedEndAtUtc), ResponsibleUnitId = w.ResponsibleUnitId,
            ResponsibleSupervisorEmployeeId = w.ResponsibleSupervisorEmployeeId, ManagerUserId = w.ManagerUserId
        };
        var vm = await FormVm(u, input, w);
        return View("Form", vm);
    }

    [HttpPost("/isler/{id:int}/duzenle"), Authorize(Policy = Permissions.WorksEdit)]
    public async Task<IActionResult> Edit(int id, WorkOrderInput input, long version)
    {
        var u = await current.GetAsync();
        if (!ModelState.IsValid)
        {
            var w = await access.LoadVisibleAsync(u, id, withAssignments: false, track: false);
            return View("Form", await FormVm(u, input, w));
        }
        try
        {
            var r = await works.UpdateAsync(u, id, input, version);
            TempData["Ok"] = r.Message;
            return Redirect($"/isler/{id}");
        }
        catch (DomainException e)
        {
            ModelState.AddModelError("", e.Message);
            var w = await access.LoadVisibleAsync(u, id, withAssignments: false, track: false);
            return View("Form", await FormVm(u, input, w));
        }
    }

    private async Task<WorkFormViewModel> FormVm(UserContext u, WorkOrderInput input, Saha360.Domain.Entities.WorkOrder? existing) => new()
    {
        Input = input,
        Existing = existing,
        Lookups = await definitions.LookupsAsync(u),
        Materials = existing is null ? await materials.ListAsync(null, false) : []
    };

    // ───────────────────────── Detay ─────────────────────────

    [HttpGet("/isler/{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        var u = await current.GetAsync();
        var d = await queries.GetDetailAsync(u, id);
        ViewData["Materials"] = d.CanContribute ? await materials.ListAsync(null, false) : [];
        return View(d);
    }

    // ───────────────────────── İş akışı eylemleri ─────────────────────────

    [HttpPost("/isler/{id:int}/kabul")]
    public Task<IActionResult> Accept(int id) => Act(id, u => workflow.AcceptAsync(u, id));

    // Gerekçe/açıklama isteyen eylemler pencereden fotoğrafla birlikte gelebilir. Sıra: gerekçe ve dosyalar denetlenir →
    // fotoğraflar işe eklenir (iş tamamlanınca veya personel reddedince artık dosya eklenemez) → durum değişir.

    [HttpPost("/isler/{id:int}/ret")]
    public Task<IActionResult> Reject(int id, string? reason, List<IFormFile>? files) =>
        ActWithFiles(id, files, AttachmentType.Photo, "Ret sırasında eklendi", () => WorkflowService.RequireReason(reason, "Ret gerekçesi"),
            u => workflow.RejectAsync(u, id, reason));

    [HttpPost("/isler/{id:int}/basla")]
    public Task<IActionResult> Start(int id) => Act(id, u => workflow.StartAsync(u, id));

    [HttpPost("/isler/{id:int}/tamamla")]
    public Task<IActionResult> Submit(int id, string? note, List<IFormFile>? files) =>
        ActWithFiles(id, files, AttachmentType.AfterWork, "Tamamlanma bildirimiyle eklendi", () => WorkflowService.RequireReason(note, "Tamamlanma açıklaması"),
            u => workflow.SubmitCompletionAsync(u, id, note));

    [HttpPost("/isler/{id:int}/onayla")]
    public Task<IActionResult> Approve(int id, string? note, List<IFormFile>? files) =>
        ActWithFiles(id, files, AttachmentType.Photo, "Şef onayıyla eklendi", null, u => workflow.ApproveAsync(u, id, note));

    [HttpPost("/isler/{id:int}/iade")]
    public Task<IActionResult> Return(int id, string? reason, List<IFormFile>? files) =>
        ActWithFiles(id, files, AttachmentType.Photo, "İade gerekçesi", () => WorkflowService.RequireReason(reason, "İade gerekçesi"),
            u => workflow.ReturnAsync(u, id, reason));

    [HttpPost("/isler/{id:int}/bilgi-iste")]
    public Task<IActionResult> RequestInfo(int id, string? reason, List<IFormFile>? files) =>
        ActWithFiles(id, files, AttachmentType.Photo, "Ek bilgi talebiyle eklendi", () => WorkflowService.RequireReason(reason, "İstenen bilgi"),
            u => workflow.RequestInfoAsync(u, id, reason));

    [HttpPost("/isler/{id:int}/iptal"), Authorize(Policy = Permissions.WorksCancel)]
    public Task<IActionResult> Cancel(int id, string? reason, List<IFormFile>? files) =>
        ActWithFiles(id, files, AttachmentType.Photo, "İptal gerekçesi", () => WorkflowService.RequireReason(reason, "İptal gerekçesi"),
            u => workflow.CancelAsync(u, id, reason));

    [HttpPost("/isler/{id:int}/yeniden-ac"), Authorize(Policy = Permissions.WorksReopen)]
    public Task<IActionResult> Reopen(int id, string? reason, DateTime? start, DateTime? end, bool restoreTeam) =>
        Act(id, u => assignments.ReopenAsync(u, id, reason, start, end, restoreTeam));

    private async Task<IActionResult> ActWithFiles(int id, List<IFormFile>? files, AttachmentType type, string caption, Action? precheck,
        Func<UserContext, Task<OpResult>> op)
    {
        var u = await current.GetAsync();
        precheck?.Invoke();
        var added = await SaveFilesAsync(u, id, files, type, caption);
        var r = await op(u);
        TempData[r.Changed ? "Ok" : "Info"] = r.Message + (added == 0 ? "" : added == 1 ? " 1 fotoğraf eklendi." : $" {added} fotoğraf eklendi.");
        return Redirect($"/isler/{id}");
    }

    /// <summary>Önce tüm dosyaları denetler (biri bile geçersizse hiçbirini kaydetmez), sonra işe ekler.</summary>
    private async Task<int> SaveFilesAsync(UserContext u, int id, List<IFormFile>? files, AttachmentType type, string? caption)
    {
        var list = files?.Where(f => f.Length > 0).ToList() ?? [];
        if (list.Count == 0) return 0;
        foreach (var f in list)
        {
            await using var s = f.OpenReadStream();
            var header = new byte[16];
            var n = await s.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false);
            var check = FileValidator.Validate(f.FileName, f.Length, header.AsSpan(0, n));
            if (!check.Ok) throw new DomainException($"{FileValidator.SafeDisplayName(f.FileName)}: {check.Error} İşlem yapılmadı.");
        }
        foreach (var f in list)
        {
            await using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            ms.Position = 0;
            await content.AddAttachmentAsync(u, id, new UploadInput(ms, f.FileName, f.Length, caption, type));
        }
        return list.Count;
    }

    [HttpPost("/isler/{id:int}/not")]
    public Task<IActionResult> AddNote(int id, string? text) => Act(id, u => content.AddNoteAsync(u, id, text), tab: "notlar");

    [HttpPost("/isler/{id:int}/malzeme")]
    public Task<IActionResult> AddMaterial(int id, int materialId, string? quantity, string? note) =>
        Act(id, u => content.AddMaterialAsync(u, id, materialId, ParseDecimal(quantity), note), tab: "malzemeler");

    [HttpPost("/isler/{id:int}/malzeme/{rowId:int}/sil")]
    public Task<IActionResult> RemoveMaterial(int id, int rowId) => Act(id, u => content.RemoveMaterialAsync(u, id, rowId), tab: "malzemeler");

    [HttpPost("/isler/{id:int}/dosya")]
    public async Task<IActionResult> Upload(int id, List<IFormFile>? files, string? caption, AttachmentType type = AttachmentType.Photo)
    {
        var u = await current.GetAsync();
        if (files is null || files.Count == 0 || files.All(f => f.Length == 0))
        {
            TempData["Error"] = "Yüklenecek dosya seçin.";
            return Redirect($"/isler/{id}#dosyalar");
        }
        var count = await SaveFilesAsync(u, id, files, type, caption);
        TempData["Ok"] = count == 1 ? "Dosya yüklendi." : $"{count} dosya yüklendi.";
        return Redirect($"/isler/{id}#dosyalar");
    }

    [HttpPost("/isler/{id:int}/dosya/{attId:int}/sil")]
    public Task<IActionResult> DeleteFile(int id, int attId) => Act(id, u => content.DeleteAttachmentAsync(u, id, attId), tab: "dosyalar");

    /// <summary>Dosya sunumu: her istekte iş görme yetkisi kontrol edilir.</summary>
    [HttpGet("/dosya/{id:int}")]
    public async Task<IActionResult> Download(int id, bool indir = false)
    {
        var u = await current.GetAsync();
        var (stream, type, name) = await content.OpenAttachmentAsync(u, id);
        Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; img-src 'self'; style-src 'unsafe-inline'";
        Response.Headers.CacheControl = "private, max-age=3600";
        return indir || !type.StartsWith("image/") ? File(stream, type, name) : File(stream, type);
    }

    private async Task<IActionResult> Act(int id, Func<UserContext, Task<OpResult>> op, string? redirect = null, string? tab = null)
    {
        var u = await current.GetAsync();
        var r = await op(u);
        TempData[r.Changed ? "Ok" : "Info"] = r.Message;
        return Redirect(redirect ?? $"/isler/{id}" + (tab is null ? "" : "#" + tab));
    }

    /// <summary>Sayı alanları tarayıcıdan "2.5" gelir, kullanıcı "2,5" yazabilir: ikisi de kabul edilir.</summary>
    internal static decimal ParseDecimal(string? s)
    {
        var t = (s ?? "").Trim().Replace(" ", "");
        if (t.Contains(',') && !t.Contains('.')) t = t.Replace(',', '.');
        if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d))
            throw new DomainException("Miktar geçerli bir sayı olmalı.");
        return d;
    }

    internal static List<MaterialLineInput> ParseMaterials(string[]? ids, string[]? qtys)
    {
        var list = new List<MaterialLineInput>();
        for (var i = 0; i < (ids?.Length ?? 0); i++)
        {
            if (!int.TryParse(ids![i], out var mid)) continue;
            var q = ParseDecimal(qtys?.ElementAtOrDefault(i));
            if (q <= 0) throw new DomainException("Malzeme miktarı sıfırdan büyük olmalı.");
            list.Add(new MaterialLineInput(mid, q, null));
        }
        return list;
    }
}
