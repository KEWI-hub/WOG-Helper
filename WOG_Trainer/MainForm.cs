using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WOG_Trainer;

/// <summary>
/// WOG Helper. Injects WOGHook.dll and installs two scripts in the game's JS env:
///   loot_log.js - read-only log of what the hero picks up
///   auto.js     - auto equip / auto sort / auto training through the game's own UI functions
/// </summary>
internal sealed class MainForm : Form
{
    private const string ProcessName = "Genesis";
    private const string HookDll     = "WOGHook.dll";
    private const string GoldTid     = "1000";
    private const string ExpTid      = "2000";

    private static readonly Color Bg     = Color.FromArgb(24, 26, 32);
    private static readonly Color Panel  = Color.FromArgb(34, 37, 46);
    private static readonly Color Fg     = Color.FromArgb(230, 232, 238);
    private static readonly Color Dim    = Color.FromArgb(150, 155, 170);
    private static readonly Color Accent = Color.FromArgb(232, 170, 60);
    private static readonly Color Good   = Color.FromArgb(110, 200, 120);
    private static readonly Color Bad    = Color.FromArgb(230, 100, 100);

    private readonly Settings _settings = Settings.Load();
    private readonly string _lootScript = ReadResource("WOG_Trainer.loot_log.js");
    private readonly string _autoScript = ReadResource("WOG_Trainer.auto.js");

    // Top bar
    private readonly Button _connect = new();
    private readonly Label  _status  = new();
    private readonly Button _tabLoot = new();
    private readonly Button _tabAuto = new();
    private readonly Panel  _pageLoot = new();
    private readonly Panel  _pageAuto = new();
    private readonly Button _tabSmith = new();
    private readonly Panel  _pageSmith = new();
    private readonly Label    _smithInfo = new();
    private readonly ListView _smithList = new();

    // Loot page
    private readonly Label    _summary = new();
    private readonly ListView _recent  = new();
    private readonly ListView _totals  = new();
    private readonly CheckBox _hideCurrency = new();
    private readonly Button   _reset  = new();
    private readonly Button   _export = new();

    // Automation page
    private readonly CheckBox      _equipOn = new();
    private readonly NumericUpDown _equipSec = new();
    private readonly CheckBox      _sortOn = new();
    private readonly NumericUpDown _sortSec = new();
    private readonly CheckBox      _trainOn = new();
    private readonly CheckBox      _trainDamage = new();
    private readonly CheckBox      _raidOn = new();
    private readonly NumericUpDown _raidSec = new();
    private readonly CheckBox      _fuseOn = new();
    private readonly ComboBox      _fuseMax = new();

