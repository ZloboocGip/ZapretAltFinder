using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretAltFinder;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class AppConfig
{
    public int TimeoutSeconds { get; set; } = 5;
    public int Attempts { get; set; } = 2;
    public int WarmupMilliseconds { get; set; } = 1800;
    public string? LastStrategy { get; set; }
    public List<string> ExcludedStrategies { get; set; } = [];
    public string Theme { get; set; } = "Light";
}

internal sealed record ProbeResult(string Host, bool Ok, int? Status, long Milliseconds, string Detail);

internal sealed class MainForm : Form
{
    readonly string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    readonly string listsDir;
    readonly string utilsDir;
    readonly string configPath;
    readonly AppConfig config;
    readonly List<int> ownedPids = [];
    CancellationTokenSource? runCts;

    readonly ListBox strategies = new() { Dock = DockStyle.Fill };
    readonly ContextMenuStrip strategyMenu = new();
    readonly DataGridView results = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    readonly TextBox domains = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(28, 30, 34), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9) };
    readonly Button findButton = new() { Text = "Найти рабочую стратегию", AutoSize = true, Height = 34 };
    readonly Button testButton = new() { Text = "Проверить выбранную", AutoSize = true, Height = 34 };
    readonly Button baselineButton = new() { Text = "Проверить без стратегии", AutoSize = true, Height = 34 };
    readonly Button stopButton = new() { Text = "Стоп", AutoSize = true, Height = 34, Enabled = false };
    readonly Button runButton = new() { Text = "Запустить выбранную", AutoSize = true, Height = 34 };
    readonly Button stopZapretButton = new() { Text = "Остановить winws", AutoSize = true, Height = 34 };
    readonly Button publicIpButton = new() { Text = "Проверить мой IP", AutoSize = true, Height = 34 };
    readonly Label status = new() { AutoSize = true, Text = "Готово", Padding = new Padding(8) };
    readonly NumericUpDown timeout = new() { Minimum = 2, Maximum = 30, Width = 55 };
    readonly NumericUpDown attempts = new() { Minimum = 1, Maximum = 5, Width = 55 };
    readonly NumericUpDown warmup = new() { Minimum = 300, Maximum = 10000, Increment = 100, Width = 75 };
    readonly ComboBox gameMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly ComboBox ipsetMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly CheckBox updates = new() { Text = "Проверять обновления" };
    readonly ComboBox discordFake = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly ComboBox gameFake = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly ComboBox themePicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly ComboBox templates = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly ComboBox listPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly TextBox listEditor = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, AcceptsTab = true, WordWrap = false, Font = new Font("Consolas", 10) };
    readonly TextBox addDomain = new() { Width = 290, PlaceholderText = "example.org или https://example.org" };
    readonly ComboBox strategyPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ComboBox blockPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly ComboBox referenceKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly ComboBox referenceFile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    readonly DataGridView referenceGrid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };

    static readonly IReadOnlyDictionary<string, string[]> DomainTemplates = new Dictionary<string, string[]>
    {
        ["Discord"] = ["discord.com", "discord.gg", "gateway.discord.gg", "cdn.discordapp.com", "updates.discord.com", "discordstatus.com"],
        ["Roblox"] = ["www.roblox.com", "clientsettings.api.roblox.com", "versioncompatibility.api.roblox.com", "chat.roblox.com", "assetgame.roblox.com", "setup.roblox.com", "setup.rbxcdn.com", "js.rbxcdn.com", "static.rbxcdn.com", "captcha.roblox.com"],
        ["Steam"] = ["store.steampowered.com", "help.steampowered.com", "steamcommunity.com", "cdn.cloudflare.steamstatic.com", "steamuserimages-a.akamaihd.net", "avatars.akamai.steamstatic.com"],
        ["YouTube"] = ["www.youtube.com", "youtu.be", "i.ytimg.com", "redirector.googlevideo.com", "www.google.com", "www.gstatic.com"],
        ["Discord + Roblox"] = ["discord.com", "discord.gg", "gateway.discord.gg", "cdn.discordapp.com", "www.roblox.com", "assetgame.roblox.com", "setup.rbxcdn.com", "static.rbxcdn.com"],
        ["Всё"] = ["discord.com", "discord.gg", "gateway.discord.gg", "cdn.discordapp.com", "www.roblox.com", "assetgame.roblox.com", "setup.rbxcdn.com", "store.steampowered.com", "steamcommunity.com", "cdn.cloudflare.steamstatic.com", "www.youtube.com", "i.ytimg.com", "redirector.googlevideo.com"]
    };

    public MainForm()
    {
        listsDir = Path.Combine(root, "lists");
        utilsDir = Path.Combine(root, "utils");
        configPath = Path.Combine(utilsDir, "alt-finder.json");
        config = LoadConfig();
        Text = "Zapret Alt Finder";
        MinimumSize = new Size(1000, 680);
        Size = new Size(1180, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        ApplyTheme();
        LoadState();
        FormClosing += (_, _) => { runCts?.Cancel(); SaveConfig(); };
    }

    void BuildUi()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var testPage = new TabPage("Поиск стратегии");
        var settingsPage = new TabPage("Настройки Flowseal");
        var listsPage = new TabPage("Списки доменов");
        var strategyPage = new TabPage("Стратегия и списки");
        tabs.TabPages.AddRange([testPage, listsPage, strategyPage, settingsPage]);

        results.Columns.Add("Domain", "Домен");
        results.Columns.Add("Result", "Результат");
        results.Columns.Add("Time", "Время");
        results.Columns.Add("Details", "Подробности");

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        left.Controls.Add(new Label { Text = "BAT-стратегии", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        ConfigureStrategyList();
        left.Controls.Add(strategies, 0, 1);
        var domainHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        domainHeader.Controls.Add(new Label { Text = "Цели:", AutoSize = true, Padding = new Padding(0, 5, 3, 0) });
        templates.Items.AddRange(DomainTemplates.Keys.ToArray()); templates.SelectedIndex = 0;
        var loadTemplate = new Button { Text = "Загрузить шаблон", AutoSize = true, Height = 25, Margin = new Padding(4, 1, 0, 0) };
        loadTemplate.Click += (_, _) => { if (templates.SelectedItem is string name) domains.Lines = DomainTemplates[name]; };
        domainHeader.Controls.Add(templates); domainHeader.Controls.Add(loadTemplate);
        left.Controls.Add(domainHeader, 0, 2);
        left.Controls.Add(domains, 0, 3);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        right.Controls.Add(results, 0, 0); right.Controls.Add(log, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 330, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(6), WrapContents = false, AutoScroll = true };
        timeout.Value = config.TimeoutSeconds; attempts.Value = config.Attempts; warmup.Value = config.WarmupMilliseconds;
        toolbar.Controls.AddRange([findButton, testButton, baselineButton, publicIpButton, stopButton, runButton, stopZapretButton,
            new Label { Text = "Connect timeout, с:", AutoSize = true, Padding = new Padding(10, 8, 0, 0) }, timeout,
            new Label { Text = "Попытки:", AutoSize = true, Padding = new Padding(6, 8, 0, 0) }, attempts,
            new Label { Text = "Прогрев, мс:", AutoSize = true, Padding = new Padding(6, 8, 0, 0) }, warmup]);
        testPage.Controls.Add(split); testPage.Controls.Add(toolbar); testPage.Controls.Add(status); status.Dock = DockStyle.Bottom;

        findButton.Click += async (_, _) => await FindFirstAsync();
        testButton.Click += async (_, _) => await TestSelectedAsync(keepOnSuccess: true);
        baselineButton.Click += async (_, _) => await TestWithoutStrategyAsync();
        publicIpButton.Click += async (_, _) => await CheckPublicIpAsync();
        stopButton.Click += (_, _) => runCts?.Cancel();
        runButton.Click += async (_, _) =>
        {
            if (!ValidateReady() || SelectedBat() is not { } b) return;
            await StopAllWinwsAsync();
            if (await StartStrategyAsync(b, CancellationToken.None)) status.Text = $"Запущена: {Path.GetFileName(b)}";
        };
        stopZapretButton.Click += async (_, _) => await StopAllWinwsAsync();

        BuildSettings(settingsPage);
        BuildLists(listsPage);
        BuildStrategyLists(strategyPage);
        Controls.Add(tabs);
    }

    void ConfigureStrategyList()
    {
        strategies.DrawMode = DrawMode.OwnerDrawFixed;
        strategies.ItemHeight = Math.Max(strategies.ItemHeight, 18);
        strategies.DrawItem += DrawStrategyItem;
        strategies.MouseDown += (_, e) =>
        {
            int index = strategies.IndexFromPoint(e.Location);
            if (index >= 0) strategies.SelectedIndex = index;
        };

        var toggle = new ToolStripMenuItem("Убрать из перебора");
        var includeAll = new ToolStripMenuItem("Вернуть все в перебор");
        toggle.Click += (_, _) => ToggleSelectedStrategyExcluded();
        includeAll.Click += (_, _) =>
        {
            config.ExcludedStrategies.Clear();
            SaveConfig();
            strategies.Invalidate();
            status.Text = "Все стратегии возвращены в перебор.";
        };
        strategyMenu.Items.AddRange([toggle, includeAll]);
        strategyMenu.Opening += (_, _) =>
        {
            bool hasSelected = strategies.SelectedItem is string;
            toggle.Enabled = hasSelected;
            includeAll.Enabled = config.ExcludedStrategies.Count > 0;
            if (hasSelected && strategies.SelectedItem is string name)
                toggle.Text = IsStrategyExcluded(name) ? "Вернуть в перебор" : "Убрать из перебора";
        };
        strategies.ContextMenuStrip = strategyMenu;
    }

    void DrawStrategyItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        string name = strategies.Items[e.Index].ToString() ?? "";
        bool excluded = IsStrategyExcluded(name);
        e.DrawBackground();
        var color = excluded ? Color.Gray : e.ForeColor;
        using var font = excluded ? new Font(e.Font!, e.Font!.Style | FontStyle.Strikeout) : new Font(e.Font!, e.Font!.Style);
        TextRenderer.DrawText(e.Graphics, name, font, e.Bounds, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }

    void ToggleSelectedStrategyExcluded()
    {
        if (strategies.SelectedItem is not string name) return;
        if (IsStrategyExcluded(name))
        {
            config.ExcludedStrategies.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            status.Text = $"{name}: возвращена в перебор.";
        }
        else
        {
            config.ExcludedStrategies.Add(name);
            status.Text = $"{name}: исключена из перебора.";
        }
        SaveConfig();
        strategies.Invalidate();
    }

    void BuildSettings(TabPage page)
    {
        gameMode.Items.AddRange(["Выключен", "TCP + UDP", "Только TCP", "Только UDP"]);
        ipsetMode.Items.AddRange(["Загруженный", "Любые IP", "Выключен"]);
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(18), CellBorderStyle = TableLayoutPanelCellBorderStyle.None };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0;
        void Add(string title, Control control, string note) { table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); table.Controls.Add(new Label { Text = title, AutoSize = true, Padding = new Padding(0, 9, 0, 0) }, 0, row); table.Controls.Add(control, 1, row); table.Controls.Add(new Label { Text = note, AutoSize = true, Padding = new Padding(0, 9, 0, 0), ForeColor = Color.DimGray }, 2, row++); }
        Add("Game Filter", gameMode, "utils\\game_filter.enabled");
        themePicker.Items.AddRange(["Светлая", "Тёмная"]);
        themePicker.SelectedItem = IsDarkTheme ? "Тёмная" : "Светлая";
        themePicker.SelectedIndexChanged += (_, _) =>
        {
            config.Theme = themePicker.SelectedItem as string == "Тёмная" ? "Dark" : "Light";
            ApplyTheme();
            SaveConfig();
        };
        Add("Тема", themePicker, "сохраняется в utils\\alt-finder.json");
        Add("IPSet Filter", ipsetMode, "lists\\ipset-all.txt (+ .backup)");
        Add("Обновления", updates, "utils\\check_updates.enabled");
        Add("Discord UDP fake", discordFake, "bin\\ACTIVE_DISCORD_UDP.bin");
        Add("Game Filter UDP fake", gameFake, "bin\\ACTIVE_GAME_UDP.bin");
        var apply = new Button { Text = "Применить настройки", AutoSize = true, Height = 34 };
        var service = new Button { Text = "Открыть service.bat", AutoSize = true, Height = 34 };
        var utils = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(18, 4, 0, 0) };
        utils.Controls.AddRange([apply, service]);
        apply.Click += (_, _) =>
        {
            try { ApplySettings(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Настройки не применены", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        service.Click += (_, _) => StartVisible(Path.Combine(root, "service.bat"));
        page.Controls.Add(utils); page.Controls.Add(table);
    }

    bool IsDarkTheme => string.Equals(config.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
    readonly record struct ThemePalette(Color Back, Color Surface, Color Text, Color Muted, Color Border, Color Selection, Color SelectionText, Color Success, Color Error);
    ThemePalette Palette => IsDarkTheme
        ? new(Color.FromArgb(24, 26, 31), Color.FromArgb(34, 37, 44), Color.FromArgb(232, 235, 241), Color.FromArgb(170, 176, 188), Color.FromArgb(72, 77, 89), Color.FromArgb(54, 91, 138), Color.White, Color.FromArgb(31, 64, 46), Color.FromArgb(78, 40, 48))
        : new(Color.FromArgb(245, 246, 248), Color.White, Color.FromArgb(31, 35, 42), Color.FromArgb(95, 101, 112), Color.FromArgb(210, 214, 221), Color.FromArgb(0, 120, 215), Color.White, Color.Honeydew, Color.MistyRose);

    void ApplyTheme()
    {
        ApplyThemeTo(this);
        strategyMenu.BackColor = Palette.Surface;
        strategyMenu.ForeColor = Palette.Text;
        foreach (ToolStripItem item in strategyMenu.Items) ApplyThemeTo(item);
        strategies.Invalidate();
    }

    void ApplyThemeTo(Control control)
    {
        var p = Palette;
        control.BackColor = p.Back;
        control.ForeColor = p.Text;
        switch (control)
        {
            case Form:
            case TabPage:
            case TableLayoutPanel:
            case FlowLayoutPanel:
            case SplitContainer:
            case Panel:
                control.BackColor = p.Back;
                break;
            case TextBox text:
                text.BackColor = p.Surface;
                text.ForeColor = p.Text;
                if (ReferenceEquals(text, log))
                {
                    text.BackColor = IsDarkTheme ? Color.FromArgb(20, 22, 26) : Color.FromArgb(250, 250, 250);
                    text.ForeColor = IsDarkTheme ? Color.Gainsboro : Color.FromArgb(35, 38, 45);
                }
                break;
            case ListBox list:
                list.BackColor = p.Surface;
                list.ForeColor = p.Text;
                break;
            case ComboBox combo:
                combo.BackColor = p.Surface;
                combo.ForeColor = p.Text;
                break;
            case Button button:
                button.BackColor = p.Surface;
                button.ForeColor = p.Text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = p.Border;
                break;
            case NumericUpDown numeric:
                numeric.BackColor = p.Surface;
                numeric.ForeColor = p.Text;
                break;
            case CheckBox check:
                check.BackColor = p.Back;
                check.ForeColor = p.Text;
                break;
            case DataGridView grid:
                grid.BackgroundColor = p.Surface;
                grid.GridColor = p.Border;
                grid.EnableHeadersVisualStyles = false;
                grid.DefaultCellStyle.BackColor = p.Surface;
                grid.DefaultCellStyle.ForeColor = p.Text;
                grid.DefaultCellStyle.SelectionBackColor = p.Selection;
                grid.DefaultCellStyle.SelectionForeColor = p.SelectionText;
                grid.ColumnHeadersDefaultCellStyle.BackColor = IsDarkTheme ? Color.FromArgb(45, 49, 58) : Color.FromArgb(235, 238, 243);
                grid.ColumnHeadersDefaultCellStyle.ForeColor = p.Text;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Text;
                break;
        }
        foreach (Control child in control.Controls) ApplyThemeTo(child);
    }

    void ApplyThemeTo(ToolStripItem item)
    {
        var p = Palette;
        item.BackColor = p.Surface;
        item.ForeColor = p.Text;
    }
    Color ResultColor(bool success) => success ? Palette.Success : Palette.Error;

    void BuildLists(TabPage page)
    {
        listPicker.Items.AddRange([
            new ListFile("Общий список — upstream", "list-general.txt", false),
            new ListFile("Общий список — пользовательский", "list-general-user.txt", true),
            new ListFile("Исключения — upstream", "list-exclude.txt", false),
            new ListFile("Исключения — пользовательские", "list-exclude-user.txt", true),
            new ListFile("Google / YouTube", "list-google.txt", false),
            new ListFile("Проверяемые цели", "check_lists.txt", true)
        ]);
        listPicker.DisplayMember = nameof(ListFile.FileName);
        listPicker.SelectedIndexChanged += (_, _) => LoadSelectedList();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(9), WrapContents = true };
        var reload = new Button { Text = "Перечитать", AutoSize = true, Height = 28 };
        var save = new Button { Text = "Сохранить", AutoSize = true, Height = 28 };
        var normalize = new Button { Text = "Убрать дубликаты и отсортировать", AutoSize = true, Height = 28 };
        var add = new Button { Text = "Добавить", AutoSize = true, Height = 28 };
        top.Controls.AddRange([
            new Label { Text = "Файл:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, listPicker, reload, save, normalize,
            new Label { Text = "  Домен:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, addDomain, add
        ]);
        var hint = new Label { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(10, 8, 10, 0), ForeColor = Color.DimGray,
            Text = "Строки с # сохраняются как комментарии. В пользовательские листы можно добавлять свои домены без изменения upstream-файлов. Сохранение вступит в силу после перезапуска стратегии." };
        reload.Click += (_, _) => LoadSelectedList();
        save.Click += (_, _) => SaveSelectedList();
        normalize.Click += (_, _) => NormalizeList();
        add.Click += (_, _) => AddDomainToList();
        page.Controls.Add(listEditor); page.Controls.Add(top); page.Controls.Add(hint);
    }

    void BuildStrategyLists(TabPage page)
    {
        referenceGrid.Columns.Add("Block", "Фильтр / блок");
        referenceGrid.Columns.Add("Kind", "Тип");
        referenceGrid.Columns.Add("File", "Файл списка");
        referenceGrid.Columns.Add("Line", "Строка BAT");
        referenceKind.Items.AddRange(["hostlist", "hostlist-exclude", "ipset", "ipset-exclude"]);
        referenceKind.SelectedIndex = 0;
        referenceKind.SelectedIndexChanged += (_, _) => UpdateReferenceFiles();
        UpdateReferenceFiles();
        var addRef = new Button { Text = "Добавить к блоку", AutoSize = true, Height = 28 };
        var removeRef = new Button { Text = "Убрать выделенное", AutoSize = true, Height = 28 };
        var restore = new Button { Text = "Восстановить .bak", AutoSize = true, Height = 28 };
        var openBat = new Button { Text = "Открыть BAT", AutoSize = true, Height = 28 };
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(9), WrapContents = true };
        header.Controls.AddRange([
            new Label { Text = "ALT:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, strategyPicker, openBat,
            new Label { Text = "  Блок:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, blockPicker,
            new Label { Text = "  Тип:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, referenceKind,
            new Label { Text = "  Список:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, referenceFile, addRef, removeRef, restore
        ]);
        var hint = new Label { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(10, 7, 10, 0), ForeColor = Color.DimGray,
            Text = "Здесь показано, где выбранный ALT использует каждый список. Добавление вставляет аргумент в выбранный --filter блок; удаление убирает только выделенную ссылку. Перед первым изменением автоматически создаётся .bat.bak. Перезапустите стратегию после изменения." };
        strategyPicker.SelectedIndexChanged += (_, _) => RefreshStrategyReferences();
        addRef.Click += (_, _) => AddStrategyReference();
        removeRef.Click += (_, _) => RemoveStrategyReference();
        restore.Click += (_, _) => RestoreStrategyBackup();
        openBat.Click += (_, _) => { if (SelectedStrategyPath() is { } p) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{p}\"") { UseShellExecute = true }); };
        page.Controls.Add(referenceGrid); page.Controls.Add(header); page.Controls.Add(hint);
    }

    void LoadState()
    {
        var bats = Directory.EnumerateFiles(root, "*.bat", SearchOption.TopDirectoryOnly)
            .Where(IsZapretStrategy)
            .OrderBy(p => NaturalKey(Path.GetFileName(p))).ToArray();
        strategies.Items.AddRange(bats.Select(Path.GetFileName).ToArray()!);
        strategyPicker.Items.AddRange(bats.Select(Path.GetFileName).ToArray()!);
        if (strategies.Items.Count > 0) strategies.SelectedIndex = Math.Max(0, Array.FindIndex(bats, p => Path.GetFileName(p) == config.LastStrategy));
        if (strategyPicker.Items.Count > 0) strategyPicker.SelectedIndex = Math.Max(0, Array.FindIndex(bats, p => Path.GetFileName(p) == config.LastStrategy));
        var checkFile = Path.Combine(listsDir, "check_lists.txt");
        if (!File.Exists(checkFile)) File.WriteAllLines(checkFile, DefaultDomains(), new UTF8Encoding(false));
        domains.Lines = File.ReadAllLines(checkFile);

        string gf = Path.Combine(utilsDir, "game_filter.enabled");
        gameMode.SelectedIndex = !File.Exists(gf) ? 0 : File.ReadAllText(gf).Trim().ToLowerInvariant() switch { "all" => 1, "tcp" => 2, _ => 3 };
        updates.Checked = File.Exists(Path.Combine(utilsDir, "check_updates.enabled"));
        ipsetMode.SelectedIndex = DetectIpsetMode();
        var fakeFiles = Directory.EnumerateFiles(Path.Combine(root, "bin"), "*.bin").Where(p => !Path.GetFileName(p).StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase)).OrderBy(Path.GetFileName).ToArray();
        discordFake.Items.AddRange(fakeFiles.Select(Path.GetFileName).ToArray()!); gameFake.Items.AddRange(fakeFiles.Select(Path.GetFileName).ToArray()!);
        SelectActiveFake(discordFake, "ACTIVE_DISCORD_UDP.bin", fakeFiles); SelectActiveFake(gameFake, "ACTIVE_GAME_UDP.bin", fakeFiles);
        listPicker.SelectedIndex = 1;
    }

    static string NaturalKey(string s) => Regex.Replace(s, @"\d+", m => m.Value.PadLeft(10, '0'));
    static bool IsZapretStrategy(string path)
    {
        try
        {
            return Regex.IsMatch(File.ReadAllText(path), "start\\s+\"[^\"]+\"\\s+/min\\s+\"%BIN%winws\\.exe\"", RegexOptions.IgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    sealed record ListFile(string Title, string FileName, bool UserEditable)
    {
        public override string ToString() => Title;
    }
    ListFile? SelectedListFile() => listPicker.SelectedItem as ListFile;
    string? SelectedListPath() => SelectedListFile() is { } f ? Path.Combine(listsDir, f.FileName) : null;
    void LoadSelectedList()
    {
        if (SelectedListPath() is not { } path) return;
        listEditor.Text = File.Exists(path) ? File.ReadAllText(path) : "";
    }
    void SaveSelectedList()
    {
        if (SelectedListPath() is not { } path) return;
        File.WriteAllText(path, listEditor.Text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(false));
        Log($"Сохранён {Path.GetFileName(path)}.");
        status.Text = $"Сохранён {Path.GetFileName(path)}. Перезапустите стратегию.";
    }
    void AddDomainToList()
    {
        string raw = addDomain.Text.Trim();
        if (raw.Length == 0) return;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)) raw = uri.Host;
        raw = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(raw) == UriHostNameType.Unknown)
        {
            MessageBox.Show("Введите доменное имя или IP-адрес.", "Некорректная цель", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        var lines = listEditor.Lines.ToList();
        if (!lines.Any(x => string.Equals(x.Trim(), raw, StringComparison.OrdinalIgnoreCase))) lines.Add(raw);
        listEditor.Lines = lines.ToArray(); addDomain.Clear();
    }
    void NormalizeList()
    {
        var comments = listEditor.Lines.Where(x => x.TrimStart().StartsWith('#')).Select(x => x.Trim()).ToList();
        var values = listEditor.Lines.Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#'))
            .Select(x => Uri.TryCreate(x, UriKind.Absolute, out var u) ? u.Host : x.TrimEnd('.').ToLowerInvariant())
            .Where(x => Uri.CheckHostName(x) != UriHostNameType.Unknown)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        listEditor.Lines = comments.Concat(values).ToArray();
        status.Text = $"Список приведён в порядок: {values.Count} целей.";
    }
    static string[] DefaultDomains() => ["discord.com", "discord.gg", "discordsays.com", "discordsez.com", "discordstatus.com"];
    string? SelectedBat() => strategies.SelectedItem is string s ? Path.Combine(root, s) : null;
    string? SelectedStrategyPath() => strategyPicker.SelectedItem is string s ? Path.Combine(root, s) : null;
    bool IsStrategyExcluded(string name) => config.ExcludedStrategies.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
    sealed record FilterBlock(int LineIndex, string Label);
    sealed record ListReference(int LineIndex, string Filter, string Kind, string File);
    static readonly Regex FilterRegex = new(@"--filter-(tcp|udp)=([^\s]+)", RegexOptions.IgnoreCase);
    static readonly Regex ListRegex = new("--(?<kind>hostlist|hostlist-exclude|ipset|ipset-exclude)=\\\"%LISTS%(?<file>[^\\\"]+)\\\"", RegexOptions.IgnoreCase);
    void RefreshStrategyReferences()
    {
        referenceGrid.Rows.Clear(); blockPicker.Items.Clear();
        if (SelectedStrategyPath() is not { } path || !File.Exists(path)) return;
        var lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            var filter = FilterRegex.Match(lines[i]); if (!filter.Success) continue;
            string label = $"строка {i + 1}: {filter.Value}";
            blockPicker.Items.Add(new FilterBlock(i, label));
            foreach (Match item in ListRegex.Matches(lines[i]))
            {
                var reference = new ListReference(i, filter.Value, item.Groups["kind"].Value, item.Groups["file"].Value);
                int row = referenceGrid.Rows.Add(filter.Value, reference.Kind, reference.File, i + 1);
                referenceGrid.Rows[row].Tag = reference;
            }
        }
        if (blockPicker.Items.Count > 0) blockPicker.SelectedIndex = 0;
        status.Text = $"{Path.GetFileName(path)}: {referenceGrid.Rows.Count} ссылок на списки.";
    }
    void UpdateReferenceFiles()
    {
        if (referenceKind.SelectedItem is not string kind) return;
        string pattern = kind.StartsWith("ipset", StringComparison.OrdinalIgnoreCase) ? "ipset*.txt" : "list*.txt";
        string? prior = referenceFile.SelectedItem as string;
        referenceFile.Items.Clear();
        referenceFile.Items.AddRange(Directory.EnumerateFiles(listsDir, pattern).Select(Path.GetFileName).OrderBy(x => x).ToArray()!);
        int selected = prior is null ? -1 : referenceFile.FindStringExact(prior);
        if (referenceFile.Items.Count > 0) referenceFile.SelectedIndex = selected >= 0 ? selected : 0;
    }
    void BackupStrategy(string path)
    {
        string backup = path + ".bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
    }
    void AddStrategyReference()
    {
        if (SelectedStrategyPath() is not { } path || blockPicker.SelectedItem is not FilterBlock block || referenceKind.SelectedItem is not string kind || referenceFile.SelectedItem is not string file) return;
        var lines = File.ReadAllLines(path).ToList();
        string argument = $"--{kind}=\"%LISTS%{file}\"";
        if (lines[block.LineIndex].Contains(argument, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("Этот список уже есть в выбранном блоке."); return; }
        BackupStrategy(path);
        var filter = FilterRegex.Match(lines[block.LineIndex]);
        lines[block.LineIndex] = lines[block.LineIndex].Insert(filter.Index + filter.Length, " " + argument);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        RefreshStrategyReferences(); status.Text = $"Добавлен {file} в {block.Label}.";
    }
    void RemoveStrategyReference()
    {
        if (SelectedStrategyPath() is not { } path || referenceGrid.SelectedRows.Count != 1 || referenceGrid.SelectedRows[0].Tag is not ListReference reference) return;
        var lines = File.ReadAllLines(path).ToList();
        string argument = $"--{reference.Kind}=\"%LISTS%{reference.File}\"";
        if (!lines[reference.LineIndex].Contains(argument, StringComparison.OrdinalIgnoreCase)) { RefreshStrategyReferences(); return; }
        BackupStrategy(path);
        lines[reference.LineIndex] = Regex.Replace(lines[reference.LineIndex], Regex.Escape(argument), "", RegexOptions.IgnoreCase).Replace("  ", " ");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        RefreshStrategyReferences(); status.Text = $"Убрана ссылка на {reference.File}.";
    }
    void RestoreStrategyBackup()
    {
        if (SelectedStrategyPath() is not { } path) return;
        string backup = path + ".bak";
        if (!File.Exists(backup)) { MessageBox.Show("Резервной копии ещё нет."); return; }
        if (MessageBox.Show("Вернуть BAT к состоянию до первого изменения в программе?", "Восстановление", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        File.Copy(backup, path, true); RefreshStrategyReferences(); status.Text = "BAT восстановлен из .bak.";
    }
    List<string> GetHosts() => domains.Lines.Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).Select(x => { if (Uri.TryCreate(x, UriKind.Absolute, out var u)) return u.Host; return x.Split('/')[0]; }).Where(x => Uri.CheckHostName(x) != UriHostNameType.Unknown).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    async Task FindFirstAsync()
    {
        if (!ValidateReady()) return;
        int enabledCount = strategies.Items.Cast<string>().Count(x => !IsStrategyExcluded(x));
        if (enabledCount == 0) { MessageBox.Show("Все стратегии исключены из перебора. Верните хотя бы одну через ПКМ."); return; }
        SetRunning(true); runCts = new(); results.Rows.Clear();
        try
        {
            await StopAllWinwsAsync();
            var hosts = GetHosts();
            int current = 0;
            for (int i = 0; i < strategies.Items.Count; i++)
            {
                runCts.Token.ThrowIfCancellationRequested();
                string itemName = (string)strategies.Items[i];
                if (IsStrategyExcluded(itemName)) { Log($"Пропуск: {itemName} исключена из перебора."); continue; }
                current++;
                strategies.SelectedIndex = i;
                string bat = SelectedBat()!; Log($"\r\n[{current}/{enabledCount}] {Path.GetFileName(bat)}");
                if (!await StartStrategyAsync(bat, runCts.Token)) continue;
                await Task.Delay((int)warmup.Value, runCts.Token);
                var probes = await ProbeAllAsync(hosts, runCts.Token); ShowResults(probes);
                int ok = probes.Count(x => x.Ok); Log($"Результат: {ok}/{probes.Count}");
                if (ok == probes.Count)
                {
                    config.LastStrategy = Path.GetFileName(bat); SaveConfig();
                    status.Text = $"Найдена: {config.LastStrategy} — оставлена запущенной";
                    MessageBox.Show(this, $"Все {ok} доменов доступны.\n\n{config.LastStrategy} оставлена запущенной.", "Стратегия найдена", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                await StopOwnedAsync();
            }
            status.Text = "Строго подходящая стратегия не найдена";
            MessageBox.Show(this, "Ни одна стратегия не прошла все проверки. Подробности есть в таблице и журнале.", "Результат", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { status.Text = "Остановлено"; await StopOwnedAsync(); }
        finally { SetRunning(false); }
    }

    async Task TestSelectedAsync(bool keepOnSuccess)
    {
        if (!ValidateReady() || SelectedBat() is not { } bat) return;
        SetRunning(true); runCts = new();
        try
        {
            await StopAllWinwsAsync();
            if (!await StartStrategyAsync(bat, runCts.Token)) return;
            await Task.Delay((int)warmup.Value, runCts.Token);
            var probes = await ProbeAllAsync(GetHosts(), runCts.Token); ShowResults(probes);
            bool all = probes.All(x => x.Ok); status.Text = all ? $"Работает: {Path.GetFileName(bat)}" : $"Не прошла: {Path.GetFileName(bat)}";
            if (!all || !keepOnSuccess) await StopOwnedAsync();
        }
        catch (OperationCanceledException) { status.Text = "Остановлено"; await StopOwnedAsync(); }
        finally { SetRunning(false); }
    }

    async Task TestWithoutStrategyAsync()
    {
        if (GetHosts().Count == 0) { MessageBox.Show("Нет корректных доменов."); return; }
        File.WriteAllLines(Path.Combine(listsDir, "check_lists.txt"), domains.Lines, new UTF8Encoding(false));
        SetRunning(true); runCts = new();
        try
        {
            await StopAllWinwsAsync();
            status.Text = "Проверка прямого подключения без winws...";
            var probes = await ProbeAllAsync(GetHosts(), runCts.Token); ShowResults(probes);
            int ok = probes.Count(x => x.Ok);
            status.Text = $"Без стратегии: {ok}/{probes.Count} доступны";
            Log($"Базовая проверка без стратегии: {ok}/{probes.Count}");
        }
        catch (OperationCanceledException) { status.Text = "Остановлено"; }
        finally { SetRunning(false); }
    }

    async Task CheckPublicIpAsync()
    {
        using var dialog = new Form
        {
            Text = "Публичный IP-адрес",
            StartPosition = FormStartPosition.CenterParent,
            Width = 680,
            Height = 390,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(12, 10, 12, 4),
            Text = "Проверка через несколько бесплатных сервисов.\r\nСовпадающие адреса считаются подтверждёнными."
        };
        var target = new TextBox { Width = 190, PlaceholderText = "чужой IPv4/IPv6" };
        var targetButton = new Button { Text = "Проверить введённый IP", AutoSize = true, Height = 28 };
        var targetPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10, 5, 10, 5), WrapContents = false };
        targetPanel.Controls.Add(new Label { Text = "Чужой IP:", AutoSize = true, Padding = new Padding(0, 5, 5, 0) });
        targetPanel.Controls.Add(target); targetPanel.Controls.Add(targetButton);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        grid.Columns.Add("Source", "Источник");
        grid.Columns.Add("Address", "IP-адрес");
        grid.Columns.Add("State", "Состояние");
        var copy = new Button { Text = "Скопировать IP", AutoSize = true, Height = 32, Enabled = false };
        var close = new Button { Text = "Закрыть", AutoSize = true, Height = 32, DialogResult = DialogResult.OK };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(10, 7, 10, 7), FlowDirection = FlowDirection.RightToLeft };
        footer.Controls.Add(close); footer.Controls.Add(copy);
        dialog.Controls.Add(grid); dialog.Controls.Add(footer); dialog.Controls.Add(targetPanel); dialog.Controls.Add(header);
        grid.SelectionChanged += (_, _) => copy.Enabled = grid.SelectedRows.Count > 0 && grid.SelectedRows[0].Cells[1].Value is string s && IPAddress.TryParse(s, out _);
        copy.Click += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0) return;
            string? value = grid.SelectedRows[0].Cells[1].Value as string;
            if (value is null || !IPAddress.TryParse(value, out _)) return;
            try { Clipboard.SetText(value); status.Text = $"IP скопирован: {value}"; }
            catch (ExternalException) { MessageBox.Show(dialog, "Не удалось поместить адрес в буфер обмена.", "Буфер обмена", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        targetButton.Click += async (_, _) =>
        {
            if (!IPAddress.TryParse(target.Text.Trim(), out var ip))
            {
                MessageBox.Show(dialog, "Введите корректный IPv4 или IPv6 адрес.", "Некорректный IP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            targetButton.Enabled = false;
            try
            {
                grid.Rows.Clear();
                string? reverse = null;
                try { reverse = (await Dns.GetHostEntryAsync(ip)).HostName; } catch { }
                grid.Rows.Add("Локальная проверка", ip.ToString(), reverse is null ? "формат корректен; PTR не найден" : $"формат корректен; PTR: {reverse}");
                foreach (var row in await ReadIpInfoAsync(ip.ToString(), CancellationToken.None))
                {
                    int i = grid.Rows.Add(row.Name, row.Address ?? "—", row.Error);
                    grid.Rows[i].DefaultCellStyle.BackColor = ResultColor(row.Address is not null);
                }
                status.Text = $"Проверен IP: {ip}";
            }
            finally { targetButton.Enabled = true; }
        };
        ApplyThemeTo(dialog);
        dialog.AcceptButton = close;
        dialog.Shown += async (_, _) =>
        {
            publicIpButton.Enabled = false;
            var services = new (string Name, string Url)[]
            {
                ("ipinfo.io", "https://ipinfo.io/ip"),
                ("ipify", "https://api.ipify.org"),
                ("ifconfig.me", "https://ifconfig.me/ip"),
                ("icanhazip", "https://icanhazip.com")
            };
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, (int)timeout.Value + 3)));
                var checks = services.Select(x => ReadPublicIpAsync(x.Name, x.Url, cts.Token)).ToArray();
                var rows = await Task.WhenAll(checks);
                foreach (var row in rows)
                {
                    int i = grid.Rows.Add(row.Name, row.Address ?? "—", row.Address is null ? row.Error : "получен");
                    grid.Rows[i].DefaultCellStyle.BackColor = ResultColor(row.Address is not null);
                }
                var addresses = rows.Where(x => x.Address is not null).Select(x => x.Address!).Distinct().ToArray();
                status.Text = addresses.Length == 0 ? "Публичный IP не получен" : $"Публичный IP: {string.Join(", ", addresses)}";
            }
            finally { publicIpButton.Enabled = true; }
        };
        dialog.ShowDialog(this);
    }

    static async Task<(string Name, string? Address, string Error)> ReadPublicIpAsync(string name, string url, CancellationToken token)
    {
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5), AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretAltFinder/1.0");
            string body = await client.GetStringAsync(url, token);
            foreach (string tokenText in Regex.Split(body, "[\\s\\\"'<>(),;:\\[\\]{}]+"))
                if (IPAddress.TryParse(tokenText.Trim(), out var address))
                    return (name, address.ToString(), "");
            return (name, null, "ответ не содержит IP");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (name, null, "таймаут"); }
        catch (Exception ex) when (ex is HttpRequestException or SocketException) { return (name, null, ex.Message); }
        catch (TaskCanceledException) { return (name, null, "таймаут"); }
    }

    static async Task<List<(string Name, string? Address, string Error)>> ReadIpInfoAsync(string ip, CancellationToken token)
    {
        var services = new (string Name, string Url)[]
        {
            ("ipinfo.io", $"https://ipinfo.io/{Uri.EscapeDataString(ip)}/json"),
            ("ipwho.is", $"https://ipwho.is/{Uri.EscapeDataString(ip)}"),
            ("ip-api.com", $"http://ip-api.com/json/{Uri.EscapeDataString(ip)}?fields=status,message,query,country,org")
        };
        var output = new List<(string, string?, string)>();
        foreach (var service in services)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretAltFinder/1.0");
                using var doc = JsonDocument.Parse(await client.GetStringAsync(service.Url, token));
                var root = doc.RootElement;
                bool failed = root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
                    || root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "fail";
                string detail = failed ? (root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "ошибка сервиса" : "ошибка сервиса") :
                    string.Join(", ", new[] { "country", "org", "city" }.Where(k => root.TryGetProperty(k, out _)).Select(k => $"{k}: {root.GetProperty(k).GetString()}"));
                output.Add((service.Name, failed ? null : ip, failed ? detail : (detail.Length == 0 ? "адрес найден" : detail)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            { output.Add((service.Name, null, ex is TaskCanceledException ? "таймаут" : ex.Message)); }
        }
        return output;
    }

    bool ValidateReady()
    {
        File.WriteAllLines(Path.Combine(listsDir, "check_lists.txt"), domains.Lines, new UTF8Encoding(false));
        if (!File.Exists(Path.Combine(root, "bin", "winws.exe"))) { MessageBox.Show("Не найден bin\\winws.exe. Положите программу в корень сборки Flowseal."); return false; }
        if (strategies.Items.Count == 0 || GetHosts().Count == 0) { MessageBox.Show("Нет стратегий или корректных доменов."); return false; }
        if (ServiceRunning("zapret")) { MessageBox.Show("Служба zapret запущена. Сначала удалите/остановите её через service.bat, иначе тест будет недостоверным.", "Конфликт", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        return true;
    }

    async Task<bool> StartStrategyAsync(string bat, CancellationToken token)
    {
        await StopOwnedAsync();
        var before = Process.GetProcessesByName("winws").Select(p => p.Id).ToHashSet();
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{bat}\"\"") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        psi.Environment["NO_UPDATE_CHECK"] = "1";
        using var p = Process.Start(psi);
        if (p is null) { Log("Не удалось запустить BAT."); return false; }
        await p.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(12), token);
        await Task.Delay(350, token);
        var added = Process.GetProcessesByName("winws").Where(x => !before.Contains(x.Id)).Select(x => x.Id).ToArray();
        ownedPids.AddRange(added);
        if (added.Length == 0) { Log("winws.exe не появился после запуска."); return false; }
        Log($"Запущен winws PID: {string.Join(", ", added)}"); return true;
    }

    async Task<List<ProbeResult>> ProbeAllAsync(List<string> hosts, CancellationToken token)
    {
        int count = (int)attempts.Value; int seconds = (int)timeout.Value;
        // Connect timeout and whole-request timeout are deliberately different.
        // A first TLS connection through WinDivert can fit into the connect limit but
        // need a little longer for HTTP headers. Treating both as one limit caused
        // false negatives (for example discord.com at a 2 second setting).
        int requestSeconds = Math.Max(6, seconds * 2);
        var tasks = hosts.Select(async host =>
        {
            ProbeResult last = new(host, false, null, 0, "нет ответа");
            string addressText;
            try
            {
                var resolved = await Dns.GetHostAddressesAsync(host, token).WaitAsync(TimeSpan.FromSeconds(seconds), token);
                if (resolved.Length == 0) return new ProbeResult(host, false, null, 0, "DNS: адреса не найдены (NXDOMAIN/NO_DATA)");
                addressText = string.Join(", ", resolved.Take(3).Select(x => x.ToString()));
            }
            catch (SocketException ex) { return new ProbeResult(host, false, null, 0, $"DNS: имя не существует или недоступно ({ex.SocketErrorCode})"); }
            catch (TimeoutException) { return new ProbeResult(host, false, null, 0, "DNS: таймаут"); }
            for (int n = 1; n <= count; n++)
            {
                token.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                try
                {
                    using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(seconds), AutomaticDecompression = DecompressionMethods.All };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(requestSeconds) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretAltFinder/1.0");
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/");
                    using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                    last = new(host, true, (int)response.StatusCode, sw.ElapsedMilliseconds, $"IP {addressText}; HTTP {(int)response.StatusCode}, попытка {n}"); break;
                }
                catch (TaskCanceledException) when (!token.IsCancellationRequested) { last = new(host, false, null, sw.ElapsedMilliseconds, $"IP {addressText}; HTTP timeout {requestSeconds} с (connect limit {seconds} с)"); }
                catch (HttpRequestException ex) { last = new(host, false, null, sw.ElapsedMilliseconds, $"IP {addressText}; HTTP: {ex.Message}"); }
            }
            return last;
        });
        return (await Task.WhenAll(tasks)).OrderBy(x => x.Host).ToList();
    }

    void ShowResults(List<ProbeResult> probes)
    {
        results.Rows.Clear();
        foreach (var x in probes) { int i = results.Rows.Add(x.Host, x.Ok ? $"OK ({x.Status})" : "ОШИБКА", $"{x.Milliseconds} мс", x.Detail); results.Rows[i].DefaultCellStyle.BackColor = ResultColor(x.Ok); }
    }

    async Task StopOwnedAsync()
    {
        foreach (int pid in ownedPids.ToArray()) try { var p = Process.GetProcessById(pid); p.Kill(true); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        ownedPids.Clear();
    }

    async Task StopAllWinwsAsync()
    {
        foreach (var p in Process.GetProcessesByName("winws")) try { p.Kill(true); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        ownedPids.Clear(); Log("Все процессы winws остановлены.");
    }

    void ApplySettings()
    {
        Directory.CreateDirectory(utilsDir);
        string gf = Path.Combine(utilsDir, "game_filter.enabled");
        if (gameMode.SelectedIndex == 0) File.Delete(gf); else File.WriteAllText(gf, new[] { "all", "tcp", "udp" }[gameMode.SelectedIndex - 1], new UTF8Encoding(false));
        string update = Path.Combine(utilsDir, "check_updates.enabled"); if (updates.Checked) File.WriteAllText(update, "ENABLED", new UTF8Encoding(false)); else File.Delete(update);
        ApplyIpsetMode(ipsetMode.SelectedIndex);
        CopyFake(discordFake, "ACTIVE_DISCORD_UDP.bin"); CopyFake(gameFake, "ACTIVE_GAME_UDP.bin");
        MessageBox.Show("Настройки сохранены. Перезапустите стратегию для применения.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    int DetectIpsetMode()
    {
        string p = Path.Combine(listsDir, "ipset-all.txt"); if (!File.Exists(p)) return 2;
        var lines = File.ReadLines(p).Where(x => !string.IsNullOrWhiteSpace(x)).Take(2).ToArray();
        return lines.Length == 0 ? 1 : lines.Length == 1 && lines[0].Trim() == "203.0.113.113/32" ? 2 : 0;
    }

    void ApplyIpsetMode(int mode)
    {
        string p = Path.Combine(listsDir, "ipset-all.txt"), backup = p + ".backup";
        int old = DetectIpsetMode(); if (old == mode) return;
        if (mode == 0) { if (File.Exists(backup)) File.Copy(backup, p, true); else throw new InvalidOperationException("Нет ipset-all.txt.backup. Обновите IPSet через service.bat."); }
        else { if (old == 0 && File.Exists(p)) File.Copy(p, backup, true); File.WriteAllText(p, mode == 1 ? "" : "203.0.113.113/32\r\n", new UTF8Encoding(false)); }
    }

    void SelectActiveFake(ComboBox box, string activeName, string[] candidates)
    {
        string active = Path.Combine(root, "bin", activeName); if (!File.Exists(active)) return;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(active)));
        int i = Array.FindIndex(candidates, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) == hash); if (i >= 0) box.SelectedIndex = i;
    }
    void CopyFake(ComboBox box, string activeName) { if (box.SelectedItem is string f) File.Copy(Path.Combine(root, "bin", f), Path.Combine(root, "bin", activeName), true); }
    static bool ServiceRunning(string name) { try { using var p = Process.Start(new ProcessStartInfo("sc.exe", $"query {name}") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }); string s = p!.StandardOutput.ReadToEnd(); p.WaitForExit(); return s.Contains("RUNNING", StringComparison.OrdinalIgnoreCase); } catch { return false; } }
    static void StartVisible(string path) => Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{path}\"\"") { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = true });
    void SetRunning(bool value) { findButton.Enabled = testButton.Enabled = baselineButton.Enabled = runButton.Enabled = !value; stopButton.Enabled = value; status.Text = value ? "Проверка..." : status.Text; }
    void Log(string text) { if (InvokeRequired) { BeginInvoke(() => Log(text)); return; } log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n"); }
    AppConfig LoadConfig() { try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new(); } catch { return new(); } }
    void SaveConfig() { config.TimeoutSeconds = (int)timeout.Value; config.Attempts = (int)attempts.Value; config.WarmupMilliseconds = (int)warmup.Value; Directory.CreateDirectory(utilsDir); File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })); }
}
