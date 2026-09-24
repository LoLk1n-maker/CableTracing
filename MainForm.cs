using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CableRouting;

public class MainForm : Form
{
    private Project _project;
    private RoutingResult _result;

    private readonly DataGridView _gridNodes, _gridEdges, _gridZones, _gridBlocks, _gridConn, _gridEmc;
    private readonly DataGridView _gridJournal, _gridSpec;
    private readonly TextBox _txtLog;
    private readonly SchemaView _schema;
    private readonly Label _lblTotal;
    private readonly ToolStripStatusLabel _status;
    private readonly ToolStripTextBox _txtProjectName;
    private bool _loading;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public MainForm()
    {
        Text = "САПР БКС — автоматизированная трассировка бортовой кабельной сети вертолёта";
        Width = 1400; Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);


        // ---------- Меню ---------- 

        // 1. файл
        var menu = new MenuStrip();
        var mFile = new ToolStripMenuItem("Файл");
        mFile.DropDownItems.Add("Контрольный пример", null, (s, e) => LoadProject(Project.CreateControlExample()));
        mFile.DropDownItems.Add("Новый (пустой) проект", null, (s, e) => NewProject());
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("Открыть из БД SQLite...", null, (s, e) => OpenDb());
        mFile.DropDownItems.Add("Сохранить в БД SQLite...", null, (s, e) => SaveDb());
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("Экспорт кабельного журнала (CSV)...", null, (s, e) => ExportCsv());
        mFile.DropDownItems.Add("Сохранить протокол (TXT)...", null, (s, e) => ExportLog());
        mFile.DropDownItems.Add(new ToolStripSeparator());
        mFile.DropDownItems.Add("Выход", null, (s, e) => Close());


        // 2. Справка
        var mHelp = new ToolStripMenuItem("Справка");
        mHelp.DropDownItems.Add("О программе", null, (s, e) => MessageBox.Show(this,
            "Автоматизация трассировки бортовой кабельной сети перспективного вертолёта.\n\n" +
            "Курсовая работа по дисциплине «Разработка систем автоматизированного проектирования».\n" +
            "КНИТУ-КАИ, каф. САПР. Выполнил: Аблякимов А.Ю., гр. 4314.\n\n" +
            "Алгоритм: модифицированный алгоритм Дейкстры —\n" +
            "запрещённые зоны исключаются из графа (2.2–2.4),\n" +
            "требования ЭМС учитываются бесконечным весом рёбер (2.5).",
            "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information));
        menu.Items.Add(mFile);
        menu.Items.Add(mHelp);


        // ---------- Панель инструментов ----------
        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 3, 6, 3) };
        var btnRun = new ToolStripButton("▶  Выполнить трассировку")
        {
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.DarkGreen
        };
        btnRun.Click += (s, e) => RunRouting();
        tool.Items.Add(btnRun);
        tool.Items.Add(new ToolStripSeparator());
        tool.Items.Add(new ToolStripLabel("Проект:"));
        _txtProjectName = new ToolStripTextBox { Width = 320 };
        _txtProjectName.TextChanged += (s, e) => { if (_project != null) _project.Name = _txtProjectName.Text; };
        tool.Items.Add(_txtProjectName);
        tool.Items.Add(new ToolStripSeparator());
        tool.Items.Add(new ToolStripLabel("Вид схемы:"));
        var cbView = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
        cbView.Items.AddRange(new object[] { "Объёмный (по умолчанию)", "Сверху (X–Y)", "Сбоку (X–Z)", "Спереди (Y–Z)" });
        cbView.SelectedIndex = 0;
        cbView.SelectedIndexChanged += (s, e) => _schema.SetPreset((SchemaView.ViewMode)cbView.SelectedIndex);
        tool.Items.Add(cbView);


        // ---------- Статус ----------
        var statusStrip = new StatusStrip();
        _status = new ToolStripStatusLabel("Готово") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        statusStrip.Items.Add(_status);


