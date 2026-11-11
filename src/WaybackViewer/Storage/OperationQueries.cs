using System.Data.Common;
using System.Text;
using WaybackViewer.Models;

namespace WaybackViewer.Storage;

/// Indexed, URL-scoped operation queries shared by the SQL stores (SQLite and the dialect-based store).
/// All values are parameters; only parameter names are generated.
internal static class OperationQueries
{
    public static IReadOnlyList<BackgroundOperation> ForSearch(DbConnection conn, string prefix, string filter,
        Func<DbDataReader, BackgroundOperation> read)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Operations WHERE UrlPrefix = @p AND Filter = @f ORDER BY UpdatedAt DESC";
        Add(cmd, "@p", prefix);
        Add(cmd, "@f", filter);
        return ReadAll(cmd, read);
    }

    public static int CountOtherActive(DbConnection conn, string prefix, string filter)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM (SELECT DISTINCT UrlPrefix, Filter FROM Operations
            WHERE State IN ({OperationStates.ActiveSqlList}) AND NOT (UrlPrefix = @p AND Filter = @f)) t
            """;
        Add(cmd, "@p", prefix);
        Add(cmd, "@f", filter);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public static OperationSearchPage ListSearches(DbConnection conn, OperationListQuery query)
    {
        var where = new StringBuilder("1 = 1");
        void In<T>(DbCommand cmd, string column, string name, IReadOnlyList<T>? values) where T : Enum
        {
            if (values is null) return;
            if (values.Count == 0) { where.Append(" AND 1 = 0"); return; }
            where.Append($" AND {column} IN (");
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0) where.Append(", ");
                where.Append($"@{name}{i}");
                Add(cmd, $"@{name}{i}", Convert.ToInt32(values[i]));
            }
            where.Append(')');
        }

        using var count = conn.CreateCommand();
        In(count, "Kind", "k", query.Kinds);
        In(count, "State", "s", query.States);
		if (!string.IsNullOrWhiteSpace(query.Search))
		{
			where.Append(" AND (LOWER(UrlPrefix) LIKE @q ESCAPE '\\' OR LOWER(Filter) LIKE @q ESCAPE '\\')");
			Add(count, "@q", "%" + EscapeLike(query.Search.Trim().ToLowerInvariant()) + "%");
		}
        var condition = where.ToString();
        count.CommandText = $"SELECT COUNT(*) FROM (SELECT UrlPrefix, Filter FROM Operations WHERE {condition} GROUP BY UrlPrefix, Filter) t";
        var total = Convert.ToInt32(count.ExecuteScalar());

        using var page = conn.CreateCommand();
        foreach (DbParameter p in count.Parameters) Add(page, p.ParameterName, p.Value!);
        page.CommandText = $"""
            SELECT UrlPrefix, Filter, MAX(UpdatedAt) AS LastUpdate FROM Operations WHERE {condition}
            GROUP BY UrlPrefix, Filter ORDER BY {OrderBy(query)} LIMIT @limit OFFSET @offset
            """;
        Add(page, "@limit", Math.Max(0, query.Limit));
        Add(page, "@offset", Math.Max(0, query.Offset));
        var result = new List<SearchKey>();
        using var reader = page.ExecuteReader();
        while (reader.Read()) result.Add(new SearchKey(reader.GetString(0), reader.GetString(1)));
        return new OperationSearchPage(result, total);
    }

    private static string OrderBy(OperationListQuery query)
	{
		var dir = query.Descending ? "DESC" : "ASC";
		return query.Sort switch
		{
			OperationSort.Url => $"UrlPrefix {dir}, Filter {dir}",
			OperationSort.State => $"MIN({OperationStates.SortRankSql}) {(query.Descending ? "ASC" : "DESC")}, UrlPrefix, Filter",
			_ => $"LastUpdate {dir}, UrlPrefix, Filter"
		};
	}

	internal static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

	/// Callers pass one page of rows (bounded by the page size).
    public static IReadOnlyList<BackgroundOperation> ForSearches(DbConnection conn, IReadOnlyCollection<SearchKey> searches,
        Func<DbDataReader, BackgroundOperation> read)
    {
        if (searches.Count == 0) return [];
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder("SELECT * FROM Operations WHERE ");
        var i = 0;
        foreach (var s in searches)
        {
            if (i > 0) sql.Append(" OR ");
            sql.Append($"(UrlPrefix = @p{i} AND Filter = @f{i})");
            Add(cmd, $"@p{i}", s.Prefix);
            Add(cmd, $"@f{i}", s.Filter);
            i++;
        }
        cmd.CommandText = sql.ToString();
        return ReadAll(cmd, read);
    }

    private static List<BackgroundOperation> ReadAll(DbCommand cmd, Func<DbDataReader, BackgroundOperation> read)
    {
        using var reader = cmd.ExecuteReader();
        var result = new List<BackgroundOperation>();
        while (reader.Read()) result.Add(read(reader));
        return result;
    }

    private static void Add(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
