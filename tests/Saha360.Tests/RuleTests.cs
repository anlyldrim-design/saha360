using System.Text;
using Saha360.Domain.Enums;
using Saha360.Domain.Rules;
using Saha360.Infrastructure.FileStorage;
using Saha360.Infrastructure.Services;

namespace Saha360.Tests;

/// <summary>Veritabanı gerektirmeyen iş kuralları.</summary>
public class RuleTests
{
    [Theory]
    [InlineData(WorkStatus.Draft, WorkStatus.AwaitingAcceptance, true)]
    [InlineData(WorkStatus.AwaitingAcceptance, WorkStatus.Planned, true)]
    [InlineData(WorkStatus.Planned, WorkStatus.InProgress, true)]
    [InlineData(WorkStatus.InProgress, WorkStatus.CompletionApprovalPending, true)]
    [InlineData(WorkStatus.CompletionApprovalPending, WorkStatus.Completed, true)]
    [InlineData(WorkStatus.CompletionApprovalPending, WorkStatus.ReturnedBySupervisor, true)]
    [InlineData(WorkStatus.ReturnedBySupervisor, WorkStatus.CompletionApprovalPending, true)]
    [InlineData(WorkStatus.Draft, WorkStatus.Completed, false)]
    [InlineData(WorkStatus.InProgress, WorkStatus.Completed, false)]
    [InlineData(WorkStatus.Completed, WorkStatus.InProgress, false)]
    [InlineData(WorkStatus.Cancelled, WorkStatus.Draft, false)]
    [InlineData(WorkStatus.CompletionApprovalPending, WorkStatus.Cancelled, false)]
    public void Transition_matrix(WorkStatus from, WorkStatus to, bool allowed) =>
        Assert.Equal(allowed, WorkflowRules.CanTransition(from, to));

    [Fact]
    public void Closed_states_can_only_be_reopened_to_assignment_states()
    {
        foreach (var s in new[] { WorkStatus.Completed, WorkStatus.Cancelled })
            Assert.Equal(new[] { WorkStatus.AwaitingAcceptance, WorkStatus.AwaitingAssignment }.Order(), WorkflowRules.TargetsOf(s).Order());
        Assert.True(WorkflowRules.IsFinal(WorkStatus.Completed));   // kapalıyken içerik/düzenleme yine yasak
    }

    [Fact]
    public void Back_to_back_intervals_do_not_overlap()
    {
        var t = new DateTime(2026, 10, 28, 8, 0, 0);
        Assert.False(TimeRange.Overlaps(t, t.AddHours(4), t.AddHours(4), t.AddHours(8)));
        Assert.True(TimeRange.Overlaps(t, t.AddHours(4), t.AddHours(3), t.AddHours(8)));
        Assert.True(TimeRange.Overlaps(t, t.AddHours(8), t.AddHours(1), t.AddHours(2)));
    }

    [Fact]
    public void File_signature_must_match_extension()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };
        var exe = Encoding.ASCII.GetBytes("MZ\x90\0\0\0\0\0\0\0\0\0");
        Assert.True(FileValidator.Validate("foto.png", 100, png).Ok);
        Assert.False(FileValidator.Validate("foto.jpg", 100, exe).Ok);         // uzantı değiştirilmiş program
        Assert.False(FileValidator.Validate("foto.png", 100, exe).Ok);
        Assert.False(FileValidator.Validate("betik.html", 100, png).Ok);        // izin verilmeyen tür
        Assert.False(FileValidator.Validate("buyuk.png", FileValidator.MaxBytes + 1, png).Ok);
        Assert.True(FileValidator.Validate("rapor.pdf", 100, "%PDF-1.7\n"u8).Ok);
    }

    [Fact]
    public void Display_name_strips_path_parts() =>
        Assert.Equal("evil.png", FileValidator.SafeDisplayName("..\\..\\windows\\evil.png"));

    [Fact]
    public void Same_material_lines_are_merged()
    {
        var merged = WorkOrderService.MergeLines([new(1, 2.5m, null), new(2, 1, null), new(1, 0.5m, null)]);
        Assert.Equal(2, merged.Count);
        Assert.Equal(3m, merged.Single(m => m.MaterialId == 1).Quantity);
    }

    [Fact]
    public void Csv_escapes_formula_injection_and_uses_semicolons()
    {
        var t = new ReportTable
        {
            Definition = new ReportDefinition("x", "Test", "", ""),
            Columns = ["A", "B"],
            Rows = [["=HYPERLINK(\"kötü\")", "1;2"]]
        };
        var csv = Encoding.UTF8.GetString(ReportService.ToCsv(t));
        Assert.Contains("'=HYPERLINK", csv);
        Assert.Contains("\"1;2\"", csv);
    }

    [Fact]
    public void Turkey_time_round_trips()
    {
        var local = new DateTime(2026, 10, 28, 8, 0, 0);
        var utc = TrTime.ToUtc(local);
        Assert.Equal(new DateTime(2026, 10, 28, 5, 0, 0), utc);
        Assert.Equal(local, TrTime.ToLocal(utc));
    }
}
