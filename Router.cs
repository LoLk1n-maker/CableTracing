using System.Globalization;
using System.Text;

namespace CableRouting;

public class CableResult
{
    public string Code = "";
    public string From = "";
    public string To = "";
    public string Type = "";
    public bool Ok;
    public List<string> Path = new();
    public double Geo;      // сумма dij по маршруту
    public double Length;   // L(k) = Geo + dc1 + dc2   (2.6)
    public string Note = "";

    public string RouteText => Ok ? string.Join(" – ", Path) : "маршрут не найден";
}

public class RoutingResult
{
    public List<CableResult> Cables = new();
    public HashSet<string> ForbiddenNodes = new();
    public double Total;    // F = Σ L(k)   (2.7)
    public string Log = "";
}

// Автоматизированная трассировка БКС модифицированным алгоритмом Дейкстры:
// запрещённые зоны – удаление узлов из графа (2.2–2.4),
// ЭМС – бесконечный вес ребра, занятого несовместимым кабелем (2.5).

public static class Router
{
    private const double Eps = 1e-9;
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string F(double v) => double.IsPositiveInfinity(v) ? "∞" : v.ToString("0.0##", Ru);

    private static string Key(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    private class Graph
    {
        public List<string> Order = new();
        public Dictionary<string, List<(string To, double D)>> Adj = new();
    }

    public static RoutingResult Run(Project p)
    {
        var res = new RoutingResult();
        var log = new StringBuilder();
        var cmp = StringComparer.OrdinalIgnoreCase;

        // ---------- 1. Ввод исходных данных и проверка ----------
        var byName = new Dictionary<string, NodeItem>(cmp);
        foreach (var n in p.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Name)) continue;
            n.Name = n.Name.Trim();
            if (byName.ContainsKey(n.Name))
                throw new InvalidDataException($"Узел «{n.Name}» задан дважды.");
            byName[n.Name] = n;
        }
        if (byName.Count == 0) throw new InvalidDataException("Не задано ни одного узла.");

        string Canon(string name, string where)
        {
            if (name != null && byName.TryGetValue(name.Trim(), out var nd)) return nd.Name;
            throw new InvalidDataException($"{where}: узел «{name}» не найден в таблице узлов.");
        }

        var blocks = new Dictionary<string, BlockItem>(cmp);
        foreach (var b in p.Blocks)
        {
            if (string.IsNullOrWhiteSpace(b.Designation)) continue;
            b.Designation = b.Designation.Trim();
            if (blocks.ContainsKey(b.Designation))
                throw new InvalidDataException($"Блок «{b.Designation}» задан дважды.");
            b.NodeName = Canon(b.NodeName, $"Блок {b.Designation}");
            blocks[b.Designation] = b;
        }

        log.AppendLine($"ПРОЕКТ: {p.Name} ({p.Code})");
        log.AppendLine($"Узлов: {byName.Count}, рёбер: {p.Edges.Count}, запрещённых зон: {p.Zones.Count}, соединений: {p.Connections.Count}");
        log.AppendLine();

        // ---------- 2. Фильтрация запрещённых зон (2.2) ----------
        log.AppendLine("=== ФИЛЬТРАЦИЯ ЗАПРЕЩЁННЫХ ЗОН (2.2)–(2.4) ===");
        foreach (var z in p.Zones)
        {
            var inside = byName.Values.Where(z.Contains).Select(n => n.Name).ToList();
            log.AppendLine($"Зона {z.Code} «{z.Name}»: x∈[{F(z.XMin)}; {F(z.XMax)}], y∈[{F(z.YMin)}; {F(z.YMax)}], z∈[{F(z.ZMin)}; {F(z.ZMax)}] → " +
                           (inside.Count > 0 ? "удаляются узлы: " + string.Join(", ", inside) : "узлов не содержит"));
            foreach (var n in inside) res.ForbiddenNodes.Add(n);
        }

        // ---------- 3. Построение маршрутного графа (2.1) ----------
        var full = new Graph();      // исходный граф (для анализа удлинений)
        var work = new Graph();      // «очищенный» граф
        foreach (var n in byName.Values)
        {
            full.Order.Add(n.Name); full.Adj[n.Name] = new();
            if (!res.ForbiddenNodes.Contains(n.Name)) { work.Order.Add(n.Name); work.Adj[n.Name] = new(); }
        }

