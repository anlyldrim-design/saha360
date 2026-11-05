using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Domain.Rules;
using Saha360.Domain.Security;
using Saha360.Infrastructure.Persistence;
using Saha360.Infrastructure.Services;
using Saha360.Web.Infrastructure;
using Saha360.Web.ViewModels;

namespace Saha360.Web.Controllers;

public class SaveAssignmentsRequest
{
    public List<AssignmentInput> Assignments { get; set; } = [];
    public bool Send { get; set; }
    public long? Version { get; set; }

    /// <summary>Ekrandaki ekip alanları (sırasıyla). Boş alanlar da saklanır.</summary>
    public List<int>? Groups { get; set; }
}

/// <summary>
/// Personel havuzu ve ekip planlama. Sürükle-bırak yalnız ekrandaki taslağı değiştirir;
/// "Taslağı kaydet / Atamaları kaydet / Personele gönder" tek istekte, sunucuda atomik kaydedilir.
/// </summary>
[Authorize(Policy = Policies.OrgView)]
public class PoolController(CurrentUser current, AvailabilityService availability, AssignmentService assignments, WorkQueryService queries,
    DefinitionService definitions, AccessService access, AppDbContext db) : Controller
{
    [HttpGet("/havuz")]
    public async Task<IActionResult> Index(int? isId, [FromQuery] PoolQuery query, [FromQuery(Name = "durum")] PoolState? state)
    {
        var u = await current.GetAsync();
        query.State ??= state;
        WorkDetail? work = null;
        DateTime from, to;
        if (isId is int id)
        {
            work = await queries.GetDetailAsync(u, id);
            if (!work.CanAssignWork) throw new ForbiddenException("Bu işin ekibini planlama yetkiniz yok.");
            from = work.Work.PlannedStartAtUtc;
            to = work.Work.PlannedEndAtUtc;
        }
        else
        {
            from = query.FromLocal is DateTime f ? TrTime.ToUtc(f) : DateTime.UtcNow;
            to = query.ToLocal is DateTime t ? TrTime.ToUtc(t) : from.AddMinutes(1);
            if (to <= from) to = from.AddMinutes(1);
        }

        var openStatuses = new[] { WorkStatus.Draft, WorkStatus.AwaitingAssignment, WorkStatus.AwaitingAcceptance, WorkStatus.Planned,
            WorkStatus.InProgress, WorkStatus.ReturnedBySupervisor };
        // İş seçici yalnız atama izni olan kişiye ve yalnız kendi biriminin işleriyle gelir.
        var all = u.AllUnits;
        var myUnit = u.UnitId ?? -1;
        var works = !u.CanAssign ? [] : await access.VisibleWorks(u).AsNoTracking()
            .Where(w => openStatuses.Contains(w.Status) && (all || w.ResponsibleUnitId == myUnit))
            .OrderBy(w => w.PlannedStartAtUtc).Take(200)
            .Select(w => new PoolWorkOption(w.Id, w.WorkNumber, w.Title, w.Status, w.PlannedStartAtUtc, w.PlannedEndAtUtc)).ToListAsync();

        var vm = new PoolViewModel
        {
            Query = query,
            Pool = await availability.GetPoolAsync(u, query, from, to, isId),
            Work = work,
            Works = works,
            Lookups = await definitions.LookupsAsync(u),
            FromUtc = from,
            ToUtc = to,
            // Tüm unvanlar: "+ Ekip alanı ekle" listesi buradan gelir (pasifler yalnız işte zaten kullanılıyorsa gösterilir).
            Groups = await db.JobRoles.AsNoTracking().OrderByDescending(r => r.IsLeadRole).ThenBy(r => r.SortOrder)
                .ThenBy(r => r.Name).ToListAsync()
        };
        return View(vm);
    }

    [HttpPost("/havuz/{workId:int}/kaydet"), Authorize(Policy = Permissions.WorksAssign)]
    public async Task<IActionResult> Save(int workId, [FromBody] SaveAssignmentsRequest request)
    {
        var u = await current.GetAsync();
        var r = await assignments.SaveAsync(u, workId, request.Assignments ?? [], request.Send, request.Version, request.Groups);
        TempData["Ok"] = r.Message;
        return Json(new { ok = true, message = r.Message, redirect = request.Send ? $"/isler/{workId}" : $"/havuz?isId={workId}" });
    }
}