    private static readonly string[] Grades =
        ["Normal", "Magic", "Rare", "Hero", "Legend", "Myth", "Ancient", "Primordial", "Transcendent", "Divine"];
    private readonly NumericUpDown _trainReserve = new();
    private readonly NumericUpDown _trainMs = new();
    private readonly Label         _autoStats = new();
    private readonly ListView      _autoLog = new();

    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };
    private readonly NotifyIcon _tray = new() { Text = "WOG Helper" };
    private TrainerBridge? _bridge;
    private Process? _game;
    private bool _polling;
    private bool _loadingSettings;
    private int _failures;

    public MainForm()
    {
        Text = "WOG Helper";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(540, 620);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 720);
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = new Icon(typeof(MainForm).Assembly.GetManifestResourceStream("WOG_Trainer.icon.ico")!); }
        catch { /* default icon */ }

        StyleButton(_connect, "Connect", Accent, Color.Black);
        _connect.SetBounds(12, 12, 110, 32);
        _connect.Click += (_, _) => ToggleConnect();

        _status.SetBounds(132, 12, 416, 32);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Dim;
        _status.Text = "Not connected - open the game first";
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        StyleButton(_tabLoot, "Loot Log", Panel, Fg);
        _tabLoot.SetBounds(12, 54, 120, 30);
        _tabLoot.Click += (_, _) => ShowPage(_pageLoot);
        StyleButton(_tabAuto, "Automation", Panel, Fg);
        _tabAuto.SetBounds(136, 54, 120, 30);
        _tabAuto.Click += (_, _) => ShowPage(_pageAuto);
        StyleButton(_tabSmith, "Blacksmith", Panel, Fg);
        _tabSmith.SetBounds(260, 54, 120, 30);
        _tabSmith.Click += (_, _) => ShowPage(_pageSmith);

        foreach (var page in new[] { _pageLoot, _pageAuto, _pageSmith })
        {
            page.SetBounds(0, 90, ClientSize.Width, ClientSize.Height - 90);
            page.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            page.BackColor = Bg;
        }
        BuildLootPage();
        BuildAutoPage();
        BuildSmithPage();

        Controls.AddRange([_connect, _status, _tabLoot, _tabAuto, _tabSmith, _pageLoot, _pageAuto, _pageSmith]);
        ShowPage(_pageLoot);

        _poll.Tick += async (_, _) => await PollAsync();
        SetConnectedUi(false);
        SetupTray();
    }

    // Minimize hides the window to the notification area; left-click the icon to bring it back.
    private void SetupTray()
    {
        _tray.Icon = Icon ?? SystemIcons.Application;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };

        // A second launch signals this event instead of starting another copy.
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                try { BeginInvoke(RestoreFromTray); } catch { return; }   // form gone
            }
        }) { IsBackground = true };
        listener.Start();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && Visible)
        {
            Hide();
            _tray.Visible = true;
        }
    }

    // ------------------------------------------------------------------ layout

    private void BuildLootPage()
    {
        _summary.SetBounds(12, 0, 536, 22);
        _summary.ForeColor = Accent;
        _summary.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var recentLabel = MakeHeader("Recent drops", 12, 28);
        _hideCurrency.SetBounds(300, 26, 248, 22);
        _hideCurrency.Text = "Hide Gold / EXP";
        _hideCurrency.Checked = _settings.HideCurrency;
        _hideCurrency.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _hideCurrency.CheckAlign = ContentAlignment.MiddleRight;
        _hideCurrency.TextAlign = ContentAlignment.MiddleRight;
        _hideCurrency.CheckedChanged += async (_, _) =>
        {
            _settings.HideCurrency = _hideCurrency.Checked;
            _settings.Save();
            await PollAsync();
        };

        SetupList(_recent, ("Time", 80), ("Item", 330), ("Qty", 80));
        _recent.SetBounds(12, 52, 536, 210);
        _recent.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var totalsLabel = MakeHeader("Totals this session", 12, 272);
        SetupList(_totals, ("Item", 290), ("Total", 90), ("Per hour", 110));
        _totals.SetBounds(12, 296, 536, 214);
        _totals.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        StyleButton(_reset, "Reset", Panel, Fg);
        _reset.SetBounds(12, 522, 100, 30);
        _reset.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _reset.Click += async (_, _) => await RunAsync("loot.reset()");

        StyleButton(_export, "Export CSV", Panel, Fg);
        _export.SetBounds(120, 522, 120, 30);
        _export.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _export.Click += async (_, _) => await ExportAsync();

        _pageLoot.Controls.AddRange([_summary, recentLabel, _hideCurrency, _recent, totalsLabel, _totals, _reset, _export]);
    }

    private void BuildAutoPage()
    {
        _loadingSettings = true;
        int y = 0;
        var equipBox = MakeGroup("Auto Equip - wear the best gear from the inventory", ref y, 70);
        SetupCheck(_equipOn, "Enabled", 12, 30, _settings.Equip.Enabled, equipBox);
        AddNumber(equipBox, "Check every (sec)", 200, 30, _equipSec, 1, 3600, _settings.Equip.IntervalSec);

        var sortBox = MakeGroup("Auto Sort - sort the inventory, clears 'New' markers", ref y, 70);
        SetupCheck(_sortOn, "Enabled", 12, 30, _settings.Sort.Enabled, sortBox);
        AddNumber(sortBox, "Every (sec)", 200, 30, _sortSec, 5, 3600, _settings.Sort.IntervalSec);

        var trainBox = MakeGroup("Auto Training - level up Training with gold", ref y, 104);
        SetupCheck(_trainOn, "Enabled", 12, 30, _settings.Training.Enabled, trainBox);
        SetupCheck(_trainDamage, "Damage first", 12, 64, _settings.Training.DamageFirst, trainBox);
        AddNumber(trainBox, "Keep gold (reserve)", 200, 30, _trainReserve, 0, 1_000_000_000_000m, _settings.Training.ReserveGold);
        _trainReserve.ThousandsSeparator = true;
        _trainReserve.Increment = 1000;
        AddNumber(trainBox, "Between level ups (ms)", 200, 64, _trainMs, 200, 60000, _settings.Training.IntervalMs);
        _trainMs.Increment = 100;

        var raidBox = MakeGroup("Auto Raid - enter Battlefield Raid while tickets last", ref y, 70);
        SetupCheck(_raidOn, "Enabled", 12, 30, _settings.Raid.Enabled, raidBox);
        AddNumber(raidBox, "Check every (sec)", 200, 30, _raidSec, 5, 600, _settings.Raid.IntervalSec);

        var fuseBox = MakeGroup("Auto Fusion - Blacksmith fusion of spare bag items", ref y, 70);
        SetupCheck(_fuseOn, "Enabled", 12, 30, _settings.Fusion.Enabled, fuseBox);
        var fuseLabel = new Label { Text = "Fuse up to grade", Left = 200, Top = 33, Width = 170, ForeColor = Dim };
        _fuseMax.DropDownStyle = ComboBoxStyle.DropDownList;
        _fuseMax.Items.AddRange(Grades);
        _fuseMax.SelectedIndex = Math.Clamp(_settings.Fusion.MaxRating, 1, Grades.Length) - 1;
        _fuseMax.SetBounds(374, 30, 140, 26);
        _fuseMax.BackColor = Bg;
        _fuseMax.ForeColor = Fg;
        _fuseMax.SelectedIndexChanged += (_, _) => OnAutoSettingChanged();
        fuseBox.Controls.AddRange([fuseLabel, _fuseMax]);

        _autoStats.SetBounds(12, y + 4, 536, 22);
        _autoStats.ForeColor = Accent;
        _autoStats.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var logLabel = MakeHeader("Activity", 12, y + 30);
        SetupList(_autoLog, ("Time", 80), ("Action", 80), ("Details", 340));
        _autoLog.SetBounds(12, y + 54, 536, _pageAuto.Height - (y + 54) - 12);
        _autoLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _pageAuto.Controls.AddRange([_autoStats, logLabel, _autoLog]);
        _loadingSettings = false;
    }

    private Panel MakeGroup(string title, ref int y, int height)
    {
        var box = new Panel { BackColor = Panel };
        box.SetBounds(12, y, 536, height);
        box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var header = new Label { Text = title, AutoSize = true, Left = 10, Top = 6, ForeColor = Accent,
                                 Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        box.Controls.Add(header);
        _pageAuto.Controls.Add(box);
        y += height + 8;
        return box;
    }

    private void SetupCheck(CheckBox cb, string text, int x, int y, bool value, Control parent)
    {
        cb.Text = text;
        cb.SetBounds(x, y, 150, 26);
        cb.Checked = value;
        cb.CheckedChanged += (_, _) => OnAutoSettingChanged();
        parent.Controls.Add(cb);
    }

    private void AddNumber(Control parent, string label, int x, int y, NumericUpDown nud, decimal min, decimal max, decimal value)
    {
        var l = new Label { Text = label, Left = x, Top = y + 3, Width = 170, ForeColor = Dim };
        nud.SetBounds(x + 174, y, 140, 26);
        nud.Minimum = min;
        nud.Maximum = max;
        nud.Value = Math.Clamp(value, min, max);
        nud.BackColor = Bg;
        nud.ForeColor = Fg;
        nud.ValueChanged += (_, _) => OnAutoSettingChanged();
        parent.Controls.AddRange([l, nud]);
    }

    private static string ReadResource(string name)
    {
        using var s = typeof(MainForm).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Missing resource " + name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static Label MakeHeader(string text, int x, int y) => new()
    {
        Text = text, Left = x, Top = y, AutoSize = true, ForeColor = Dim,
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
    };

    private static void StyleButton(Button b, string text, Color back, Color fore)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = back;
        b.ForeColor = fore;
        b.FlatAppearance.BorderSize = 0;
    }

    private static void SetupList(ListView lv, params (string name, int width)[] cols)
    {
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        lv.BackColor = Panel;
        lv.ForeColor = Fg;
        lv.BorderStyle = BorderStyle.None;
        foreach (var (name, width) in cols) lv.Columns.Add(name, width);
    }

    private void ShowPage(Panel page)
    {
        foreach (var (p, tab) in new[] { (_pageLoot, _tabLoot), (_pageAuto, _tabAuto), (_pageSmith, _tabSmith) })
        {
            bool on = p == page;
            p.Visible = on;
            tab.BackColor = on ? Accent : Panel;
            tab.ForeColor = on ? Color.Black : Fg;
        }
    }

    private void BuildSmithPage()
    {
        _smithInfo.SetBounds(12, 0, 536, 40);
        _smithInfo.ForeColor = Dim;
        _smithInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _smithInfo.Text = "Spare bag items per fusion group. Locked items, storage and gear better than " +
                          "what you wear are left out. Green = ready to fuse.";
        SetupList(_smithList, ("Grade", 80), ("Lv", 40), ("Have", 70), ("Items", 330));
        _smithList.SetBounds(12, 44, 536, _pageSmith.Height - 56);
        _smithList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _pageSmith.Controls.AddRange([_smithInfo, _smithList]);
    }

    private void SetConnectedUi(bool on)
    {
        _reset.Enabled = on;
        _export.Enabled = on;
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
        string tip = "WOG Helper - " + text;
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;   // NotifyIcon text limit
    }

    // ------------------------------------------------------------------ settings

    private async void OnAutoSettingChanged()
    {
        if (_loadingSettings) return;
        _settings.Equip.Enabled        = _equipOn.Checked;
        _settings.Equip.IntervalSec    = (int)_equipSec.Value;
        _settings.Sort.Enabled         = _sortOn.Checked;
        _settings.Sort.IntervalSec     = (int)_sortSec.Value;
        _settings.Training.Enabled     = _trainOn.Checked;
        _settings.Training.ReserveGold = (long)_trainReserve.Value;
        _settings.Training.IntervalMs  = (int)_trainMs.Value;
        _settings.Training.DamageFirst = _trainDamage.Checked;
        _settings.Raid.Enabled         = _raidOn.Checked;
        _settings.Raid.IntervalSec     = (int)_raidSec.Value;
        _settings.Fusion.Enabled       = _fuseOn.Checked;
        _settings.Fusion.MaxRating     = _fuseMax.SelectedIndex + 1;
        _settings.Save();
        if (_bridge != null) await RunAsync($"auto.config({_settings.ToAutoConfigJs()})");
    }

    // ------------------------------------------------------------------ connection

    private void ToggleConnect()
    {
        if (_bridge != null) Disconnect("Disconnected", Dim);
        else Connect();
    }

    private void Connect()
    {
        _game = Process.GetProcessesByName(ProcessName).FirstOrDefault();
        if (_game == null)
        {
            SetStatus("Game not running (Genesis.exe)", Bad);
            return;
        }

        SetStatus("Injecting...", Dim);
        _connect.Enabled = false;
        Application.DoEvents();
        try
        {
            var (ok, msg) = Injector.Inject(_game.Id, Path.Combine(AppContext.BaseDirectory, HookDll));
            if (!ok)
            {
                SetStatus(msg, Bad);
                return;
            }

            var bridge = new TrainerBridge();
            if (!bridge.Connect(10000))
            {
                bridge.Dispose();
                SetStatus("Hook did not answer", Bad);
                return;
            }
            if ((bridge.Status & 4) == 0)
            {
                bridge.Dispose();
                SetStatus("Unsupported game version (frame hook failed)", Bad);
                return;
            }
            _bridge = bridge;
            _bridge.SetPaused(false);
            _connect.Text = "Disconnect";
            SetConnectedUi(true);
            SetStatus("Connected", Good);
            _failures = 0;
            _ = InstallAsync(force: true);
            _poll.Start();
        }
        finally
        {
            _connect.Enabled = true;
        }
    }

    private void Disconnect(string text, Color color)
    {
        _poll.Stop();
        if (_bridge != null)
        {
            // Automation must not keep running without the app; the loot log can keep counting.
            try { _bridge.EvalJs("typeof auto==='object'?auto.off():0", 1000); } catch { /* game busy */ }
            _bridge.SetPaused(true);
            _bridge.Dispose();
            _bridge = null;
        }
        _game = null;
        _connect.Text = "Connect";
        SetConnectedUi(false);
        SetStatus(text, color);
    }

    private Task<string> EvalAsync(string code)
    {
        var bridge = _bridge ?? throw new InvalidOperationException("Not connected");
        return Task.Run(() => bridge.EvalJs(code));
    }

    private async Task RunAsync(string code)
    {
        if (_bridge == null) return;
        try { await EvalAsync(code); }
        catch (Exception ex) { SetStatus(ex.Message, Bad); }
        await PollAsync();
    }

    // (Re)installs the scripts. The game can restart its JS env (reconnect, title screen),
    // which drops both; the poll notices and calls this again.
    private async Task InstallAsync(bool force)
    {
        string present = await EvalAsync("JSON.stringify([typeof loot==='object',typeof auto==='object'])");
        bool hasLoot = present.Contains("[true");
        bool hasAuto = present.EndsWith("true]");
        if (!hasLoot) await EvalAsync(_lootScript);
        if (!hasAuto || force) await EvalAsync(_autoScript);
        await EvalAsync($"auto.config({_settings.ToAutoConfigJs()})");
    }

    private async Task PollAsync()
    {
        if (_bridge == null || _polling) return;
        if (_game == null || _game.HasExited)
        {
            Disconnect("Game closed", Dim);
            return;
        }

        _polling = true;
        try
        {
            string json = await EvalAsync(
                "({loot:typeof loot==='object'?loot.state():null,auto:typeof auto==='object'?auto.state(40):null," +
                "smith:typeof auto==='object'&&auto.fusionPlan?auto.fusionPlan():null})");
            var root = JsonDocument.Parse(json).RootElement;
            if (root.GetProperty("loot").ValueKind == JsonValueKind.Null ||
                root.GetProperty("auto").ValueKind == JsonValueKind.Null)
            {
                await InstallAsync(force: false);
                return;
            }
            RenderLoot(root.GetProperty("loot"));
            RenderAuto(root.GetProperty("auto"));
            if (root.TryGetProperty("smith", out var smith) && smith.ValueKind == JsonValueKind.Array) RenderSmith(smith);
            _failures = 0;
            SetStatus("Connected", Good);
        }
        catch (Exception ex)
        {
            if (++_failures >= 3) SetStatus(ex.Message, Bad);
        }
        finally
        {
            _polling = false;
        }
    }

    // ------------------------------------------------------------------ rendering

    private void RenderLoot(JsonElement state)
    {
        double minutes = state.GetProperty("minutes").GetDouble();
        var totals = state.GetProperty("totals").EnumerateArray().ToList();
        long gold = 0, exp = 0;
        foreach (var t in totals)
        {
            string tid = t.GetProperty("tid").GetString() ?? "";
            if (tid == GoldTid) gold = t.GetProperty("cnt").GetInt64();
            if (tid == ExpTid) exp = t.GetProperty("cnt").GetInt64();
        }
        double hours = Math.Max(minutes / 60.0, 1.0 / 3600);
        _summary.Text = $"{minutes:0.0} min   Gold {gold:N0} ({gold / hours:N0}/h)   EXP {exp:N0} ({exp / hours:N0}/h)";

        var colors = totals.ToDictionary(t => t.GetProperty("tid").GetString() ?? "", ParseColor);

        _recent.BeginUpdate();
        _recent.Items.Clear();
        foreach (var d in state.GetProperty("recent").EnumerateArray().Reverse())
        {
            string tid = d.GetProperty("tid").GetString() ?? "";
            if (_hideCurrency.Checked && (tid == GoldTid || tid == ExpTid)) continue;
            var item = new ListViewItem([d.GetProperty("time").GetString(), d.GetProperty("item").GetString(),
                                         d.GetProperty("cnt").GetInt64().ToString("N0")]);
            if (colors.TryGetValue(tid, out var c) && c.HasValue) item.ForeColor = c.Value;
            _recent.Items.Add(item);
        }
        _recent.EndUpdate();

        _totals.BeginUpdate();
        _totals.Items.Clear();
        foreach (var t in totals)
        {
            var item = new ListViewItem([t.GetProperty("item").GetString(),
                                         t.GetProperty("cnt").GetInt64().ToString("N0"),
                                         t.GetProperty("perHour").GetInt64().ToString("N0")]);
            var c = ParseColor(t);
            if (c.HasValue) item.ForeColor = c.Value;
            _totals.Items.Add(item);
        }
        _totals.EndUpdate();
    }

    private void RenderAuto(JsonElement state)
    {
        var s = state.GetProperty("stats");
        long gold = long.TryParse(state.GetProperty("gold").GetString(), out var g) ? g : 0;
        _autoStats.Text = $"Gold {gold:N0}   Equipped {s.GetProperty("equips").GetInt32()}   " +
                          $"Sorted {s.GetProperty("sorts").GetInt32()}   Trained {s.GetProperty("trainings").GetInt32()} " +
                          $"(-{s.GetProperty("goldSpent").GetInt64():N0} gold)   " +
                          $"Raids {(s.TryGetProperty("raids", out var r) ? r.GetInt32() : 0)}   " +
                          $"Fused {(s.TryGetProperty("fusions", out var f) ? f.GetInt32() : 0)}";

        _autoLog.BeginUpdate();
        _autoLog.Items.Clear();
        foreach (var e in state.GetProperty("log").EnumerateArray().Reverse())
        {
            string kind = e.GetProperty("kind").GetString() ?? "";
            var item = new ListViewItem([e.GetProperty("time").GetString(), kind, e.GetProperty("text").GetString()]);
            if (kind == "error") item.ForeColor = Bad;
            _autoLog.Items.Add(item);
        }
        _autoLog.EndUpdate();
    }

    private void RenderSmith(JsonElement plan)
    {
        _smithList.BeginUpdate();
        _smithList.Items.Clear();
        foreach (var g in plan.EnumerateArray())
        {
            int have = g.GetProperty("have").GetInt32(), need = g.GetProperty("need").GetInt32();
            int kept = g.GetProperty("kept").GetInt32();
            var names = g.GetProperty("items").EnumerateArray().Select(x => x.GetString())
                         .GroupBy(n => n).Select(n => n.Count() > 1 ? $"{n.Key} x{n.Count()}" : n.Key);
            var item = new ListViewItem([g.GetProperty("grade").GetString(), g.GetProperty("level").GetInt32().ToString(),
                                         $"{have}/{need}" + (kept > 0 ? $" (+{kept} kept)" : ""), string.Join(", ", names)]);
            if (have >= need) item.ForeColor = Good;
            _smithList.Items.Add(item);
        }
        _smithList.EndUpdate();
    }

    // Item grade colour from the game's rich text. The common grade is a dark grey that is
    // unreadable on this background, so it falls back to the default text colour.
    private static Color? ParseColor(JsonElement e)
    {
        if (!e.TryGetProperty("color", out var c) || c.ValueKind != JsonValueKind.String) return null;
        if (!int.TryParse(c.GetString(), NumberStyles.HexNumber, null, out int rgb)) return null;
        var color = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        if (color.GetBrightness() < 0.45f && color.GetSaturation() < 0.2f) return null;
        return color.GetBrightness() >= 0.45f ? color
            : Color.FromArgb(Math.Min(255, color.R + 70), Math.Min(255, color.G + 70), Math.Min(255, color.B + 70));
    }

    private async Task ExportAsync()
    {
        if (_bridge == null) return;
        try
        {
            string json = await EvalAsync("loot.list(5000)");
            var sb = new StringBuilder("time,item,qty,tid\r\n");
            foreach (var d in JsonDocument.Parse(json).RootElement.EnumerateArray())
            {
                string name = (d.GetProperty("item").GetString() ?? "").Replace("\"", "\"\"");
                sb.Append(d.GetProperty("time").GetString()).Append(",\"").Append(name).Append("\",")
                  .Append(d.GetProperty("cnt").GetInt64()).Append(',')
                  .Append(d.GetProperty("tid").GetString()).Append("\r\n");
            }
            using var dlg = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"wog_loot_{DateTime.Now:yyyyMMdd_HHmm}.csv",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            // BOM so Excel opens names with non-ASCII characters correctly.
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            SetStatus("Exported " + Path.GetFileName(dlg.FileName), Good);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, Bad);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_bridge != null) Disconnect("Disconnected", Dim);
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }
}