        // ---------- Левая часть: исходные данные ----------
        var tabsIn = new TabControl { Dock = DockStyle.Fill };
        _gridNodes = MakeGrid<NodeItem>();
        _gridEdges = MakeGrid<EdgeItem>();
        _gridZones = MakeGrid<ZoneItem>();
        _gridBlocks = MakeGrid<BlockItem>();
        _gridConn = MakeGrid<ConnectionItem>(comboProperty: nameof(ConnectionItem.CableType));
        _gridEmc = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window
        };
        _gridEmc.CellEndEdit += EmcCellEndEdit;

        tabsIn.TabPages.Add(MakePage("Узлы", _gridNodes, "Узлы маршрутной сети (вершины графа) и их координаты, м."));
        tabsIn.TabPages.Add(MakePage("Рёбра", _gridEdges, "Допустимые участки трасс. Длина dij вычисляется автоматически по формуле (2.1)."));
        tabsIn.TabPages.Add(MakePage("Запрещ. зоны", _gridZones, "Параллелепипеды; попавшие в них узлы удаляются из графа (2.2)–(2.4)."));
        tabsIn.TabPages.Add(MakePage("Блоки", _gridBlocks, "Блоки бортового оборудования и узлы их подключения."));
        tabsIn.TabPages.Add(MakeConnPage());
        tabsIn.TabPages.Add(MakePage("Нормы ЭМС", _gridEmc, "Dmin(α, β), м. 0 — совместимы (общий жгут), > 0 — совместная прокладка запрещена (2.5)."));

        // ---------- Правая часть: результаты ----------
        var tabsOut = new TabControl { Dock = DockStyle.Fill };
        _schema = new SchemaView { Dock = DockStyle.Fill };
        var pSchema = new TabPage("Схема трассировки");
        pSchema.Controls.Add(_schema);
        pSchema.Controls.Add(MakeDisplayPanel());
        _schema.BringToFront();
        tabsOut.TabPages.Add(pSchema);


        _gridJournal = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window, SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        _gridJournal.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _gridJournal.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        foreach (var (name, weight) in new[] { ("Каб.", 7), ("Откуда – куда", 16), ("Тип", 13), ("Маршрут", 30), ("Σdij, м", 8), ("L, м", 8), ("Примечание", 24) })
            _gridJournal.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });

        _gridSpec = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
            BackgroundColor = SystemColors.Window
        };
        foreach (var name in new[] { "Тип кабеля", "Количество, шт.", "Суммарная длина, м" })
            _gridSpec.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, SortMode = DataGridViewColumnSortMode.NotSortable });

        _lblTotal = new Label
        {
            Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), Text = "Трассировка не выполнена"
        };
        var specHeader = new Label { Dock = DockStyle.Top, Height = 22, Text = "Спецификация кабельной сети", Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft };
        var journalHeader = new Label { Dock = DockStyle.Top, Height = 22, Text = "Кабельный журнал", Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.BottomLeft };

        var journalPanel = new Panel { Dock = DockStyle.Fill };
        journalPanel.Controls.Add(_gridJournal);
        journalPanel.Controls.Add(journalHeader);
        _gridJournal.BringToFront();

        var specPanel = new Panel { Dock = DockStyle.Bottom, Height = 190 };
        specPanel.Controls.Add(_gridSpec);
        specPanel.Controls.Add(specHeader);
        _gridSpec.BringToFront();

        var pJournal = new TabPage("Кабельный журнал");
        pJournal.Controls.Add(journalPanel);
        pJournal.Controls.Add(specPanel);
        pJournal.Controls.Add(_lblTotal);
        journalPanel.BringToFront();
        tabsOut.TabPages.Add(pJournal);

        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Font = new Font("Consolas", 9.5f), BackColor = Color.White
        };
        var pLog = new TabPage("Протокол алгоритма");
        pLog.Controls.Add(_txtLog);
        tabsOut.TabPages.Add(pLog);

        // ---------- Компоновка ----------
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.None };
        split.Panel1.Controls.Add(tabsIn);
        split.Panel2.Controls.Add(tabsOut);

        Controls.Add(split);
        Controls.Add(tool);
        Controls.Add(menu);
        Controls.Add(statusStrip);
        MainMenuStrip = menu;
        split.BringToFront();

        Load += (s, e) =>
        {
            try { split.SplitterDistance = Math.Min(560, split.Width / 2); } catch {  }
        };

        LoadProject(Project.CreateControlExample());
    }

    // ================= Построение интерфейса =================

    private DataGridView MakeGrid<T>(string comboProperty = null)
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            BackgroundColor = SystemColors.Window,
            RowHeadersWidth = 28
        };
        foreach (var pr in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var header = pr.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? pr.Name;
            DataGridViewColumn col;
            if (pr.Name == comboProperty)
                col = new DataGridViewComboBoxColumn { FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
            else
                col = new DataGridViewTextBoxColumn();
            col.Name = pr.Name;
            col.DataPropertyName = pr.Name;
            col.HeaderText = header;
            col.SortMode = DataGridViewColumnSortMode.NotSortable;
            g.Columns.Add(col);
        }
        g.DataError += (s, e) =>
        {
            e.ThrowException = false;
            if (e.ColumnIndex < 0 || (e.Context & (DataGridViewDataErrorContexts.Display | DataGridViewDataErrorContexts.Formatting)) != 0) return;
            _status.Text = "Некорректное значение в ячейке: " + (g.Columns[e.ColumnIndex].HeaderText) + ". Для чисел используйте запятую, например 1,5";
            System.Media.SystemSounds.Beep.Play();
        };
        g.CellValueChanged += (s, e) => DataChanged();
        g.UserDeletedRow += (s, e) => DataChanged();
        g.UserAddedRow += (s, e) => DataChanged();
        return g;
    }

    //Панель с галочками «что показывать на схеме» (справа от схемы).
    private Panel MakeDisplayPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, Width = 185, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(8, 8, 4, 4), BackColor = Color.FromArgb(246, 246, 246),
            BorderStyle = BorderStyle.FixedSingle
        };
        panel.Controls.Add(new Label
        {
            Text = "Отображать:", AutoSize = true, Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6)
        });

        void AddCheck(string text, bool initial, Action<bool> apply)
        {
            var chk = new CheckBox { Text = text, Checked = initial, AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
            chk.CheckedChanged += (s, e) => { apply(chk.Checked); _schema.Invalidate(); };
            panel.Controls.Add(chk);
        }

        AddCheck("Названия блоков", true, v => _schema.ShowBlockNames = v);
        AddCheck("Названия запрещ. зон", true, v => _schema.ShowZoneNames = v);
        AddCheck("Имена узлов", true, v => _schema.ShowNodeNames = v);
        AddCheck("Длины рёбер", true, v => _schema.ShowLengths = v);
        AddCheck("Легенда кабелей", true, v => _schema.ShowLegend = v);
        return panel;
    }

    private static TabPage MakePage(string title, Control content, string hint)
    {
        var page = new TabPage(title);
        var lbl = new Label
        {
            Dock = DockStyle.Top, Height = 34, Text = hint, ForeColor = Color.DimGray,
            Padding = new Padding(4, 4, 4, 0)
        };
        page.Controls.Add(content);
        page.Controls.Add(lbl);
        content.BringToFront();
        return page;
    }

    private TabPage MakeConnPage()
    {
        var page = MakePage("Соединения", _gridConn,
            "Кабели прокладываются последовательно в порядке строк. Порядок можно менять кнопками ▲ ▼.");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(2) };
        var up = new Button { Text = "▲ Выше", Width = 90 };
        var down = new Button { Text = "▼ Ниже", Width = 90 };
        up.Click += (s, e) => MoveConnection(-1);
        down.Click += (s, e) => MoveConnection(+1);
        bar.Controls.Add(up);
        bar.Controls.Add(down);
        page.Controls.Add(bar);
        _gridConn.BringToFront();
        return page;
    }

    // ================= Данные =================

    private void LoadProject(Project p)
    {
        _loading = true;
        _project = p;
        _result = null;
        _txtProjectName.Text = p.Name;

        var combo = (DataGridViewComboBoxColumn)_gridConn.Columns[nameof(ConnectionItem.CableType)];
        combo.DataSource = null;
        combo.Items.Clear();
        foreach (var c in p.Connections)
            if (!string.IsNullOrWhiteSpace(c.CableType) && !p.CableTypes.Contains(c.CableType))
                p.CableTypes.Add(c.CableType);
        combo.Items.AddRange(p.CableTypes.Cast<object>().ToArray());

        _gridNodes.DataSource = p.Nodes;
        _gridEdges.DataSource = p.Edges;
        _gridZones.DataSource = p.Zones;
        _gridBlocks.DataSource = p.Blocks;
        _gridConn.DataSource = p.Connections;
        BuildEmcGrid();

        _schema.Project = p;
        _schema.Result = null;
        _loading = false;
        ShowResult();
        _status.Text = $"Загружен проект «{p.Name}». Нажмите «Выполнить трассировку».";
    }

    private void NewProject()
    {
        var p = new Project { Code = "PRJ001", Name = "Новый проект" };
        foreach (var a in p.CableTypes)
            foreach (var b in p.CableTypes)
                p.SetDmin(a, b, a == b ? 0 : 0.1);
        LoadProject(p);
    }

    private void BuildEmcGrid()
    {
        _gridEmc.Columns.Clear();
        _gridEmc.Rows.Clear();
        _gridEmc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Тип \\ Тип", ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var t in _project.CableTypes)
            _gridEmc.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = t, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var a in _project.CableTypes)
        {
            var row = new List<object> { a };
            row.AddRange(_project.CableTypes.Select(b => (object)_project.GetDmin(a, b).ToString("0.00", Ru)));
            int i = _gridEmc.Rows.Add(row.ToArray());
            _gridEmc.Rows[i].Cells[0].Style.BackColor = SystemColors.Control;
            _gridEmc.Rows[i].Cells[0].Style.Font = new Font(Font, FontStyle.Bold);
        }
    }

    private void EmcCellEndEdit(object sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex < 1 || e.RowIndex < 0) return;
        string a = _project.CableTypes[e.RowIndex];
        string b = _project.CableTypes[e.ColumnIndex - 1];
        var cell = _gridEmc.Rows[e.RowIndex].Cells[e.ColumnIndex];
        if (!TryParse(cell.Value?.ToString(), out double v) || v < 0)
        {
            _status.Text = "Dmin должно быть неотрицательным числом.";
            v = _project.GetDmin(a, b);
        }
        _project.SetDmin(a, b, v);
        cell.Value = v.ToString("0.00", Ru);
        // Матрица симметрична
        _gridEmc.Rows[e.ColumnIndex - 1].Cells[e.RowIndex + 1].Value = v.ToString("0.00", Ru);
        DataChanged();
    }

    private static bool TryParse(string s, out double v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return double.TryParse(s, NumberStyles.Float, Ru, out v) ||
               double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private void MoveConnection(int dir)
    {
        if (_gridConn.CurrentRow == null) return;
        _gridConn.EndEdit();
        int i = _gridConn.CurrentRow.Index;
        int j = i + dir;
        var list = _project.Connections;
        if (i < 0 || i >= list.Count || j < 0 || j >= list.Count) return;
        var item = list[i];
        list.RemoveAt(i);
        list.Insert(j, item);
        _gridConn.CurrentCell = _gridConn.Rows[j].Cells[0];
        DataChanged();
    }

    private void DataChanged()
    {
        if (_loading) return;
        if (_result != null)
        {
            _result = null;
            ShowResult();
            _status.Text = "Исходные данные изменены — выполните трассировку заново.";
        }
        _schema.Invalidate();
    }

    // ================= Трассировка =================

    private void RunRouting()
    {
        foreach (var g in new[] { _gridNodes, _gridEdges, _gridZones, _gridBlocks, _gridConn, _gridEmc })
            g.EndEdit();
        try
        {
            _result = Router.Run(_project);
        }
        catch (Exception ex)
        {
            _result = null;
            ShowResult();
            MessageBox.Show(this, ex.Message, "Ошибка в исходных данных", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _status.Text = "Ошибка: " + ex.Message;
            return;
        }
        ShowResult();
        int bad = _result.Cables.Count(c => !c.Ok);
        _status.Text = bad == 0
            ? $"Трассировка выполнена: проложено кабелей — {_result.Cables.Count}, F = {_result.Total.ToString("0.0##", Ru)} м."
            : $"Трассировка выполнена с ошибками: не проложено кабелей — {bad}. См. протокол.";
    }

    private void ShowResult()
    {
        _schema.Result = _result;
        _schema.Invalidate();
        _gridJournal.Rows.Clear();
        _gridSpec.Rows.Clear();

        if (_result == null)
        {
            _lblTotal.Text = "Трассировка не выполнена";
            _txtLog.Text = "Нажмите «▶ Выполнить трассировку».";
            return;
        }

        for (int i = 0; i < _result.Cables.Count; i++)
        {
            var c = _result.Cables[i];
            int r = _gridJournal.Rows.Add(c.Code, $"{c.From} – {c.To}", c.Type, c.RouteText,
                c.Ok ? c.Geo.ToString("0.0##", Ru) : "—",
                c.Ok ? c.Length.ToString("0.0##", Ru) : "—",
                c.Note);
            var color = SchemaView.Palette[i % SchemaView.Palette.Length];
            _gridJournal.Rows[r].Cells[0].Style.BackColor = color;
            _gridJournal.Rows[r].Cells[0].Style.ForeColor = Color.White;
            if (!c.Ok) _gridJournal.Rows[r].DefaultCellStyle.ForeColor = Color.Red;
        }

        foreach (var gr in _result.Cables.Where(c => c.Ok).GroupBy(c => c.Type))
            _gridSpec.Rows.Add(gr.Key, gr.Count(), gr.Sum(c => c.Length).ToString("0.0##", Ru));
        _gridSpec.Rows.Add("ИТОГО", _result.Cables.Count(c => c.Ok), _result.Total.ToString("0.0##", Ru));
        _gridSpec.Rows[_gridSpec.Rows.Count - 1].DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);

        _lblTotal.Text = $"Суммарная длина БКС  F = Σ L(k) = {_result.Total.ToString("0.0##", Ru)} м";
        _txtLog.Text = _result.Log.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    // ================= Файлы =================

    private void OpenDb()
    {
        using var dlg = new OpenFileDialog { Filter = "База данных SQLite (*.db;*.sqlite)|*.db;*.sqlite|Все файлы (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            LoadProject(Database.Load(dlg.FileName));
            _status.Text = "Проект загружен из " + dlg.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось открыть БД:\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveDb()
    {
        foreach (var g in new[] { _gridNodes, _gridEdges, _gridZones, _gridBlocks, _gridConn, _gridEmc }) g.EndEdit();
        using var dlg = new SaveFileDialog { Filter = "База данных SQLite (*.db)|*.db", FileName = "bks_project.db" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Database.Save(dlg.FileName, _project, _result);
            _status.Text = "Проект сохранён в " + dlg.FileName + (_result != null ? " (вместе с результатами трассировки)" : "");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось сохранить БД:\n" + ex.Message +
                "\n\nПроверьте, что все блоки ссылаются на существующие узлы, а соединения — на существующие блоки.",
                "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportCsv()
    {
        if (_result == null) { MessageBox.Show(this, "Сначала выполните трассировку."); return; }
        using var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "cable_journal.csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder();
        sb.AppendLine("Каб.;Откуда;Куда;Тип;Маршрут;Геом. длина, м;L, м;Примечание");
        foreach (var c in _result.Cables)
            sb.AppendLine(string.Join(";", c.Code, c.From, c.To, c.Type, c.RouteText,
                c.Ok ? c.Geo.ToString("0.###", Ru) : "", c.Ok ? c.Length.ToString("0.###", Ru) : "", c.Note));
        sb.AppendLine($";;;;ИТОГО;;{_result.Total.ToString("0.###", Ru)};");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        _status.Text = "Журнал сохранён: " + dlg.FileName;
    }

    private void ExportLog()
    {
        if (_result == null) { MessageBox.Show(this, "Сначала выполните трассировку."); return; }
        using var dlg = new SaveFileDialog { Filter = "Текст (*.txt)|*.txt", FileName = "protocol.txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, _txtLog.Text, new UTF8Encoding(true));
        _status.Text = "Протокол сохранён: " + dlg.FileName;
    }
}
