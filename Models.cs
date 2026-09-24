using System.ComponentModel;

namespace CableRouting;

/// <summary>Узел маршрутной сети (вершина графа G).</summary>
public class NodeItem
{
    [DisplayName("Имя узла")] public string Name { get; set; } = "";
    [DisplayName("X, м")] public double X { get; set; }
    [DisplayName("Y, м")] public double Y { get; set; }
    [DisplayName("Z, м")] public double Z { get; set; }
}

/// <summary>Ребро маршрутной сети (участок трассы между двумя узлами).</summary>
public class EdgeItem
{
    [DisplayName("Узел i")] public string A { get; set; } = "";
    [DisplayName("Узел j")] public string B { get; set; } = "";
}

/// <summary>Запрещённая зона – прямоугольный параллелепипед.</summary>
public class ZoneItem
{
    [DisplayName("Код зоны")] public string Code { get; set; } = "";
    [DisplayName("Название")] public string Name { get; set; } = "";
    [DisplayName("X min")] public double XMin { get; set; }
    [DisplayName("X max")] public double XMax { get; set; }
    [DisplayName("Y min")] public double YMin { get; set; }
    [DisplayName("Y max")] public double YMax { get; set; }
    [DisplayName("Z min")] public double ZMin { get; set; }
    [DisplayName("Z max")] public double ZMax { get; set; }

    /// <summary>Условие (2.2): узел лежит внутри всех трёх интервалов.</summary>
    public bool Contains(NodeItem n) =>
        XMin <= n.X && n.X <= XMax &&
        YMin <= n.Y && n.Y <= YMax &&
        ZMin <= n.Z && n.Z <= ZMax;
}

/// <summary>Блок оборудования, установленный в проекте.</summary>
public class BlockItem
{
    [DisplayName("Поз. обозначение")] public string Designation { get; set; } = "";
    [DisplayName("Наименование")] public string Title { get; set; } = "";
    [DisplayName("Узел подключения")] public string NodeName { get; set; } = "";
    [DisplayName("Подводка dc, м")] public double Lead { get; set; } = 0.2;
}

/// <summary>Требуемое соединение (строка схемы электрической).</summary>
public class ConnectionItem
{
    [DisplayName("Кабель")] public string Code { get; set; } = "";
    [DisplayName("Блок-источник")] public string From { get; set; } = "";
    [DisplayName("Блок-получатель")] public string To { get; set; } = "";
    [DisplayName("Тип кабеля")] public string CableType { get; set; }
}

/// <summary>Проект трассировки – все исходные данные.</summary>
public class Project
{
    public string Code { get; set; } = "PRJ001";
    public string Name { get; set; } = "Проект";

    public BindingList<NodeItem> Nodes { get; set; } = new();
    public BindingList<EdgeItem> Edges { get; set; } = new();
    public BindingList<ZoneItem> Zones { get; set; } = new();
    public BindingList<BlockItem> Blocks { get; set; } = new();
    public BindingList<ConnectionItem> Connections { get; set; } = new();

    public List<string> CableTypes { get; set; } = new() { "сигнальный", "информационный", "силовой" };

    /// <summary>Нормы ЭМС: Dmin(α, β), ключ "α|β".</summary>
    public Dictionary<string, double> Emc { get; set; } = new();

    public double GetDmin(string a, string b)
    {
        if (Emc.TryGetValue(a + "|" + b, out var v)) return v;
        if (Emc.TryGetValue(b + "|" + a, out v)) return v;
        return a == b ? 0 : 0.1; // неизвестная пара разных типов считается несовместимой
    }

    public void SetDmin(string a, string b, double v)
    {
        Emc[a + "|" + b] = v;
        Emc[b + "|" + a] = v;
    }

    /// <summary>Контрольный пример из раздела 2.4 пояснительной записки.</summary>
    public static Project CreateControlExample()
    {
        var p = new Project { Code = "PRJ001", Name = "Контрольный пример (раздел 2.4)" };

        // Узлы: F/U – пол/потолок, L/R – левый/правый борт, цифра – сечение (x = 0; 2; 4)
        foreach (var (lvl, z) in new[] { ("F", 0.0), ("U", 1.5) })
            foreach (var (side, y) in new[] { ("L", 0.0), ("R", 1.0) })
                for (int i = 0; i < 3; i++)
                    p.Nodes.Add(new NodeItem { Name = $"{lvl}{side}{i}", X = 2.0 * i, Y = y, Z = z });

        // Продольные трассы
        foreach (var t in new[] { "FL", "FR", "UL", "UR" })
        {
            p.Edges.Add(new EdgeItem { A = t + "0", B = t + "1" });
            p.Edges.Add(new EdgeItem { A = t + "1", B = t + "2" });
        }
        // Поперечные участки и стояки
        for (int i = 0; i < 3; i++)
        {
            p.Edges.Add(new EdgeItem { A = $"FL{i}", B = $"FR{i}" });
            p.Edges.Add(new EdgeItem { A = $"UL{i}", B = $"UR{i}" });
            p.Edges.Add(new EdgeItem { A = $"FL{i}", B = $"UL{i}" });
            p.Edges.Add(new EdgeItem { A = $"FR{i}", B = $"UR{i}" });
        }

        p.Zones.Add(new ZoneItem
        {
            Code = "ZZ1", Name = "Топливный бак",
            XMin = 1, XMax = 3, YMin = -0.5, YMax = 0.5, ZMin = -0.5, ZMax = 0.5
        });

        p.Blocks.Add(new BlockItem { Designation = "БСУП", Title = "Блок системы управления полётом", NodeName = "UL0", Lead = 0.2 });
        p.Blocks.Add(new BlockItem { Designation = "ЦПМ", Title = "Цифровой пилотажный модуль", NodeName = "UL2", Lead = 0.2 });
        p.Blocks.Add(new BlockItem { Designation = "БРЭО", Title = "Блок радиоэлектронного оборудования", NodeName = "FL0", Lead = 0.2 });
        p.Blocks.Add(new BlockItem { Designation = "БС", Title = "Блок связи", NodeName = "FL2", Lead = 0.2 });
        p.Blocks.Add(new BlockItem { Designation = "РЩП", Title = "Распределительный щит питания", NodeName = "FR0", Lead = 0.2 });
        p.Blocks.Add(new BlockItem { Designation = "БПН", Title = "Блок преобразования напряжения", NodeName = "FR1", Lead = 0.2 });
        p.Blocks.Add(new BlockItem { Designation = "БНК", Title = "Блок навигационного комплекса", NodeName = "FR2", Lead = 0.2 });

        p.Connections.Add(new ConnectionItem { Code = "K1", From = "БСУП", To = "ЦПМ", CableType = "сигнальный" });
        p.Connections.Add(new ConnectionItem { Code = "K2", From = "БРЭО", To = "БС", CableType = "информационный" });
        p.Connections.Add(new ConnectionItem { Code = "K3", From = "РЩП", To = "БПН", CableType = "силовой" });
        p.Connections.Add(new ConnectionItem { Code = "K4", From = "БРЭО", To = "БНК", CableType = "информационный" });

        p.SetDmin("сигнальный", "сигнальный", 0);
        p.SetDmin("информационный", "информационный", 0);
        p.SetDmin("силовой", "силовой", 0);
        p.SetDmin("сигнальный", "информационный", 0.10);
        p.SetDmin("сигнальный", "силовой", 0.15);
        p.SetDmin("информационный", "силовой", 0.20);

        return p;
    }
}
