using System.Diagnostics;

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

public class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HttpClient _httpClient;

    private readonly ToolStripMenuItem _statusRoot;

    private const int IconSize = 16;
    private const int MaxBars = 8;
    private const int TimeoutMs = 2000;

    private static readonly (string Name, string Url)[] CheckUrls =
    {
        ("Google",     "https://www.google.com/generate_204"),
        ("Cloudflare", "http://cp.cloudflare.com/generate_204"),
        ("Microsoft",  "http://edge-http.microsoft.com/captiveportal/generate_204"),
        ("Ubuntu",     "http://connectivity-check.ubuntu.com"),
        ("MIUI",       "http://connect.rom.miui.com/generate_204")
    };

    public TrayAppContext()
    {
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
        var tasks = CheckUrls.Select(x => CheckInternet(x.Url)).ToArray();
        var results = await Task.WhenAll(tasks);

        UpdateStatusMenu(results);
        UpdateIcon(results);
    }

    private void UpdateStatusMenu((bool ok, int code, long ping)[] results)
    {
        _statusRoot.DropDownItems.Clear();

        for (int i = 0; i < results.Length; i++)
        {
            var (ok, code, ping) = results[i];
            string name = CheckUrls[i].Name;

            string text = $"{name}: {(ok ? "✓" : "✗")} ({code}){(ok ? $", {ping} ms" : "")}";

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
                color = Color.FromArgb(0xff, 0x00, 0x00); // red
                height = IconSize / 3;
            }
            else if (ping <= TimeoutMs / 2)
            {
                color = Color.FromArgb(0x00, 0xff, 0x00); // green
                height = IconSize;
            }
            else
            {
                color = Color.FromArgb(0xff, 0xff, 0x00); // yellow
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
            bool ok = code == 204;

            return (ok, code, sw.ElapsedMilliseconds);
        }
        catch
        {
            sw.Stop();
            return (false, 0, sw.ElapsedMilliseconds);
        }
    }

    private void Exit()
    {
        _timer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _httpClient.Dispose();
        Application.Exit();
    }
}
