namespace NexMesh.Core.AutoApi;

using System.Collections.Concurrent;

/// <summary>逻辑路由(可带 {param} 模板段):10M+ 逻辑路由托管于内存表,物理路由数恒定。</summary>
public sealed record LogicalRoute(string Verb, string Route, AutoApiEndpoint Endpoint)
{
    internal string[] Segments { get; } = Route.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    internal bool HasParameters { get; } = Route.Contains('{');
}

/// <summary>
/// 虚拟路由表:精确字典(无参数路由,O(1))+ 参数化模板列表({id} 等模板段匹配,段数约束扫描)。
/// 10M+ 场景下绝大多数为字面量路由(默认方法路由),模板路由是少数——两层结构兼顾吞吐与 REST 语义。
/// 后续可平滑升级 Radix/FrozenDictionary,接口不变。
/// </summary>
public sealed class LogicalRouteTable
{
    private readonly ConcurrentDictionary<string, LogicalRoute> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<LogicalRoute> _parameterized = [];

    public int Count => _exact.Count + _parameterized.Count;

    public void Register(LogicalRoute route)
    {
        if (route.HasParameters)
        {
            _parameterized.Add(route);
        }
        else
        {
            _exact[Normalize(route.Route)] = route;
        }
    }

    /// <summary>按路径+动词匹配;命中返回 true 并输出捕获的路由参数(如 {id}=42)。</summary>
    public bool TryMatch(string path, string verb, out LogicalRoute route, out IReadOnlyDictionary<string, string> routeValues)
    {
        route = null!;
        routeValues = new Dictionary<string, string>();

        var normalized = Normalize(path);
        if (_exact.TryGetValue(normalized, out var exact))
        {
            if (MatchesVerb(exact, verb))
            {
                route = exact;
                return true;
            }
            return false; // 路径存在但动词不符 → 405 语义由调用方按 TryMatchPath 判定
        }

        var segments = normalized.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var candidate in _parameterized)
        {
            if (candidate.Segments.Length != segments.Length || !MatchesVerb(candidate, verb))
            {
                continue;
            }
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var matched = true;
            for (var i = 0; i < segments.Length; i++)
            {
                var template = candidate.Segments[i];
                if (template.StartsWith('{') && template.EndsWith('}'))
                {
                    values[template[1..^1]] = Uri.UnescapeDataString(segments[i]);
                }
                else if (!string.Equals(template, segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                route = candidate;
                routeValues = values;
                return true;
            }
        }
        return false;
    }

    /// <summary>路径存在与否(任意动词)——用于区分 404 与 405。</summary>
    public bool ContainsPath(string path, string verb)
    {
        var normalized = Normalize(path);
        if (_exact.ContainsKey(normalized))
        {
            return true;
        }
        var segments = normalized.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return _parameterized.Any(candidate =>
            candidate.Segments.Length == segments.Length
            && Enumerable.Range(0, segments.Length).All(i =>
                candidate.Segments[i].StartsWith('{') && candidate.Segments[i].EndsWith('}')
                || string.Equals(candidate.Segments[i], segments[i], StringComparison.OrdinalIgnoreCase)));
    }

    private static bool MatchesVerb(LogicalRoute route, string verb) =>
        string.Equals(route.Verb, verb, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        path.StartsWith('/') ? path : "/" + path.Trim('/');
}
