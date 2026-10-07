using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Helpers;

public interface IPagedList
{
    int PageIndex { get; }
    int PageSize { get; }
    int TotalCount { get; }
    int TotalPages { get; }
}

public class PagedList<T> : List<T>, IPagedList
{
    public const int DefaultPageSize = 10;

    private PagedList(List<T> items, int count, int pageIndex, int pageSize)
    {
        PageIndex = pageIndex;
        PageSize = pageSize;
        TotalCount = count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(count / (double)pageSize));
        AddRange(items);
    }

    public int PageIndex { get; }
    public int PageSize { get; }
    public int TotalCount { get; }
    public int TotalPages { get; }

    public static async Task<PagedList<T>> CreateAsync(IQueryable<T> source, int page, int pageSize = DefaultPageSize)
    {
        pageSize = Math.Clamp(pageSize, 5, 100);
        var count = await source.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(count / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        var items = await source.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedList<T>(items, count, page, pageSize);
    }

    public static PagedList<T> Create(IEnumerable<T> source, int page, int pageSize = DefaultPageSize)
    {
        var list = source.ToList();
        pageSize = Math.Clamp(pageSize, 5, 100);
        var totalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);
        return new PagedList<T>(list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), list.Count, page, pageSize);
    }
}

public static class UrlQueryExtensions
{
    /// <summary>Current URL with some query-string values replaced (null removes the key).</summary>
    public static string WithQuery(this HttpRequest request, params (string Key, string? Value)[] changes)
    {
        var values = request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in changes)
        {
            if (value is null) values.Remove(key);
            else values[key] = value;
        }
        var query = QueryString.Create(values.Where(v => !string.IsNullOrEmpty(v.Value)));
        return request.PathBase + request.Path + query;
    }

    public static bool IsApiRequest(this HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Plain-language titles for API error responses.</summary>
public static class ApiErrors
{
    public static string TitleFor(int status) => status switch
    {
        400 => "The request is not valid.",
        401 => "Authentication required. Sign in with POST /api/auth/login.",
        403 => "You do not have permission to perform this action.",
        404 => "Resource not found.",
        405 => "This HTTP method is not allowed for this endpoint.",
        409 => "The resource conflicts with an existing record.",
        415 => "Unsupported media type. Send JSON with Content-Type: application/json.",
        429 => "Too many requests. Wait a minute and try again.",
        _ => "The request could not be completed."
    };
}

public static class DisplayHelpers
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>"ColdCall" → "Cold Call", "SalesExecutive" → "Sales Executive".</summary>
    public static string Label(this Enum value) => SplitWords(value.ToString());

    public static string SplitWords(string value) => Regex.Replace(value, "(?<=[a-z])([A-Z])", " $1");

    public static string Money(this decimal amount) => "₹" + amount.ToString("N0", India);

    public static string Money(this double amount) => ((decimal)amount).Money();

    /// <summary>Select-list items whose value is the enum name (so client rules can compare names).</summary>
    public static List<SelectListItem> EnumOptions<TEnum>(IEnumerable<TEnum>? only = null) where TEnum : struct, Enum =>
        (only ?? Enum.GetValues<TEnum>())
            .Select(v => new SelectListItem(SplitWords(v.ToString()), v.ToString()))
            .ToList();

    public static string Badge(Enum value) => value switch
    {
        LeadStatus.New => "text-bg-primary",
        LeadStatus.Contacted => "text-bg-info",
        LeadStatus.Qualified => "text-bg-success",
        LeadStatus.Unqualified => "text-bg-secondary",
        LeadStatus.Converted => "text-bg-dark",
        LeadStatus.Lost => "text-bg-danger",
        OpportunityStage.Qualification => "text-bg-info",
        OpportunityStage.Proposal => "text-bg-primary",
        OpportunityStage.Negotiation => "text-bg-warning",
        OpportunityStage.Won => "text-bg-success",
        OpportunityStage.Lost => "text-bg-danger",
        OpportunityStatus.Open => "text-bg-primary",
        OpportunityStatus.Won => "text-bg-success",
        OpportunityStatus.Lost => "text-bg-danger",
        FollowUpStatus.Planned => "text-bg-primary",
        FollowUpStatus.Completed => "text-bg-success",
        FollowUpStatus.Missed => "text-bg-danger",
        FollowUpStatus.Cancelled => "text-bg-secondary",
        ActivityStatus.Planned => "text-bg-primary",
        ActivityStatus.Completed => "text-bg-success",
        ActivityStatus.Cancelled => "text-bg-secondary",
        CustomerStatus.Active => "text-bg-success",
        CustomerStatus.Prospect => "text-bg-info",
        CustomerStatus.Inactive => "text-bg-secondary",
        Priority.High => "text-bg-danger",
        Priority.Medium => "text-bg-warning",
        Priority.Low => "text-bg-secondary",
        _ => "text-bg-light"
    };

    public static IHtmlContent StatusBadge(this IHtmlHelper html, Enum value) =>
        new HtmlString($"<span class=\"badge {Badge(value)}\">{HtmlEncoder.Default.Encode(value.Label())}</span>");

    /// <summary>Sortable column header that toggles asc/desc via the "sort" query value.</summary>
    public static IHtmlContent SortHeader(this IHtmlHelper html, string key, string label)
    {
        var request = html.ViewContext.HttpContext.Request;
        var current = request.Query["sort"].ToString();
        var next = current == key ? key + "_desc" : key;
        var icon = current == key ? "bi-caret-up-fill" : current == key + "_desc" ? "bi-caret-down-fill" : "bi-chevron-expand text-body-tertiary";
        var url = request.WithQuery(("sort", next), ("page", null));
        return new HtmlString(
            $"<a class=\"sort-link\" href=\"{HtmlEncoder.Default.Encode(url)}\">{HtmlEncoder.Default.Encode(label)} <i class=\"bi {icon}\"></i></a>");
    }
}

/// <summary>Builds CSV exports, neutralising spreadsheet formula injection.</summary>
public static class CsvExport
{
    public static byte[] Build(IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(v => Escape(Format(v)))));
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm"),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        Enum e => e.Label(),
        _ => value.ToString() ?? string.Empty
    };

    private static string Escape(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

public static class ObjectValidator
{
    /// <summary>Runs DataAnnotations on an object (used by services as a second line of defence).</summary>
    public static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }
}
