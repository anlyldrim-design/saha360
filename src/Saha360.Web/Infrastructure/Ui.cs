using System.Net;
using Microsoft.AspNetCore.Html;
using Saha360.Domain.Enums;

namespace Saha360.Web.Infrastructure;

/// <summary>Görünümlerde tekrar eden küçük parçalar. Renk hiçbir zaman tek başına anlam taşımaz; rozetlerde metin vardır.</summary>
public static class Ui
{
    public static IHtmlContent Icon(string name, string cls = "") =>
        new HtmlString($"<svg class=\"ic {cls}\" aria-hidden=\"true\"><use href=\"#i-{name}\"/></svg>");

    public static IHtmlContent Status(WorkStatus s) =>
        new HtmlString($"<span class=\"badge {TrNames.Badge(s)}\">{WebUtility.HtmlEncode(TrNames.Of(s))}</span>");

    public static IHtmlContent Priority(WorkPriority p) =>
        new HtmlString($"<span class=\"badge {TrNames.Badge(p)}\">{(p == WorkPriority.Urgent ? "⚠ " : "")}{WebUtility.HtmlEncode(TrNames.Of(p))}</span>");

    public static IHtmlContent Assignment(AssignmentStatus s) =>
        new HtmlString($"<span class=\"badge {TrNames.Badge(s)}\">{WebUtility.HtmlEncode(TrNames.Of(s))}</span>");

    public static IHtmlContent Overdue(bool overdue) =>
        overdue ? new HtmlString("<span class=\"badge text-bg-danger\" title=\"Planlanan bitiş geçti\">⏱ Gecikmiş</span>") : HtmlString.Empty;

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };

    /// <summary>Sorgu dizesine sayfa numarası koyarak süzgeçleri korur.</summary>
    public static string WithPage(Microsoft.AspNetCore.Http.HttpRequest req, int page)
    {
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(req.QueryString.Value)
            .Where(kv => !string.Equals(kv.Key, "page", StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v)))
            .Append(new KeyValuePair<string, string?>("page", page.ToString()));
        return req.Path + Microsoft.AspNetCore.Http.QueryString.Create(q).ToString();
    }
}
