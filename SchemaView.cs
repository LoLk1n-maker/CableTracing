using System.Drawing.Drawing2D;
using System.Globalization;

namespace CableRouting;

/// <summary>Визуализация маршрутной сети, запрещённых зон и проложенных кабелей.</summary>
public class SchemaView : Control
{
    public enum ViewMode { Oblique, TopXY, SideXZ, FrontYZ }

    private Project _project;
    public Project Project
    {
        get => _project;
        set { _project = value; SetPreset(ViewMode.Oblique); }   // новый проект — вид по умолчанию
    }
    public RoutingResult Result { get; set; }

    // --- Камера: вращение вокруг центра всех узлов ---
    private double _yaw = 30;    // поворот вокруг вертикальной оси Z, градусы
    private double _pitch = 25;   // наклон (0 — вид сбоку, 90 — вид сверху), градусы
    private float _zoom = 1f;     // 1 — всё помещается в окно
    private float _baseScale;     // px на метр при zoom = 1 (подбирается при сбросе вида)
    private Size _baseSize;       // размер окна, для которого подобран _baseScale
    private float _baseLegendH;   // высота легенды, для которой подобран _baseScale
    private double _cx, _cy, _cz; // центр вращения
    private bool _dragging;
    private Point _lastMouse;
    public bool ShowLengths { get; set; } = true;
    public bool ShowBlockNames { get; set; } = true;
    public bool ShowZoneNames { get; set; } = true;
    public bool ShowNodeNames { get; set; } = true;
    public bool ShowLegend { get; set; } = true;

