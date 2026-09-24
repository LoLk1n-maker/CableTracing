using Microsoft.Data.Sqlite;

namespace CableRouting;

/// <summary>
/// Работа с БД SQLite. Структура таблиц соответствует разделу 3.3 пояснительной записки.
/// Один файл БД = один проект.
/// </summary>
public static class Database
{
    private const string Schema = @"
PRAGMA foreign_keys = ON;

CREATE TABLE проект (
    код_проекта               TEXT PRIMARY KEY,
    название_проекта          TEXT NOT NULL,
    дата_создания             TEXT,
    дата_последнего_изменения TEXT
);

CREATE TABLE узел_маршрутной_сети (
    код_узла    TEXT PRIMARY KEY,
    код_проекта TEXT NOT NULL REFERENCES проект(код_проекта),
    имя_узла    TEXT NOT NULL,
    x REAL NOT NULL,
    y REAL NOT NULL,
    z REAL NOT NULL
);

CREATE TABLE ребро_маршрутной_сети (
    код_узла_i  TEXT NOT NULL REFERENCES узел_маршрутной_сети(код_узла),
    код_узла_j  TEXT NOT NULL REFERENCES узел_маршрутной_сети(код_узла),
    код_проекта TEXT NOT NULL REFERENCES проект(код_проекта),
    геометрическая_длина REAL NOT NULL,
    PRIMARY KEY (код_узла_i, код_узла_j)
);

CREATE TABLE запрещённая_зона (
    код_зоны    TEXT PRIMARY KEY,
    код_проекта TEXT NOT NULL REFERENCES проект(код_проекта),
    название    TEXT,
    x_min REAL, x_max REAL,
    y_min REAL, y_max REAL,
    z_min REAL, z_max REAL
);

CREATE TABLE блок (
    код_блока          TEXT PRIMARY KEY,
    наименование_блока TEXT NOT NULL
);

CREATE TABLE блок_в_проекте (
    код_блока_в_проекте     TEXT PRIMARY KEY,
    код_проекта             TEXT NOT NULL REFERENCES проект(код_проекта),
    код_блока               TEXT NOT NULL REFERENCES блок(код_блока),
    код_узла_сети           TEXT NOT NULL REFERENCES узел_маршрутной_сети(код_узла),
    позиционное_обозначение TEXT NOT NULL,
    расстояние_подводки     REAL NOT NULL
);

CREATE TABLE тип_кабеля (
    код_типа          TEXT PRIMARY KEY,
    наименование_типа TEXT NOT NULL
);

CREATE TABLE требования_эмс (
    код_проекта  TEXT NOT NULL REFERENCES проект(код_проекта),
    код_типа_α   TEXT NOT NULL REFERENCES тип_кабеля(код_типа),
    код_типа_β   TEXT NOT NULL REFERENCES тип_кабеля(код_типа),
    минимально_допустимое_расстояние REAL NOT NULL,
    PRIMARY KEY (код_проекта, код_типа_α, код_типа_β)
);

CREATE TABLE требуемое_соединение (
    код_соединения       TEXT PRIMARY KEY,
    код_проекта          TEXT NOT NULL REFERENCES проект(код_проекта),
    код_блока_источника  TEXT NOT NULL REFERENCES блок_в_проекте(код_блока_в_проекте),
    код_блока_получателя TEXT NOT NULL REFERENCES блок_в_проекте(код_блока_в_проекте),
    код_типа_кабеля      TEXT NOT NULL REFERENCES тип_кабеля(код_типа),
    порядок_прокладки    INTEGER NOT NULL
);

CREATE TABLE кабель (
    код_соединения      TEXT PRIMARY KEY REFERENCES требуемое_соединение(код_соединения),
    полная_длина_кабеля REAL NOT NULL
);

CREATE TABLE сегмент_маршрута (
    код_соединения   TEXT NOT NULL REFERENCES кабель(код_соединения),
    порядковый_номер INTEGER NOT NULL,
    код_узла_i       TEXT NOT NULL REFERENCES узел_маршрутной_сети(код_узла),
    код_узла_j       TEXT NOT NULL REFERENCES узел_маршрутной_сети(код_узла),
    PRIMARY KEY (код_соединения, порядковый_номер)
);
";