public class MyWorksController(CurrentUser current, WorkQueryService queries) : Controller
{
    [HttpGet("/islerim")]
    public async Task<IActionResult> Index()
    {
        var u = await current.GetAsync();
        if (u.EmployeeId is null)
        {
            TempData["Info"] = "Hesabınız bir personel kaydına bağlı değil; \"İşlerim\" ekranı boş görünür.";
        }
        return View(await queries.MyWorksAsync(u));
    }
}

/// <summary>Herkes açabilir; yalnız onaylayabileceği (sorumlu şefi olduğu ya da onay izni olan) işler listelenir.</summary>
public class ApprovalsController(CurrentUser current, AccessService access, AppDbContext db) : Controller
{
    /// <summary>Şef yalnız kendi kapsamındaki ve onayına gelen işleri görür.</summary>
    [HttpGet("/onay")]
    public async Task<IActionResult> Index()
    {
        var u = await current.GetAsync();
        var pending = await access.VisibleWorks(u).AsNoTracking()
            .Where(w => w.Status == WorkStatus.CompletionApprovalPending)
            .Include(w => w.ResponsibleSupervisorEmployee).Include(w => w.WorkCategory).Include(w => w.WorkLocation)
            .Include(w => w.Assignments).ThenInclude(a => a.Employee)
            .Include(w => w.Assignments).ThenInclude(a => a.JobRole)
            .AsSplitQuery()
            .OrderBy(w => w.CompletionSubmittedAtUtc).Take(200).ToListAsync();
        var mine = pending.Where(w => access.CanApprove(u, w)).ToList();
        var ids = mine.Select(w => w.Id).ToList();
        var photos = await db.WorkAttachments.Where(a => ids.Contains(a.WorkOrderId) && a.DeletedAtUtc == null)
            .GroupBy(a => a.WorkOrderId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C);
        var notes = await db.WorkNotes.Where(a => ids.Contains(a.WorkOrderId))
            .GroupBy(a => a.WorkOrderId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C);
        var mats = await db.WorkMaterials.Where(a => ids.Contains(a.WorkOrderId))
            .GroupBy(a => a.WorkOrderId).Select(g => new { g.Key, C = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.C);
        var now = DateTime.UtcNow;
        var cards = mine.Select(w => new ApprovalCard
        {
            Work = new WorkListItem(w.Id, w.WorkNumber, w.Title, w.WorkCategory?.Name,
                w.WorkLocation is null ? w.LocationText : w.WorkLocation.Name + (w.LocationText is null ? "" : " · " + w.LocationText),
                w.PlannedStartAtUtc, w.PlannedEndAtUtc, w.Assignments.Count(a => a.ReleasedAtUtc == null), w.ResponsibleSupervisorEmployee?.FullName,
                w.Priority, w.Status, WorkflowRules.IsOverdue(w.Status, w.PlannedEndAtUtc, now), w.UpdatedAtUtc),
            CompletionNote = w.CompletionNote,
            SubmittedAtUtc = w.CompletionSubmittedAtUtc,
            ActualStartAtUtc = w.ActualStartAtUtc,
            PhotoCount = photos.GetValueOrDefault(w.Id),
            NoteCount = notes.GetValueOrDefault(w.Id),
            MaterialCount = mats.GetValueOrDefault(w.Id),
            Team = w.Assignments.Where(a => a.ReleasedAtUtc == null).Select(a => $"{a.Employee.FullName} ({a.JobRole.Name})").ToList()
        }).ToList();
        return View(cards);
    }
}