    public static readonly Color[] Palette =
    {
        Color.FromArgb(31, 119, 180), Color.FromArgb(44, 160, 44), Color.FromArgb(255, 127, 14),
        Color.FromArgb(148, 103, 189), Color.FromArgb(23, 190, 207), Color.FromArgb(227, 119, 194),
        Color.FromArgb(140, 86, 75), Color.FromArgb(188, 189, 34), Color.FromArgb(127, 127, 127),
        Color.FromArgb(0, 70, 140), Color.FromArgb(0, 120, 60), Color.FromArgb(200, 80, 0),
        Color.FromArgb(100, 40, 160), Color.FromArgb(0, 150, 150), Color.FromArgb(180, 30, 120),
        Color.FromArgb(90, 60, 30), Color.FromArgb(130, 140, 0), Color.FromArgb(60, 60, 60),
        Color.FromArgb(90, 160, 230), Color.FromArgb(120, 200, 90), Color.FromArgb(240, 170, 60),
        Color.FromArgb(190, 150, 230), Color.FromArgb(80, 210, 200), Color.FromArgb(240, 150, 200)
    };

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public SchemaView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
    }

    /// <summary>Установить стандартный вид (из выпадающего списка) и сбросить масштаб.</summary>
    public void SetPreset(ViewMode mode)
    {
        (_yaw, _pitch) = mode switch
        {
            ViewMode.TopXY => (0.0, 90.0),
            ViewMode.SideXZ => (0.0, 0.0),
            ViewMode.FrontYZ => (-90.0, 0.0),
            _ => (30.0, 25.0),
        };
        _zoom = 1f;
        _baseScale = 0;   // пересчитать «вписывание» при следующей отрисовке
        Invalidate();
    }

    /// <summary>Ортогональная проекция точки после поворота вокруг центра (результат — метры на экране, Y вверх).</summary>
    private PointF Raw(double x, double y, double z)
    {
        double dx = x - _cx, dy = y - _cy, dz = z - _cz;
        double ya = _yaw * Math.PI / 180, pa = _pitch * Math.PI / 180;
        // поворот вокруг вертикальной оси
        double x1 = dx * Math.Cos(ya) - dy * Math.Sin(ya);
        double y1 = dx * Math.Sin(ya) + dy * Math.Cos(ya);   // глубина
        // наклон камеры
        double up = dz * Math.Cos(pa) + y1 * Math.Sin(pa);
        return new PointF((float)x1, (float)up);
    }

    // --- Мышь: ЛКМ — вращать, колесо — масштаб, двойной клик — сброс ---
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Left) { _dragging = true; _lastMouse = e.Location; Cursor = Cursors.SizeAll; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _yaw -= (e.X - _lastMouse.X) * 0.5;
        _pitch = Math.Clamp(_pitch + (e.Y - _lastMouse.Y) * 0.5, -90, 90);
        _lastMouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15f : 1 / 1.15f), 0.2f, 25f);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        SetPreset(ViewMode.Oblique);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (Project == null) return;
        var nodes = Project.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.Name))
                                 .GroupBy(n => n.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                                 .ToDictionary(gr => gr.Key, gr => gr.First(), StringComparer.OrdinalIgnoreCase);
        if (nodes.Count == 0)
        {
            g.DrawString("Нет узлов для отображения", Font, Brushes.Gray, 20, 20);
            return;
        }

        // --- Центр вращения = среднее всех узлов ---
        _cx = nodes.Values.Average(n => n.X);
        _cy = nodes.Values.Average(n => n.Y);
        _cz = nodes.Values.Average(n => n.Z);

        const float margin = 50;
        float legendH = 26 + (ShowLegend ? 18 * (Result?.Cables.Count ?? 0) : 0);
        float w = Math.Max(ClientSize.Width - 2 * margin, 50);
        float h = Math.Max(ClientSize.Height - 2 * margin - legendH, 50);

        // «Вписывание» подбирается один раз (при сбросе вида или изменении окна),
        // чтобы при вращении модель не прыгала по размеру
        if (_baseScale <= 0 || _baseSize != ClientSize || _baseLegendH != legendH)
        {
            var pts = nodes.Values.Select(n => Raw(n.X, n.Y, n.Z)).ToList();
            float halfX = Math.Max(pts.Max(p => Math.Abs(p.X)), 0.01f);
            float halfY = Math.Max(pts.Max(p => Math.Abs(p.Y)), 0.01f);
            _baseScale = Math.Min(w / (2 * halfX), h / (2 * halfY));
            _baseSize = ClientSize;
            _baseLegendH = legendH;
        }
        float scale = _baseScale * _zoom;
        float ox = margin + w / 2;                 // экранная точка центра модели
        float oy = margin + legendH + h / 2;

        PointF P(double x, double y, double z)
        {
            var r = Raw(x, y, z);
            return new PointF(ox + r.X * scale, oy - r.Y * scale);
        }
        PointF PN(NodeItem n) => P(n.X, n.Y, n.Z);

        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in Project.Zones)
            foreach (var n in nodes.Values)
                if (z.Contains(n)) forbidden.Add(n.Name.Trim());

        using var small = new Font("Segoe UI", 7.5f);
        using var normal = new Font("Segoe UI", 9f);
        using var bold = new Font("Segoe UI", 9f, FontStyle.Bold);

        // --- Запрещённые зоны ---
        foreach (var z in Project.Zones)
        {
            // Ограничиваем бесконечно большие зоны областью узлов
            var c = new PointF[8];
            int i = 0;
            foreach (var x in new[] { z.XMin, z.XMax })
                foreach (var y in new[] { z.YMin, z.YMax })
                    foreach (var zz in new[] { z.ZMin, z.ZMax })
                        c[i++] = P(x, y, zz);
            int[][] faces =
            {
                new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 },
                new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 }
            };
            using var fill = new SolidBrush(Color.FromArgb(22, 220, 40, 40));
            using var pen = new Pen(Color.FromArgb(170, 210, 40, 40), 1.2f) { DashStyle = DashStyle.Dash };
            foreach (var f in faces)
            {
                var poly = f.Select(k => c[k]).ToArray();
                g.FillPolygon(fill, poly);
                g.DrawPolygon(pen, poly);
            }
            var top = c.OrderBy(p => p.Y).First();
            if (ShowZoneNames)
                g.DrawString($"{z.Code} {z.Name}", small, Brushes.Firebrick, top.X + 3, top.Y - 15);
        }

        // --- Рёбра ---
        using var edgePen = new Pen(Color.FromArgb(150, 150, 150), 1.5f);
        using var removedPen = new Pen(Color.FromArgb(200, 235, 130, 130), 1.2f) { DashStyle = DashStyle.Dot };
        foreach (var e2 in Project.Edges)
        {
            if (e2.A == null || e2.B == null) continue;
            if (!nodes.TryGetValue(e2.A.Trim(), out var a) || !nodes.TryGetValue(e2.B.Trim(), out var b)) continue;
            bool rem = forbidden.Contains(a.Name.Trim()) || forbidden.Contains(b.Name.Trim());
            var pa = PN(a); var pb = PN(b);
            g.DrawLine(rem ? removedPen : edgePen, pa, pb);
            if (ShowLengths && !rem)
            {
                double d = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow(b.Z - a.Z, 2));
                var mid = new PointF((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
                g.DrawString(d.ToString("0.##", Ru), small, Brushes.DimGray, mid.X + 2, mid.Y - 2);
            }
        }

        // --- Маршруты кабелей ---
        // Сдвиг вбок считается ОТДЕЛЬНО для каждого ребра: только среди кабелей,
        // которые реально идут по этому ребру. Один кабель на ребре — рисуется точно по ребру.
        if (Result != null)
        {
            string EdgeKey(string u, string v) => string.CompareOrdinal(u, v) <= 0 ? u + "|" + v : v + "|" + u;

            var onEdge = new Dictionary<string, List<int>>();   // ребро -> номера кабелей на нём
            for (int ci = 0; ci < Result.Cables.Count; ci++)
            {
                var cr = Result.Cables[ci];
                if (!cr.Ok) continue;
                for (int i = 0; i + 1 < cr.Path.Count; i++)
                {
                    var k = EdgeKey(cr.Path[i], cr.Path[i + 1]);
                    if (!onEdge.TryGetValue(k, out var list)) onEdge[k] = list = new List<int>();
                    if (!list.Contains(ci)) list.Add(ci);
                }
            }

            const float gap = 4f;   // расстояние между соседними кабелями на одном ребре, px
            for (int ci = 0; ci < Result.Cables.Count; ci++)
            {
                var cr = Result.Cables[ci];
                if (!cr.Ok) continue;
                using var pen = new Pen(Palette[ci % Palette.Length], 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                for (int i = 0; i + 1 < cr.Path.Count; i++)
                {
                    // Ребро всегда берём в одном направлении (по алфавиту),
                    // чтобы сторона сдвига не зависела от направления прохода кабеля
                    string u = cr.Path[i], v = cr.Path[i + 1];
                    if (string.CompareOrdinal(u, v) > 0) (u, v) = (v, u);
                    if (!nodes.TryGetValue(u, out var a) || !nodes.TryGetValue(v, out var b)) continue;

                    var pa = PN(a); var pb = PN(b);
                    float dx = pb.X - pa.X, dy = pb.Y - pa.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (len < 0.001f) continue;

                    var list = onEdge[EdgeKey(u, v)];
                    float shift = (list.IndexOf(ci) - (list.Count - 1) / 2f) * gap;
                    float nx = -dy / len * shift, ny = dx / len * shift;
                    g.DrawLine(pen, pa.X + nx, pa.Y + ny, pb.X + nx, pb.Y + ny);
                }
            }
        }

        // --- Узлы ---
        foreach (var n in nodes.Values)
        {
            var p = PN(n);
            if (forbidden.Contains(n.Name.Trim()))
            {
                using var xp = new Pen(Color.Red, 2.2f);
                g.DrawLine(xp, p.X - 6, p.Y - 6, p.X + 6, p.Y + 6);
                g.DrawLine(xp, p.X - 6, p.Y + 6, p.X + 6, p.Y - 6);
                if (ShowNodeNames) g.DrawString(n.Name, normal, Brushes.Red, p.X + 6, p.Y + 3);
            }
            else
            {
                g.FillEllipse(Brushes.White, p.X - 4.5f, p.Y - 4.5f, 9, 9);
                g.DrawEllipse(Pens.Black, p.X - 4.5f, p.Y - 4.5f, 9, 9);
                if (ShowNodeNames) g.DrawString(n.Name, normal, Brushes.Black, p.X + 6, p.Y + 3);
            }
        }

        // --- Блоки оборудования ---
        var perNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Project.Blocks)
        {
            if (string.IsNullOrWhiteSpace(b.NodeName) || !nodes.TryGetValue(b.NodeName.Trim(), out var n)) continue;
            perNode.TryGetValue(n.Name, out int k);
            perNode[n.Name] = k + 1;
            var p = PN(n);
            if (!ShowBlockNames)
            {
                // без подписи — только маленький маркер, что в узле стоит блок
                var mark = new RectangleF(p.X - 11 - k * 7, p.Y - 11, 6, 6);
                g.FillRectangle(Brushes.Goldenrod, mark);
                g.DrawRectangle(Pens.SaddleBrown, mark.X, mark.Y, mark.Width, mark.Height);
                continue;
            }
            var size = g.MeasureString(b.Designation, bold);
            var rect = new RectangleF(p.X - size.Width - 10, p.Y - 22 - k * 20, size.Width + 6, size.Height + 2);
            using var bb = new SolidBrush(Color.FromArgb(235, 255, 248, 200));
            g.FillRectangle(bb, rect);
            g.DrawRectangle(Pens.DarkGoldenrod, rect.X, rect.Y, rect.Width, rect.Height);
            g.DrawString(b.Designation, bold, Brushes.SaddleBrown, rect.X + 3, rect.Y + 1);
        }

        // --- Легенда ---
        float ly = 8;
        string title = Result == null
            ? "Маршрутная сеть (трассировка не выполнена)"
            : $"Результат трассировки: F = {Result.Total.ToString("0.0##", Ru)} м";
        g.DrawString(title, bold, Brushes.Black, 10, ly);
        ly += 22;
        if (Result != null && ShowLegend)
        {
            for (int ci = 0; ci < Result.Cables.Count; ci++)
            {
                var cr = Result.Cables[ci];
                using var br = new SolidBrush(Palette[ci % Palette.Length]);
                g.FillRectangle(cr.Ok ? br : Brushes.LightGray, 12, ly + 5, 22, 6);
                string txt = cr.Ok
                    ? $"{cr.Code} ({cr.Type}) {cr.From} → {cr.To}: {cr.RouteText}, L = {cr.Length.ToString("0.0##", Ru)} м"
                    : $"{cr.Code}: {cr.Note}";
                g.DrawString(txt, normal, cr.Ok ? Brushes.Black : Brushes.Red, 40, ly);
                ly += 18;
            }
        }

        string axes = $"ЛКМ — вращать, колесо — масштаб ({_zoom * 100:0}%), двойной клик — сброс вида";
        g.DrawString(axes + "     ✕ — узел в запрещённой зоне, пунктир — удалённые рёбра",
            small, Brushes.Gray, 10, ClientSize.Height - 18);
    }
}
