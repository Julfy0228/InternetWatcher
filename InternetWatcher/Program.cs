using System.Diagnostics;
using System.Text.Json;

namespace InternetWatcher;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}

public class UrlEntry
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public class AppConfig
{
    public List<UrlEntry> Urls { get; set; } = new();

    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 760;
    public int WindowHeight { get; set; } = 560;
}

public static class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InternetWatcher");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null && cfg.Urls.Count > 0)
                    return cfg;
            }
        }
        catch
        {
        }

        var def = CreateDefault();
        Save(def);
        return def;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
        }
    }

    public static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            Urls = new List<UrlEntry>
            {
                new() { Name = "Google",     Url = "https://www.google.com/generate_204" },
                new() { Name = "Cloudflare", Url = "http://cp.cloudflare.com/generate_204" },
                new() { Name = "Microsoft",  Url = "http://edge-http.microsoft.com/captiveportal/generate_204" },
                new() { Name = "Ubuntu",     Url = "http://connectivity-check.ubuntu.com" },
                new() { Name = "MIUI",       Url = "http://connect.rom.miui.com/generate_204" },
            },
            WindowX = -1,
            WindowY = -1,
            WindowWidth = 760,
            WindowHeight = 560
        };
    }
}

public class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HttpClient _httpClient;

    private readonly ToolStripMenuItem _statusRoot;
    private readonly ToolStripMenuItem _windowItem;

    private const int IconSize = 16;
    private const int MaxBars = 5;
    private const int TimeoutMs = 2000;

    private readonly AppConfig _config;
    private List<UrlEntry> _urls;
    private MonitorForm? _monitorForm;

    private bool _exiting;

    public TrayAppContext()
    {
        _config = ConfigManager.Load();
        _urls = _config.Urls;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(TimeoutMs)
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = true,
            Text = "Internet Watcher"
        };

        var contextMenu = new ContextMenuStrip();

        _statusRoot = new ToolStripMenuItem("Статус");
        contextMenu.Items.Add(_statusRoot);

        _windowItem = new ToolStripMenuItem("Открыть окно");
        _windowItem.Click += (_, _) => ToggleWindow();
        contextMenu.Items.Add(_windowItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => Exit();
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    private async Task TickAsync()
    {
        if (_exiting) return;

        try
        {
            var entries = _urls.ToList();
            var tasks = entries.Select(x => CheckInternet(x.Url)).ToArray();
            var results = await Task.WhenAll(tasks);

            if (_exiting) return;

            UpdateStatusMenu(entries, results);
            UpdateIcon(results);

            double avg = results.Where(r => r.ok)
                                .Select(r => (double)r.ping)
                                .DefaultIfEmpty(double.NaN)
                                .Average();
            double? avgPing = double.IsNaN(avg) ? null : avg;

            if (!_exiting)
                _monitorForm?.UpdateData(entries, results, avgPing);
        }
        catch (ObjectDisposedException) { }
        catch (TaskCanceledException) { }
    }

    private void UpdateStatusMenu(List<UrlEntry> entries, (bool ok, int code, long ping)[] results)
    {
        _statusRoot.DropDownItems.Clear();

        for (int i = 0; i < results.Length; i++)
        {
            var (ok, code, ping) = results[i];
            string name = entries[i].Name;

            string text = $"{name}: {(ok ? "✓" : "✗")}{(ok ? $", {ping} ms" : "")}";

            var item = new ToolStripMenuItem(text)
            {
                Enabled = false
            };

            _statusRoot.DropDownItems.Add(item);
        }
    }

    private void UpdateIcon((bool ok, int code, long ping)[] results)
    {
        int serverCount = results.Length;
        int bars = Math.Min(serverCount, MaxBars);

        var barValues = new List<(bool ok, long ping)>();

        for (int i = 0; i < bars; i++)
        {
            int start = (int)Math.Round(i * serverCount / (double)bars);
            int end = (int)Math.Round((i + 1) * serverCount / (double)bars);

            var group = results.Skip(start).Take(end - start).ToArray();

            if (group.Length == 0)
            {
                barValues.Add((true, 0));
                continue;
            }

            bool anyFail = group.Any(g => !g.ok);

            long maxPing = group.Where(g => g.ok)
                                .Select(g => g.ping)
                                .DefaultIfEmpty(TimeoutMs)
                                .Max();

            barValues.Add((!anyFail, maxPing));
        }

        Icon? icon = CreateBarsIcon(barValues);
        _notifyIcon.Icon = icon;
    }

    private static Icon? CreateBarsIcon(List<(bool ok, long ping)> bars)
    {
        var bmp = new Bitmap(IconSize, IconSize);
        using var g = Graphics.FromImage(bmp);

        g.Clear(Color.Transparent);

        int barWidth = IconSize / bars.Count;
        if (barWidth < 1) barWidth = 1;

        for (int i = 0; i < bars.Count; i++)
        {
            var (ok, ping) = bars[i];

            Color color;
            int height;

            if (!ok)
            {
                color = Color.FromArgb(0xff, 0x00, 0x00);
                height = IconSize / 3;
            }
            else if (ping <= TimeoutMs / 2)
            {
                color = Color.FromArgb(0x00, 0xff, 0x00);
                height = IconSize;
            }
            else
            {
                color = Color.FromArgb(0xff, 0xff, 0x00);
                height = (IconSize * 2) / 3;
            }

            int x = i * barWidth;
            int y = IconSize - height;

            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, x, y, barWidth - 1, height);
        }

        IntPtr hIcon = bmp.GetHicon();
        Icon? icon = Icon.FromHandle(hIcon).Clone() as Icon;
        DestroyIcon(hIcon);

        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    extern static bool DestroyIcon(IntPtr handle);

    private async Task<(bool ok, int code, long ping)> CheckInternet(string url)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            sw.Stop();

            int code = (int)response.StatusCode;
            bool ok = response.IsSuccessStatusCode;

            return (ok, code, sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, 0, sw.ElapsedMilliseconds);
        }
    }

    private void ToggleWindow()
    {
        if (_monitorForm == null || _monitorForm.IsDisposed)
        {
            _monitorForm = new MonitorForm(_config, _urls, OnUrlsChanged);
            _monitorForm.FormClosing += (_, e) =>
            {
                if (_exiting) return;
                e.Cancel = true;
                SaveWindowState();
                _monitorForm!.Hide();
                _windowItem.Text = "Открыть окно";
            };

            _monitorForm.Show();
            _windowItem.Text = "Закрыть окно";
            return;
        }

        if (_monitorForm.Visible)
        {
            SaveWindowState();
            _monitorForm.Hide();
            _windowItem.Text = "Открыть окно";
        }
        else
        {
            _monitorForm.Show();
            _windowItem.Text = "Закрыть окно";
        }
    }

    private void SaveWindowState()
    {
        if (_monitorForm == null || _monitorForm.IsDisposed) return;

        _config.WindowX = _monitorForm.Location.X;
        _config.WindowY = _monitorForm.Location.Y;
        _config.WindowWidth = _monitorForm.Size.Width;
        _config.WindowHeight = _monitorForm.Size.Height;
        ConfigManager.Save(_config);
    }

    private void OnUrlsChanged(List<UrlEntry> newUrls)
    {
        _urls = newUrls;
        _config.Urls = newUrls;
        ConfigManager.Save(_config);
    }

    private void Exit()
    {
        _exiting = true;

        _timer.Stop();
        _timer.Dispose();

        if (_monitorForm != null && !_monitorForm.IsDisposed)
        {
            SaveWindowState();
            _monitorForm.Dispose();
            _monitorForm = null;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _httpClient.Dispose();

        ExitThread();
    }
}