        var edgeLen = new Dictionary<string, double>();
        var removed = new List<string>();
        foreach (var e in p.Edges)
        {
            if (string.IsNullOrWhiteSpace(e.A) && string.IsNullOrWhiteSpace(e.B)) continue;
            var a = Canon(e.A, "Ребро"); var b = Canon(e.B, "Ребро");
            if (a == b) continue;
            var k = Key(a, b);
            if (edgeLen.ContainsKey(k)) continue;
            var na = byName[a]; var nb = byName[b];
            double d = Math.Sqrt(Math.Pow(nb.X - na.X, 2) + Math.Pow(nb.Y - na.Y, 2) + Math.Pow(nb.Z - na.Z, 2));
            edgeLen[k] = d;
            full.Adj[a].Add((b, d)); full.Adj[b].Add((a, d));
            if (res.ForbiddenNodes.Contains(a) || res.ForbiddenNodes.Contains(b)) { removed.Add($"{a}–{b}"); continue; }
            work.Adj[a].Add((b, d)); work.Adj[b].Add((a, d));
        }
        log.AppendLine(removed.Count > 0 ? "Удалены рёбра: " + string.Join(", ", removed) : "Рёбра не удалялись.");
        int workEdges = work.Adj.Values.Sum(l => l.Count) / 2;
        log.AppendLine($"Рабочий граф: {work.Order.Count} узлов, {workEdges} рёбер.");
        log.AppendLine();

        // ---------- 4. Последовательная трассировка ----------
        var occupancy = new Dictionary<string, List<(string Code, string Type)>>();

        foreach (var c in p.Connections)
        {
            if (string.IsNullOrWhiteSpace(c.Code) && string.IsNullOrWhiteSpace(c.From)) continue;
            var cr = new CableResult { Code = c.Code?.Trim() ?? "", From = c.From?.Trim() ?? "", To = c.To?.Trim() ?? "", Type = c.CableType?.Trim() ?? "" };
            res.Cables.Add(cr);

            log.AppendLine($"=== КАБЕЛЬ {cr.Code} ({cr.Type}): {cr.From} → {cr.To} ===");

            if (!blocks.TryGetValue(cr.From, out var src) || !blocks.TryGetValue(cr.To, out var dst))
            {
                cr.Note = "ошибка: блок не найден";
                log.AppendLine("Ошибка: блок-источник или блок-получатель не найден в таблице блоков.\n");
                continue;
            }
            if (!p.CableTypes.Contains(cr.Type))
            {
                cr.Note = "ошибка: неизвестный тип кабеля";
                log.AppendLine($"Ошибка: тип «{cr.Type}» отсутствует в нормах ЭМС.\n");
                continue;
            }
            string s = src.NodeName, t = dst.NodeName;
            log.AppendLine($"Исток: {s}, приёмник: {t}; подводки dc = {F(src.Lead)} + {F(dst.Lead)} м");

            if (res.ForbiddenNodes.Contains(s) || res.ForbiddenNodes.Contains(t))
            {
                cr.Note = "ошибка: блок в запрещённой зоне";
                log.AppendLine("Ошибка: узел подключения блока находится в запрещённой зоне.\n");
                continue;
            }

            // Пересчёт весов по правилу (2.5) – вывод занятых рёбер
            string type = cr.Type;
            double Weight(string u, string v, double d)
            {
                if (occupancy.TryGetValue(Key(u, v), out var occ) && occ.Any(o => p.GetDmin(type, o.Type) > Eps))
                    return double.PositiveInfinity;
                return d;
            }

            if (occupancy.Count > 0)
            {
                log.AppendLine("Пересчёт весов занятых рёбер (2.5):");
                foreach (var kv in occupancy)
                {
                    var parts = kv.Key.Split('|');
                    double w = Weight(parts[0], parts[1], edgeLen[kv.Key]);
                    var who = string.Join(", ", kv.Value.Select(o => $"{o.Code} ({o.Type}, Dmin={F(p.GetDmin(type, o.Type))})"));
                    log.AppendLine($"   {parts[0]}–{parts[1],-5} d={F(edgeLen[kv.Key]),-5} занято: {who} → w = {F(w)}");
                }
            }
            else log.AppendLine("Сеть пуста – веса равны геометрическим длинам.");

            log.AppendLine("Трасса алгоритма Дейкстры:");
            log.AppendLine("   Шаг  Зафиксирован       dist    Релаксация соседей");
            var (path, dist) = Dijkstra(work, s, t, Weight, log);

            if (path == null)
            {
                cr.Note = "маршрут не найден";
                log.AppendLine("Приёмник недостижим – допустимого маршрута нет.\n");
                continue;
            }

            cr.Ok = true;
            cr.Path = path;
            cr.Geo = dist;
            cr.Length = dist + src.Lead + dst.Lead;

            // Классификация результата (для примечания в журнале)
            var shared = new HashSet<string>();
            for (int i = 0; i + 1 < path.Count; i++)
                if (occupancy.TryGetValue(Key(path[i], path[i + 1]), out var occ))
                    foreach (var o in occ) shared.Add(o.Code);

            double pure = Dijkstra(full, s, t, (u, v, d) => d, null).Dist;
            double zonesOnly = Dijkstra(work, s, t, (u, v, d) => d, null).Dist;
            var notes = new List<string>();
            if (zonesOnly > pure + Eps) notes.Add($"обход запрещённой зоны (+{F(zonesOnly - pure)} м)");
            if (dist > zonesOnly + Eps) notes.Add($"обход по ЭМС (+{F(dist - zonesOnly)} м)");
            if (shared.Count > 0) notes.Add("совместная прокладка с " + string.Join(", ", shared));
            cr.Note = notes.Count > 0 ? string.Join("; ", notes) : "обычная трассировка";

            // Пометка рёбер маршрута типом кабеля
            for (int i = 0; i + 1 < path.Count; i++)
            {
                var k = Key(path[i], path[i + 1]);
                if (!occupancy.TryGetValue(k, out var list)) occupancy[k] = list = new();
                list.Add((cr.Code, cr.Type));
            }

            log.AppendLine($"Маршрут: {cr.RouteText}");
            log.AppendLine($"L({cr.Code}) = {F(dist)} + {F(src.Lead)} + {F(dst.Lead)} = {F(cr.Length)} м   [{cr.Note}]");
            log.AppendLine();
        }