    private static SqliteConnection Open(string file)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params object[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("@p" + i, args[i] ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сохранить проект (и результаты трассировки, если есть) в новый файл БД.</summary>
    public static void Save(string file, Project p, RoutingResult result)
    {
        if (File.Exists(file)) File.Delete(file);

        using var c = Open(file);
        using (var cmd = c.CreateCommand()) { cmd.CommandText = Schema; cmd.ExecuteNonQuery(); }

        using var tx = c.BeginTransaction();
        string now = DateTime.Now.ToString("dd.MM.yyyy");
        Exec(c, tx, "INSERT INTO проект VALUES (@p0,@p1,@p2,@p3)", p.Code, p.Name, now, now);

        foreach (var n in p.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Name)))
            Exec(c, tx, "INSERT INTO узел_маршрутной_сети VALUES (@p0,@p1,@p2,@p3,@p4,@p5)",
                n.Name, p.Code, n.Name, n.X, n.Y, n.Z);

        var nodes = p.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Name)).ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>();
        foreach (var e in p.Edges)
        {
            if (!nodes.TryGetValue(e.A ?? "", out var a) || !nodes.TryGetValue(e.B ?? "", out var b)) continue;
            if (!seen.Add(a.Name + "|" + b.Name) || !seen.Add(b.Name + "|" + a.Name)) continue;
            double d = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
            Exec(c, tx, "INSERT INTO ребро_маршрутной_сети VALUES (@p0,@p1,@p2,@p3)", a.Name, b.Name, p.Code, Math.Round(d, 3));
        }

        int zi = 0;
        foreach (var z in p.Zones)
        {
            zi++;
            var code = string.IsNullOrWhiteSpace(z.Code) ? $"ZZ{zi}" : z.Code;
            Exec(c, tx, "INSERT INTO запрещённая_зона VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)",
                code, p.Code, z.Name, z.XMin, z.XMax, z.YMin, z.YMax, z.ZMin, z.ZMax);
        }

        // Справочник «блок»: одна запись на одно наименование модели
        var catalog = new Dictionary<string, string>();
        foreach (var b in p.Blocks.Where(b => !string.IsNullOrWhiteSpace(b.Designation)))
        {
            var title = string.IsNullOrWhiteSpace(b.Title) ? b.Designation : b.Title;
            if (!catalog.TryGetValue(title, out var bc))
            {
                bc = "B" + (catalog.Count + 1).ToString("000");
                catalog[title] = bc;
                Exec(c, tx, "INSERT INTO блок VALUES (@p0,@p1)", bc, title);
            }
            Exec(c, tx, "INSERT INTO блок_в_проекте VALUES (@p0,@p1,@p2,@p3,@p4,@p5)",
                b.Designation, p.Code, bc,
                nodes.TryGetValue(b.NodeName ?? "", out var bn) ? bn.Name : b.NodeName,
                b.Designation, b.Lead);
        }

        var typeCode = new Dictionary<string, string>();
        for (int i = 0; i < p.CableTypes.Count; i++)
        {
            typeCode[p.CableTypes[i]] = "T" + (i + 1);
            Exec(c, tx, "INSERT INTO тип_кабеля VALUES (@p0,@p1)", "T" + (i + 1), p.CableTypes[i]);
        }
        foreach (var a in p.CableTypes)
            foreach (var b in p.CableTypes)
                Exec(c, tx, "INSERT INTO требования_эмс VALUES (@p0,@p1,@p2,@p3)", p.Code, typeCode[a], typeCode[b], p.GetDmin(a, b));

        int order = 0;
        foreach (var cn in p.Connections.Where(x => !string.IsNullOrWhiteSpace(x.Code)))
        {
            order++;
            typeCode.TryGetValue(cn.CableType ?? "", out var tc);
            Exec(c, tx, "INSERT INTO требуемое_соединение VALUES (@p0,@p1,@p2,@p3,@p4,@p5)",
                cn.Code, p.Code, cn.From, cn.To, tc, order);
        }

        if (result != null)
        {
            foreach (var cr in result.Cables.Where(x => x.Ok))
            {
                Exec(c, tx, "INSERT INTO кабель VALUES (@p0,@p1)", cr.Code, Math.Round(cr.Length, 3));
                for (int i = 0; i + 1 < cr.Path.Count; i++)
                    Exec(c, tx, "INSERT INTO сегмент_маршрута VALUES (@p0,@p1,@p2,@p3)", cr.Code, i + 1, cr.Path[i], cr.Path[i + 1]);
            }
        }

        tx.Commit();
    }

    /// <summary>Загрузить проект из файла БД.</summary>
    public static Project Load(string file)
    {
        using var c = Open(file);
        var p = new Project();

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT код_проекта, название_проекта FROM проект LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidDataException("В базе данных нет ни одного проекта.");
            p.Code = r.GetString(0);
            p.Name = r.GetString(1);
        }

        SqliteDataReader Q(string sql)
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@prj", p.Code);
            return cmd.ExecuteReader();
        }

        var nodeName = new Dictionary<string, string>();
        using (var r = Q("SELECT код_узла, имя_узла, x, y, z FROM узел_маршрутной_сети WHERE код_проекта=@prj ORDER BY rowid"))
            while (r.Read())
            {
                nodeName[r.GetString(0)] = r.GetString(1);
                p.Nodes.Add(new NodeItem { Name = r.GetString(1), X = r.GetDouble(2), Y = r.GetDouble(3), Z = r.GetDouble(4) });
            }
        string N(string code) => nodeName.TryGetValue(code, out var n) ? n : code;

        using (var r = Q("SELECT код_узла_i, код_узла_j FROM ребро_маршрутной_сети WHERE код_проекта=@prj ORDER BY rowid"))
            while (r.Read())
                p.Edges.Add(new EdgeItem { A = N(r.GetString(0)), B = N(r.GetString(1)) });

        using (var r = Q("SELECT код_зоны, название, x_min, x_max, y_min, y_max, z_min, z_max FROM запрещённая_зона WHERE код_проекта=@prj ORDER BY rowid"))
            while (r.Read())
                p.Zones.Add(new ZoneItem
                {
                    Code = r.GetString(0), Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    XMin = r.GetDouble(2), XMax = r.GetDouble(3), YMin = r.GetDouble(4),
                    YMax = r.GetDouble(5), ZMin = r.GetDouble(6), ZMax = r.GetDouble(7)
                });

        using (var r = Q(@"SELECT bp.позиционное_обозначение, b.наименование_блока, bp.код_узла_сети, bp.расстояние_подводки
                           FROM блок_в_проекте bp JOIN блок b ON b.код_блока = bp.код_блока
                           WHERE bp.код_проекта=@prj ORDER BY bp.rowid"))
            while (r.Read())
                p.Blocks.Add(new BlockItem { Designation = r.GetString(0), Title = r.GetString(1), NodeName = N(r.GetString(2)), Lead = r.GetDouble(3) });

        var typeName = new Dictionary<string, string>();
        p.CableTypes.Clear();
        using (var r = Q("SELECT код_типа, наименование_типа FROM тип_кабеля ORDER BY rowid"))
            while (r.Read())
            {
                typeName[r.GetString(0)] = r.GetString(1);
                p.CableTypes.Add(r.GetString(1));
            }

        using (var r = Q("SELECT код_типа_α, код_типа_β, минимально_допустимое_расстояние FROM требования_эмс WHERE код_проекта=@prj"))
            while (r.Read())
                if (typeName.TryGetValue(r.GetString(0), out var a) && typeName.TryGetValue(r.GetString(1), out var b))
                    p.Emc[a + "|" + b] = r.GetDouble(2);

        using (var r = Q(@"SELECT код_соединения, код_блока_источника, код_блока_получателя, код_типа_кабеля
                           FROM требуемое_соединение WHERE код_проекта=@prj ORDER BY порядок_прокладки"))
            while (r.Read())
                p.Connections.Add(new ConnectionItem
                {
                    Code = r.GetString(0), From = r.GetString(1), To = r.GetString(2),
                    CableType = !r.IsDBNull(3) && typeName.TryGetValue(r.GetString(3), out var tn) ? tn : ""
                });

        return p;
    }
}
