using Microsoft.EntityFrameworkCore;

namespace MatMail.Services;

/// <summary>One page of a list plus what the pagination control needs.</summary>
public sealed record PageResult<T>(IReadOnlyList<T> Rows, int Total, int PageNumber, int TotalPages, int PageSize);

public static class Pager
{
    public const int DefaultPageSize = 20;

    /// <summary>Counts, clamps the requested page into range and loads it.</summary>
    public static async Task<PageResult<T>> ToPageAsync<T>(this IQueryable<T> query, int pageNumber, int pageSize = DefaultPageSize)
    {
        int total = await query.CountAsync();
        int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        int page = Math.Clamp(pageNumber, 1, totalPages);
        List<T> rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PageResult<T>(rows, total, page, totalPages, pageSize);
    }

    /// <summary>"%text%" for ILIKE with the LIKE wildcards of the input escaped.</summary>
    public static string ContainsPattern(string text)
        => "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
