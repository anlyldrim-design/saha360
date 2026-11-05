using Saha360.Domain.Enums;
using Saha360.Domain.Exceptions;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Şartname 14.3 ve 14.4 — malzeme, not, fotoğraf, raporlar ve yetki kapsamı.</summary>
public class ContentAndReportTests : IDisposable
{
    private readonly TestHost h = new();
    public void Dispose() => h.Dispose();
    private WorkContentService Content => h.Get<WorkContentService>();

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private async Task<int> SentWorkAsync()
    {
        var w = await h.CreateAsync();
        await h.AssignAsync(w, true, h.A(h.Ali));
        return w.Id;
    }

    [Fact]
    public async Task Material_quantity_must_be_positive_and_same_material_is_merged()
    {
        var id = await SentWorkAsync();
        await Assert.ThrowsAsync<DomainException>(() => Content.AddMaterialAsync(h.Ali, id, h.Pipe.Id, -2, null));
        await Assert.ThrowsAsync<DomainException>(() => Content.AddMaterialAsync(h.Ali, id, h.Pipe.Id, 0, null));
        await Assert.ThrowsAsync<DomainException>(() => Content.AddMaterialAsync(h.Ali, id, h.Pipe.Id, 1.2345m, null));
        await Content.AddMaterialAsync(h.Ali, id, h.Pipe.Id, 2.5m, null);
        await Content.AddMaterialAsync(h.Ali, id, h.Pipe.Id, 1.5m, null);
        var row = Assert.Single(h.Db.WorkMaterials.Where(m => m.WorkOrderId == id));
        Assert.Equal(4m, row.Quantity);
        Assert.Equal("metre", row.UnitSnapshot);
    }

    [Fact]
    public async Task Assigned_personnel_can_add_note_and_photo_outsider_cannot()
    {
        var id = await SentWorkAsync();
        await Content.AddNoteAsync(h.Ali, id, "Sahaya ulaştık.");
        await Content.AddAttachmentAsync(h.Ali, id, new UploadInput(new MemoryStream(Png), "foto.png", Png.Length, "Önce", AttachmentType.BeforeWork));
        Assert.Single(h.Files.Files);

        await Assert.ThrowsAsync<NotFoundException>(() => Content.AddNoteAsync(h.Outsider, id, "Merhaba"));
        // Aynı birimde ama bu işte olmayan personel işi göremez bile (varlığı da açığa çıkmaz).
        await Assert.ThrowsAsync<NotFoundException>(() => Content.AddNoteAsync(h.Fatma, id, "Ben de yazayım"));
        // Şef işi görür ve not ekleyebilir.
        await Content.AddNoteAsync(h.Supervisor, id, "Kontrol ettim.");
    }

    [Fact]
    public async Task Unauthorized_user_cannot_open_file_even_with_its_id()
    {
        var id = await SentWorkAsync();
        await Content.AddAttachmentAsync(h.Ali, id, new UploadInput(new MemoryStream(Png), "foto.png", Png.Length, null, AttachmentType.Photo));
        var attId = h.Db.WorkAttachments.Single().Id;

        var (stream, type, _) = await Content.OpenAttachmentAsync(h.Supervisor, attId);
        Assert.Equal("image/png", type);
        await stream.DisposeAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => Content.OpenAttachmentAsync(h.Outsider, attId));
    }

    [Fact]
    public async Task Forbidden_file_type_and_oversize_are_rejected()
    {
        var id = await SentWorkAsync();
        var exe = "MZ\u0090\0\0\0\0\0\0\0\0\0"u8.ToArray();
        await Assert.ThrowsAsync<DomainException>(() =>
            Content.AddAttachmentAsync(h.Ali, id, new UploadInput(new MemoryStream(exe), "foto.jpg", exe.Length, null, AttachmentType.Photo)));
        await Assert.ThrowsAsync<DomainException>(() =>
            Content.AddAttachmentAsync(h.Ali, id, new UploadInput(new MemoryStream(Png), "foto.png", 50L * 1024 * 1024, null, AttachmentType.Photo)));
        Assert.Empty(h.Files.Files);
    }

    [Fact]
    public async Task Removing_a_file_is_soft_and_audited()
    {
        var id = await SentWorkAsync();
        await Content.AddAttachmentAsync(h.Ali, id, new UploadInput(new MemoryStream(Png), "foto.png", Png.Length, null, AttachmentType.Photo));
        var attId = h.Db.WorkAttachments.Single().Id;
        await Content.DeleteAttachmentAsync(h.Ali, id, attId);
        h.Db.ChangeTracker.Clear();
        Assert.NotNull(h.Db.WorkAttachments.Single().DeletedAtUtc);
        Assert.Contains(h.Db.AuditLogs, a => a.WorkOrderId == id && a.Action == "AttachmentRemoved");
        await Assert.ThrowsAsync<NotFoundException>(() => Content.OpenAttachmentAsync(h.Ali, attId));
    }

    [Fact]
    public async Task Report_separates_work_count_from_assignment_count()
    {
        var w = await h.CreateAsync("Ekip işi", startHoursFromNow: -2, durationHours: 4);
        await h.AssignAsync(w, true, h.A(h.Ali), h.A(h.Fatma), h.A(h.NoAccount));
        var t = await h.Get<ReportService>().RunAsync(h.Planner, new ReportQuery { Key = "ozet" });
        Assert.Equal("1", t.Rows.Single(r => r[0] == "Oluşturulan iş")[1]);
        Assert.Equal("3", t.Rows.Single(r => r[0] == "Personel ataması")[1]);
    }

    [Fact]
    public async Task Reports_are_limited_to_users_scope()
    {
        await h.CreateAsync("Birim işi", -2, 4);
        var foreignPlanner = await h.UserAsync("yabanci-rapor", Domain.Security.SystemRoles.ReportViewer, null,
            h.Db.OrganizationUnits.Single(u => u.Name == "Diğer Birim").Id);
        var mine = await h.Get<ReportService>().RunAsync(h.Planner, new ReportQuery { Key = "durum" });
        var theirs = await h.Get<ReportService>().RunAsync(foreignPlanner, new ReportQuery { Key = "durum" });
        Assert.Equal("1", mine.Rows.Last()[1]);
        Assert.Equal("0", theirs.Rows.Last()[1]);
        await Assert.ThrowsAsync<ForbiddenException>(() => h.Get<ReportService>().RunAsync(h.Ali, new ReportQuery()));
    }

    [Fact]
    public async Task Inactive_employee_history_is_kept_in_reports()
    {
        var w = await h.CreateAsync("Eski iş", -3, 2);
        await h.AssignAsync(w, true, h.A(h.NoAccount));
        await h.Flow.CancelAsync(h.Planner, w.Id, "Test için iptal");
        await h.Get<EmployeeService>().SaveAsync(h.Manager, h.NoAccount.Id, new EmployeeInput
        {
            FirstName = h.NoAccount.FirstName, LastName = h.NoAccount.LastName, OrganizationUnitId = h.Unit.Id, IsActive = false
        });
        var t = await h.Get<ReportService>().RunAsync(h.Planner, new ReportQuery { Key = "personel" });
        Assert.Contains(t.Rows, r => r[0].Contains("Hesapsız") && r[0].Contains("pasif"));
    }
}