        // ---------- 5. Формирование результатов ----------
        res.Total = res.Cables.Where(c => c.Ok).Sum(c => c.Length);
        log.AppendLine("=== ИТОГ ===");
        log.AppendLine("F = " + string.Join(" + ", res.Cables.Where(c => c.Ok).Select(c => F(c.Length))) + $" = {F(res.Total)} м");
        int failed = res.Cables.Count(c => !c.Ok);
        if (failed > 0) log.AppendLine($"Не проложено кабелей: {failed}");
        res.Log = log.ToString();
        return res;
    }

    /// <summary>Классический алгоритм Дейкстры (выбор минимума перебором – как в таблицах 6, 7, Б).</summary>
    private static (List<string> Path, double Dist) Dijkstra(
        Graph g, string s, string t, Func<string, string, double, double> weight, StringBuilder log)
    {
        var dist = g.Order.ToDictionary(v => v, v => double.PositiveInfinity);
        var prev = new Dictionary<string, string>();
        var done = new HashSet<string>();
        dist[s] = 0;
        int step = 0;

        while (true)
        {
            string u = null; double best = double.PositiveInfinity;
            foreach (var v in g.Order)
                if (!done.Contains(v) && dist[v] < best) { best = dist[v]; u = v; }
            if (u == null) break;                 // все достижимые вершины обработаны

            done.Add(u); step++;
            string label = u + (u == s ? " (исток)" : u == t ? " (приёмник)" : "");

            if (u == t)
            {
                log?.AppendLine($"   {step,3}  {label,-17} {F(dist[u]),6}   цель достигнута, останов");
                break;
            }

            var rel = new List<string>();
            foreach (var (to, d) in g.Adj[u])
            {
                if (done.Contains(to)) continue;
                double w = weight(u, to, d);
                if (double.IsPositiveInfinity(w)) { rel.Add($"{to}: блок. (w=∞)"); continue; }
                double nd = dist[u] + w;                         // релаксация (2.10)
                if (nd < dist[to] - Eps) { dist[to] = nd; prev[to] = u; rel.Add($"{to}: {F(nd)}"); }
            }
            log?.AppendLine($"   {step,3}  {label,-17} {F(dist[u]),6}   {(rel.Count > 0 ? string.Join("; ", rel) : "— (нет улучшений)")}");
        }

        if (double.IsPositiveInfinity(dist[t])) return (null, double.PositiveInfinity);

        var path = new List<string> { t };
        while (path[^1] != s) path.Add(prev[path[^1]]);
        path.Reverse();
        return (path, dist[t]);
    }
}