public class MonitorForm : Form
{
    private readonly TabControl _tabs;
    private readonly PingChartControl _chart;
    private readonly DataGridView _statusGrid;
    private readonly DataGridView _urlGrid;
    private readonly Action<List<UrlEntry>> _onUrlsChanged;

    public MonitorForm(AppConfig config, List<UrlEntry> urls, Action<List<UrlEntry>> onUrlsChanged)
    {
        _onUrlsChanged = onUrlsChanged;

        Text = "Internet Watcher";
        Icon = SystemIcons.Information;
        MinimumSize = new Size(560, 400);

        int width = Math.Max(config.WindowWidth, 600);
        int height = Math.Max(config.WindowHeight, 420);

        if (config.WindowX >= 0 && config.WindowY >= 0)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(config.WindowX, config.WindowY, width, height);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(width, height);
        }

        _tabs = new TabControl
        {
            Location = new Point(0, 0),
            Size = new Size(ClientSize.Width, ClientSize.Height),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };

        var monitorTab = new TabPage("Мониторинг");
        var settingsTab = new TabPage("Настройки серверов");

        BuildMonitorTab(monitorTab, out _chart, out _statusGrid);
        BuildSettingsTab(settingsTab, urls, out _urlGrid);

        _tabs.TabPages.Add(monitorTab);
        _tabs.TabPages.Add(settingsTab);
        Controls.Add(_tabs);
    }

    private void BuildMonitorTab(TabPage tab, out PingChartControl chart, out DataGridView statusGrid)
    {
        var chartLabel = new Label
        {
            Text = "Средний пинг (мс)",
            Location = new Point(10, 8),
            Size = new Size(300, 18),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        chart = new PingChartControl
        {
            Location = new Point(10, 30),
            Size = new Size(tab.ClientSize.Width - 20, 220),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle
        };

        var listLabel = new Label
        {
            Text = "Серверы",
            Location = new Point(10, 262),
            Size = new Size(200, 18),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        statusGrid = new DataGridView
        {
            Location = new Point(10, 284),
            Size = new Size(tab.ClientSize.Width - 20, tab.ClientSize.Height - 294),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            StandardTab = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ScrollBars = ScrollBars.Vertical,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        };

        statusGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Имя", FillWeight = 35, SortMode = DataGridViewColumnSortMode.NotSortable });
        statusGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Статус", FillWeight = 22, SortMode = DataGridViewColumnSortMode.NotSortable });
        statusGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code", HeaderText = "Код", FillWeight = 18, SortMode = DataGridViewColumnSortMode.NotSortable });
        statusGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ping", HeaderText = "Пинг", FillWeight = 25, SortMode = DataGridViewColumnSortMode.NotSortable });

        tab.Controls.Add(chartLabel);
        tab.Controls.Add(chart);
        tab.Controls.Add(listLabel);
        tab.Controls.Add(statusGrid);
    }

    private void BuildSettingsTab(TabPage tab, List<UrlEntry> urls, out DataGridView grid)
    {
        var hintLabel = new Label
        {
            Text = "Список серверов для проверки",
            Location = new Point(10, 8),
            Size = new Size(500, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        grid = new DataGridView
        {
            Location = new Point(10, 30),
            Size = new Size(tab.ClientSize.Width - 20, tab.ClientSize.Height - 80),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ScrollBars = ScrollBars.Vertical
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Название", FillWeight = 30, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Url", HeaderText = "URL", FillWeight = 70, SortMode = DataGridViewColumnSortMode.NotSortable });

        foreach (var u in urls)
            grid.Rows.Add(u.Name, u.Url);

        var localGrid = grid;

        var addBtn = new Button
        {
            Text = "Добавить",
            Location = new Point(10, tab.ClientSize.Height - 40),
            Size = new Size(100, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        addBtn.Click += (_, _) => localGrid.Rows.Add("Новый сервер", "https://");

        var removeBtn = new Button
        {
            Text = "Удалить",
            Location = new Point(118, tab.ClientSize.Height - 40),
            Size = new Size(100, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        removeBtn.Click += (_, _) =>
        {
            if (localGrid.CurrentRow != null && !localGrid.CurrentRow.IsNewRow)
                localGrid.Rows.Remove(localGrid.CurrentRow);
        };

        var defaultsBtn = new Button
        {
            Text = "По умолчанию",
            Location = new Point(226, tab.ClientSize.Height - 40),
            Size = new Size(130, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };
        defaultsBtn.Click += (_, _) =>
        {
            localGrid.Rows.Clear();
            foreach (var u in ConfigManager.CreateDefault().Urls)
                localGrid.Rows.Add(u.Name, u.Url);
        };

        var saveBtn = new Button
        {
            Text = "Сохранить",
            Location = new Point(tab.ClientSize.Width - 130, tab.ClientSize.Height - 40),
            Size = new Size(120, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        saveBtn.Click += (_, _) => SaveUrlsFromGrid(localGrid);

        tab.Controls.Add(hintLabel);
        tab.Controls.Add(grid);
        tab.Controls.Add(addBtn);
        tab.Controls.Add(removeBtn);
        tab.Controls.Add(defaultsBtn);
        tab.Controls.Add(saveBtn);
    }

    private void SaveUrlsFromGrid(DataGridView grid)
    {
        var list = new List<UrlEntry>();

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;

            string name = row.Cells[0].Value?.ToString()?.Trim() ?? "";
            string url = row.Cells[1].Value?.ToString()?.Trim() ?? "";

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;

            list.Add(new UrlEntry { Name = name, Url = url });
        }

        if (list.Count == 0)
        {
            MessageBox.Show(this, "Список серверов не может быть пустым.", "Internet Watcher",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _onUrlsChanged(list);

        MessageBox.Show(this, "Список серверов сохранён.", "Internet Watcher",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void UpdateData(List<UrlEntry> entries, (bool ok, int code, long ping)[] results, double? avgPing)
    {
        if (IsDisposed) return;

        _statusGrid.SuspendLayout();
        _statusGrid.Rows.Clear();

        for (int i = 0; i < entries.Count && i < results.Length; i++)
        {
            var (ok, code, ping) = results[i];

            int rowIndex = _statusGrid.Rows.Add(
                entries[i].Name,
                ok ? "OK" : "FAIL",
                code.ToString(),
                ok ? $"{ping} мс" : "—");

            _statusGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
            _statusGrid.Rows[rowIndex].DefaultCellStyle.SelectionBackColor = Color.White;
            _statusGrid.Rows[rowIndex].DefaultCellStyle.SelectionForeColor = ok ? Color.DarkGreen : Color.Firebrick;
        }

        _statusGrid.ResumeLayout();

        _chart.AddValue(avgPing);
    }
}

public class PingChartControl : Panel
{
    private const int MaxPoints = 120;
    private readonly List<double?> _values = new();

    public PingChartControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    public void AddValue(double? value)
    {
        _values.Add(value);
        if (_values.Count > MaxPoints)
            _values.RemoveAt(0);

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.White);

        var rect = new Rectangle(38, 8, Width - 48, Height - 34);
        g.DrawRectangle(Pens.LightGray, rect);

        if (_values.Count < 2)
        {
            using var f = new Font("Segoe UI", 9);
            g.DrawString("Сбор данных...", f, Brushes.Gray, rect.Left + 10, rect.Top + 10);
            return;
        }

        double maxVal = _values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(100).Max();
        maxVal = Math.Max(maxVal * 1.2, 50);

        const int gridLines = 4;
        using var gridPen = new Pen(Color.FromArgb(230, 230, 230));
        using var textBrush = new SolidBrush(Color.Gray);
        using var smallFont = new Font("Segoe UI", 7);

        for (int i = 0; i <= gridLines; i++)
        {
            int y = rect.Top + (int)(rect.Height * i / (double)gridLines);
            g.DrawLine(gridPen, rect.Left, y, rect.Right, y);
            double val = maxVal * (1 - i / (double)gridLines);
            g.DrawString($"{val:0}", smallFont, textBrush, 2, y - 6);
        }

        float xStep = rect.Width / (float)(MaxPoints - 1);
        int startIndex = MaxPoints - _values.Count;

        var lineColor = Color.FromArgb(0, 90, 220);
        using var linePen = new Pen(lineColor, 2.5f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        using var pointBrush = new SolidBrush(lineColor);
        using var failBrush = new SolidBrush(Color.Red);

        var segments = new List<List<PointF>>();
        var current = new List<PointF>();

        for (int i = 0; i < _values.Count; i++)
        {
            float x = rect.Left + (startIndex + i) * xStep;
            var v = _values[i];

            if (v.HasValue)
            {
                float y = rect.Bottom - (float)(v.Value / maxVal * rect.Height);
                y = Math.Max(rect.Top, Math.Min(rect.Bottom, y));
                current.Add(new PointF(x, y));
            }
            else
            {
                if (current.Count > 0)
                {
                    segments.Add(current);
                    current = new List<PointF>();
                }
                g.FillEllipse(failBrush, x - 3, rect.Bottom - 6, 6, 6);
            }
        }
        if (current.Count > 0)
            segments.Add(current);

        foreach (var segment in segments)
        {
            if (segment.Count >= 2)
            {
                var areaPoints = new List<PointF>(segment)
                {
                    new(segment[^1].X, rect.Bottom),
                    new(segment[0].X, rect.Bottom)
                };

                using var areaBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    rect, Color.FromArgb(90, lineColor), Color.FromArgb(0, lineColor),
                    System.Drawing.Drawing2D.LinearGradientMode.Vertical);

                g.FillPolygon(areaBrush, areaPoints.ToArray());
                g.DrawLines(linePen, segment.ToArray());
            }

            foreach (var pt in segment)
                g.FillEllipse(pointBrush, pt.X - 2, pt.Y - 2, 4, 4);
        }

        var okVals = _values.Where(v => v.HasValue).Select(v => v!.Value).ToList();

        using var statFont = new Font("Segoe UI", 8, FontStyle.Bold);
        using var labelBrush = new SolidBrush(Color.Black);

        if (okVals.Count == 0)
        {
            g.DrawString("Нет данных", statFont, labelBrush, rect.Left, Height - 20);
        }
        else
        {
            float x = rect.Left;
            float y = Height - 20;

            void DrawSegment(string label, double value, bool withUnit)
            {
                string labelText = $"{label}: ";
                g.DrawString(labelText, statFont, labelBrush, x, y);
                x += g.MeasureString(labelText, statFont).Width;

                string valueText = withUnit ? $"{value:0} мс   " : $"{value:0}   ";
                using var valueBrush = new SolidBrush(GetPingColor(value));
                g.DrawString(valueText, statFont, valueBrush, x, y);
                x += g.MeasureString(valueText, statFont).Width;
            }

            DrawSegment("Текущий", okVals.Last(), true);
            DrawSegment("Средний", okVals.Average(), true);
            DrawSegment("Мин", okVals.Min(), false);
            DrawSegment("Макс", okVals.Max(), false);
        }
    }

    private static Color GetPingColor(double pingMs)
    {
        const int timeoutMs = 2000;

        if (pingMs <= timeoutMs * 0.5)
            return Color.FromArgb(0, 150, 0);

        if (pingMs <= timeoutMs * 0.85)
            return Color.FromArgb(200, 150, 0);

        return Color.FromArgb(200, 0, 0);
    }
}