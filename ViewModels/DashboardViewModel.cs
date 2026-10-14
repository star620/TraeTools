using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Services.Avatar;
using TraeTools.Views;

namespace TraeTools.ViewModels;

/// <summary>
/// 趋势图数据点（X/Y 为画布坐标）。P2-2：从 DashboardViewModel 的嵌套类型提升为顶层类型——
/// Avalonia 编译绑定（x:DataType）无法引用嵌套类型，提升后 XAML 才能用编译绑定，NativeAOT 下才不留反射绑定。
/// </summary>
public record TrendPoint(string DateLabel, double Credits, double X, double Y, string Tooltip)
{
    /// <summary>悬浮命中区宽度（=相邻点间距，用于连续覆盖无缝隙）。</summary>
    public double StepWidth { get; set; }
    /// <summary>是否为当前悬浮命中的点（高亮参考线用）。</summary>
    public bool IsHovered { get; set; }
}

public partial class DashboardViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _remainingCredits = 0;

    [ObservableProperty]
    private string _todayReward = "--";

    [ObservableProperty]
    private string _checkinStatus = "--";

    /// <summary>是否需要重新登录（CheckinStatus 为"需重登"或"未登录"时显示重新登录入口）。</summary>
    [ObservableProperty]
    private bool _needLogin;

    [ObservableProperty]
    private int _streakDays = 0;

    [ObservableProperty]
    private bool _isMember = false;

    /// <summary>立即签到进行中（防止重复点击产生重复记录/状态错乱）。</summary>
    [ObservableProperty]
    private bool _isQuickChecking;

    /// <summary>是否允许点击「立即签到」（今日已签到/已完成或进行中时禁用）。</summary>
    [ObservableProperty]
    private bool _canQuickCheckin = true;

    partial void OnCheckinStatusChanged(string value) => UpdateCanQuickCheckin();
    partial void OnIsQuickCheckingChanged(bool value) => UpdateCanQuickCheckin();

    /// <summary>重新计算「立即签到」可用性：进行中或今日已签到（已完成/今日已签到）则禁用。</summary>
    private void UpdateCanQuickCheckin()
        => CanQuickCheckin = !IsQuickChecking && !IsTodayDone(CheckinStatus);

    /// <summary>签到状态是否表示"今日已完成"（不可再签）。</summary>
    private static bool IsTodayDone(string s)
        => s is "今日已签到 ✓" or "已完成 ✓";

    [ObservableProperty]
    private string _currentAccount = "未添加账号";

    /// <summary>头部展示的当前激活账号（含头像/名称），null=无账号。</summary>
    [ObservableProperty]
    private Models.AccountInfo? _currentAccountInfo;

    [ObservableProperty]
    private bool _hasCurrentAccount;

    [ObservableProperty]
    private string _dateText = DateTime.Today.ToString("yyyy-MM-dd ddd");

    public ObservableCollection<AccountInfo> Accounts { get; } = new();

    private IList<Point> _linePoints = new List<Point>();
    /// <summary>趋势折线坐标（重建时带变更通知，否则切换账号后折线不刷新）。</summary>
    public IList<Point> LinePoints { get => _linePoints; private set => SetProperty(ref _linePoints, value); }

    private Geometry _fillGeometry = new StreamGeometry();
    public Geometry FillGeometry { get => _fillGeometry; private set => SetProperty(ref _fillGeometry, value); }

    private double _todayX;
    public double TodayX { get => _todayX; private set => SetProperty(ref _todayX, value); }

    private double _todayY;
    public double TodayY { get => _todayY; private set => SetProperty(ref _todayY, value); }

    public ObservableCollection<TrendPoint> TrendPoints { get; } = new();
    public ObservableCollection<string> XAxisLabels { get; } = new();

    /// <summary>趋势点横向间距（用于悬浮命中区连续覆盖，避免悬停漏触发）。</summary>
    public double TrendStepX { get; private set; } = 40;

    /// <summary>当前悬浮命中的趋势点（即时展示冒泡，替代有延迟的原生 ToolTip）。</summary>
    [ObservableProperty]
    private TrendPoint? _trendHover;

    [ObservableProperty]
    private bool _trendHoverVisible;

    /// <summary>即时悬浮：命中设置/移开清除。</summary>
    public void SetTrendHover(TrendPoint? p)
    {
        if (TrendHover is { } prev && !ReferenceEquals(prev, p)) prev.IsHovered = false;
        if (p is not null) p.IsHovered = true;
        TrendHover = p;
        TrendHoverVisible = p != null;
    }

    public const double ChartWidth = 520;
    public const double ChartHeight = 200;

    /// <summary>Y 轴刻度（积分）。</summary>
    [ObservableProperty]
    private int _chartYMax = 0;

    [ObservableProperty]
    private int _chartYMid = 0;

    [ObservableProperty]
    private int _chartYMin = 0;

    /// <summary>总积分历史文件（%APPDATA%\TraeTools\data\credits_total_&lt;accId&gt;.txt，逐账号独立）。</summary>
    private static string TotalHistoryPathFor(string accountId)
        => System.IO.Path.Combine(TraeTools.Services.DataPaths.DataDir, $"credits_total_{accountId}.txt");

    /// <summary>读取某账号总积分历史（按日期升序；文件格式 yyyy-MM-dd,total）。</summary>
    private static List<(DateTime Date, double Total)> ReadTotalHistory(string accountId)
    {
        var list = new List<(DateTime Date, double Total)>();
        try
        {
            var path = TotalHistoryPathFor(accountId);
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(',');
                if (parts.Length == 2 && DateTime.TryParse(parts[0], out var d) && double.TryParse(parts[1], out var v))
                    list.Add((d, v));
            }
        }
        catch { /* 读取失败返回空 */ }
        return list.OrderBy(x => x.Date).ToList();
    }

    /// <summary>当天无记录时追加当前总积分（同时写入 SQLite 与旧版文本文件）。</summary>
    private static void AppendTotalToday(TraeCheckin.TraeAccount acc, double total)
    {
        if (total < 0) return;
        // 写入 SQLite（ON CONFLICT 自动去重）
        try
        {
            MainViewModel.CheckinDb?.InsertSnapshot(acc.Id, total);
        }
        catch { /* 数据库写入失败不影响 */ }
        // 同时写入旧版文本文件（保持兼容）
        try
        {
            var history = ReadTotalHistory(acc.Id);
            if (history.Any(h => h.Date.Date == DateTime.Today)) return;
            var dir = System.IO.Path.GetDirectoryName(TotalHistoryPathFor(acc.Id))!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(TotalHistoryPathFor(acc.Id), $"{DateTime.Today:yyyy-MM-dd},{total:0.##}{Environment.NewLine}");
        }
        catch { /* 记录失败不影响 */ }
    }

    /// <summary>从该账号真实总积分历史重建曲线（近 N 天），并更新 Y 轴刻度。</summary>
    private void BuildChartFromHistory(string accountId)
    {
        // 优先从 SQLite 读取趋势数据
        List<(DateTime Date, double Remaining)> dbTrend = new();
        try
        {
            dbTrend = MainViewModel.CheckinDb?.GetSnapshotTrend(accountId, 14) ?? new();
        }
        catch { /* 数据库读取失败回退到文本文件 */ }

        List<(DateTime Date, double Total)> history;
        if (dbTrend.Count > 0)
        {
            history = dbTrend.Select(t => (t.Date, t.Remaining)).ToList();
        }
        else
        {
            history = ReadTotalHistory(accountId);
        }

        if (history.Count == 0)
        {
            // 无历史：退化为单日当前积分 mock（避免空图）
            double[] single = { RemainingCredits, RemainingCredits };
            LinePoints = ComputeLinePoints(single);
            FillGeometry = ComputeFillGeometry(single);
            TrendPoints.Clear();
            XAxisLabels.Clear();
            XAxisLabels.Add("今日");
            ChartYMax = ChartYMid = ChartYMin = (int)RemainingCredits;
            // 无历史时今日标记同样对齐最后一个数据点，避免落到 (0,0)
            if (LinePoints.Count > 0)
            {
                TodayX = LinePoints[^1].X - 6;
                TodayY = LinePoints[^1].Y - 6;
            }
            return;
        }

        var recent = history.TakeLast(14).ToList();
        double[] values = recent.Select(h => h.Total).ToArray();
        LinePoints = ComputeLinePoints(values);
        FillGeometry = ComputeFillGeometry(values);

        TrendPoints.Clear();
        const double pad = 12;
        double stepX = values.Length > 1 ? (ChartWidth - 2 * pad) / (values.Length - 1) : 0;
        TrendStepX = stepX > 0 ? stepX : ChartWidth;
        double min = values.Min();
        double max = values.Max();
        double range = 1.0 * (max - min == 0 ? 1 : max - min);
        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
            TrendPoints.Add(new TrendPoint(recent[i].Date.ToString("M/d"), values[i], x, y,
                $"{recent[i].Date:yyyy-MM-dd}\n积分：{(int)values[i]}") { StepWidth = TrendStepX });
        }
        XAxisLabels.Clear();
        int labelCount = Math.Max(1, Math.Min(8, recent.Count));
        int span = recent.Count - 1;
        for (int i = 0; i < labelCount; i++)
        {
            int idx = labelCount > 1 ? (int)Math.Round(i * (double)span / (labelCount - 1)) : 0;
            XAxisLabels.Add(idx == recent.Count - 1 ? "今日" : recent[idx].Date.ToString("M/d"));
        }

        ChartYMax = (int)Math.Ceiling(max);
        ChartYMid = (int)Math.Ceiling((max + min) / 2);
        ChartYMin = (int)Math.Floor(min);

        // 今日标记点（历史路径此前未赋值会画到 (0,0)，导致左上角出现白点）
        if (TrendPoints.Count > 0)
        {
            TodayX = TrendPoints[^1].X - 6;
            TodayY = TrendPoints[^1].Y - 6;
        }
    }

    public DashboardViewModel()
    {
        // 填充账号列表（优先真实账号，否则 mock）
        PopulateAccounts();

        // 从 config 读取剩余积分与签到状态
        var cfg = MainViewModel.AppConfig;
        if (cfg != null)
        {
            try
            {
                // 剩余积分按账号持久化：该账号已有记录则优先，旧数据/未刷新过回退全局 LastRemaining
                var accRem = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)?.RemainingCredits;
                if (accRem is >= 0)
                    RemainingCredits = (int)accRem;
                else if (cfg.LastRemaining >= 0)
                    RemainingCredits = (int)cfg.LastRemaining;

                if (cfg.LastCheckinDate.HasValue)
                {
                    CheckinStatus = cfg.LastCheckinDate.Value.Date == DateTime.Today
                        ? "已完成 ✓"
                        : "未签到";
                }

                var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                          ?? cfg.Accounts.FirstOrDefault();
                if (acc != null)
                {
                    CurrentAccount = string.IsNullOrEmpty(acc.Name) ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}" : acc.Name;
                    IsMember = acc.IsMember;
                }
            }
            catch { /* 保留 mock 默认值 */ }
        }

        // 趋势数据：优先读取激活账号的真实总积分历史，无账号或无历史时不生成模拟曲线
        var app = MainViewModel.AppConfig;
        var active = app?.Accounts.FirstOrDefault(a => a.Id == app.ActiveAccountId) ?? app?.Accounts.FirstOrDefault();
        if (active != null)
        {
            BuildChartFromHistory(active.Id);
            if (TrendPoints.Count == 0)
            {
                var values = GenerateTrendValues();
                LinePoints = ComputeLinePoints(values);
                FillGeometry = ComputeFillGeometry(values);
                TodayX = LinePoints[LinePoints.Count - 1].X - 6;
                TodayY = LinePoints[LinePoints.Count - 1].Y - 6;
                BuildTrendPointsAndLabels(values);
            }
        }
    }

    private void PopulateAccounts()
    {
        try
        {
            // 重载前必须清空，否则追加导致旧账号重复显示（含重复高亮）
            Accounts.Clear();

            var cfg = MainViewModel.AppConfig;
            if (cfg != null && cfg.Accounts.Count > 0)
            {
                var colors = new[] { "#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899" };
                int idx = 0;
                foreach (var acc in cfg.Accounts)
                {
                    var name = string.IsNullOrEmpty(acc.Name)
                        ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
                        : acc.Name;
                    var info = new AccountInfo
                    {
                        Id = acc.Id,
                        Name = name,
                        Initial = name.Length > 0 ? name[0].ToString() : "?",
                        Color = colors[idx % colors.Length],
                        Status = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today ? "已签到" : "待签到",
                        StatusType = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today ? "ok" : "info",
                        IsCurrent = acc.Id == cfg.ActiveAccountId
                    };
                    // 点击账号概览卡片 → 全局切换账号（仪表盘/用量页跟随）
                    var accountId = acc.Id;
                    info.SelectCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => SelectAccount(accountId));
                    Accounts.Add(info);
                    // 同步头部展示的当前账号（含头像加载）
                    if (info.IsCurrent)
                    {
                        CurrentAccountInfo = info;
                        HasCurrentAccount = true;
                    }
                    idx++;
                }
                LoadAvatars();
                return;
            }

            // 无账号：清空头部当前账号展示
            CurrentAccountInfo = null;
            HasCurrentAccount = false;
        }
        catch { /* 加载失败保持空列表 */ }
    }

    /// <summary>全局切换账号（仪表盘/用量统计/签到等跟随）。</summary>
    private void SelectAccount(string accountId)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || cfg.ActiveAccountId == accountId) return;
            cfg.ActiveAccountId = accountId;
            try { cfg.Save(); } catch { /* 忽略 */ }
            MainViewModel.NotifyActiveAccountChanged();
        }
        catch { /* 切换失败不影响 */ }
    }

    /// <summary>为账号概览卡片异步加载头像（有 AvatarUrl 且未加载过才拉，内存缓存）。</summary>
    private void LoadAvatars()
    {
        var cfg = MainViewModel.AppConfig;
        if (cfg == null) return;
        foreach (var item in Accounts)
        {
            if (item.HasAvatar) continue;
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == item.Id);
            if (string.IsNullOrEmpty(acc?.AvatarUrl)) continue;
            _ = AvatarLoader.LoadIntoAsync(acc.AvatarUrl, img =>
            {
                if (img != null) item.AvatarImage = img;
            });
        }
    }

    private double[] GenerateTrendValues()
    {
        // 基于当前剩余积分，向前推 13 天的模拟积分（每天 +50）
        double today = RemainingCredits;
        double[] values = new double[14];
        for (int i = 0; i < 14; i++)
        {
            values[i] = today - (13 - i) * 50;
            if (values[i] < 0) values[i] = 0;
        }
        values[13] = today;
        return values;
    }

    private void BuildTrendPointsAndLabels(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        TrendStepX = stepX > 0 ? stepX : ChartWidth;
        double min = values.Min();
        double max = values.Max();

        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / (max - min));
            var date = DateTime.Today.AddDays(i - (values.Length - 1));
            string dateLabel = date.ToString("M/d");
            string tooltip = $"{date:yyyy-MM-dd}\n积分：{(int)values[i]}";
            TrendPoints.Add(new TrendPoint(dateLabel, values[i], x, y, tooltip) { StepWidth = TrendStepX });
        }

        // X 轴标签：约 8 个均匀分布
        int labelCount = 8;
        for (int i = 0; i < labelCount; i++)
        {
            int idx = (int)Math.Round(i * (values.Length - 1) / (double)(labelCount - 1));
            if (idx == values.Length - 1)
                XAxisLabels.Add("今日");
            else
                XAxisLabels.Add(TrendPoints[idx].DateLabel);
        }
    }

    private static IList<Point> ComputeLinePoints(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        double min = values.Min();
        double max = values.Max();
        double range = 1.0 * (max - min == 0 ? 1 : max - min);
        var pts = new List<Point>();
        for (int i = 0; i < values.Length; i++)
        {
            double x = pad + i * stepX;
            double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
            pts.Add(new Point(x, y));
        }
        return pts;
    }

    private static Geometry ComputeFillGeometry(double[] values)
    {
        const double pad = 12;
        double stepX = (ChartWidth - 2 * pad) / (values.Length - 1);
        double min = values.Min();
        double max = values.Max();
        double range = 1.0 * (max - min == 0 ? 1 : max - min);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(pad, ChartHeight - pad), true);
            for (int i = 0; i < values.Length; i++)
            {
                double x = pad + i * stepX;
                double y = pad + (ChartHeight - 2 * pad) * (1 - (values[i] - min) / range);
                ctx.LineTo(new Point(x, y));
            }
            ctx.LineTo(new Point(pad + (values.Length - 1) * stepX, ChartHeight - pad));
            ctx.EndFigure(true);
        }
        return geometry;
    }

    public async Task LoadAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            if (cfg == null || api == null) return;

            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null || string.IsNullOrEmpty(acc.Token)) return;

            var status = await api.GetStatusAsync(acc.Token, acc.DeviceId);
            if (status != null && status.code == 0)
            {
                CheckinStatus = status.checked_in ? "已完成 ✓" : "未签到";
                TodayReward = "+" + (status.credits + (acc.IsMember ? status.extra_credits : 0));
            }
            else
            {
                // 状态接口失败（token 失效/风控）：明确提示重登，不显示误导性的"已完成"
                CheckinStatus = "需重登";
            }

            // 归零守卫：接口失败/未授权时 GetRemainingCreditsAsync 可能返回 0，此时不覆盖现有显示
            var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
            if (credits > 0 || (credits >= 0 && string.IsNullOrEmpty(api.LastError)))
            {
                RemainingCredits = (int)credits;
                acc.RemainingCredits = credits;   // 按账号持久化
                cfg.LastRemaining = credits;
                try { cfg.Save(); } catch { /* 忽略保存失败 */ }
            }
        }
        catch
        {
            // 网络/解析失败时保留 mock 数据，不崩溃
        }
    }

    /// <summary>对齐 TraeCheckin.RefreshAllAsync：按激活账号真实拉取状态/积分/单日奖励。</summary>
    public async Task RefreshAllAsync()
    {
        try
        {
            // 先同步头部当前账号 + 账号概览高亮（重建列表，令 CurrentAccountInfo/IsCurrent 跟随激活账号）
            PopulateAccounts();

            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (acc == null)
            {
                CurrentAccount = "未添加账号";
                CheckinStatus = "未登录";
                return;
            }
            CurrentAccount = string.IsNullOrEmpty(acc.Name)
                ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
                : acc.Name!;
            IsMember = acc.IsMember;
            AccountHelpers.EnsureDeviceId(acc);

            // 账号切换：先从该账号本地积分历史取最近一条，立即同步剩余积分与趋势图，
            // 不必等接口返回（接口慢/失败也不会残留上个账号的旧值）
            var localHis = ReadTotalHistory(acc.Id);
            if (localHis.Count > 0)
            {
                RemainingCredits = (int)localHis[^1].Total;
                acc.RemainingCredits = localHis[^1].Total;   // 按账号持久化
                if (cfg != null) { cfg.LastRemaining = localHis[^1].Total; }
            }
            BuildChartFromHistory(acc.Id);

            double remaining = acc.RemainingCredits >= 0 ? acc.RemainingCredits : RemainingCredits;   // 失败保留旧值，不清零
            TraeCheckin.CheckinStatus? status = null;
            if (api != null && !string.IsNullOrEmpty(acc.Token))
            {
                bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
                if (valid)
                {
                    var st = await api.GetStatusAsync(acc.Token ?? "", acc.DeviceId);
                    if (st != null && st.code == 0) status = st;
                    var r = await api.GetRemainingCreditsAsync(acc.Token ?? "", acc.DeviceId);
                    if (r >= 0 && string.IsNullOrEmpty(api.LastError))
                    {
                        remaining = r;
                        acc.RemainingCredits = r;   // 按账号持久化，切号再切回仍是新积分
                        if (cfg != null) { cfg.LastRemaining = r; try { cfg.Save(); } catch { /* 忽略 */ } }
                    }
                }
            }

            RemainingCredits = (int)remaining;
            if (status != null)
            {
                CheckinStatus = status.checked_in ? "今日已签到 ✓" : "今日可签到";
                NeedLogin = false;
                TodayReward = "+" + (int)(status.credits + (acc.IsMember ? status.extra_credits : 0));
            }
            else
            {
                CheckinStatus = string.IsNullOrEmpty(acc.Token) ? "未登录" : "需重登";
                NeedLogin = true;
            }

            // 记录今日总积分并重建该账号趋势曲线（账号切换后曲线随之同步）
            if (remaining >= 0)
            {
                AppendTotalToday(acc, remaining);
                BuildChartFromHistory(acc.Id);
            }
        }
        catch (Exception ex)
        {
            AccountHelpers.CheckinLog("?", $"[仪表盘刷新] 异常：{ex.Message}");
        }
    }

    /// <summary>重读当前激活账号并刷新展示（账号切换联动用）。</summary>
    public void Reload()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null) return;
            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null) return;
            CurrentAccount = string.IsNullOrEmpty(acc.Name) ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}" : acc.Name;
            IsMember = acc.IsMember;
            Accounts.Clear();
            PopulateAccounts();
        }
        catch { /* 刷新失败保留原值 */ }
    }

    [RelayCommand]
    private void GoToCheckin()
    {
        RaiseNavigateRequested("checkin");
    }

    [RelayCommand]
    private async Task RefreshStatus()
    {
        await LoadAsync();
    }

    /// <summary>重新登录当前激活账号（仪表盘"需重登/未登录"入口）。登录成功后全局刷新。</summary>
    [RelayCommand]
    private async Task Relogin()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var owner = UiHost.MainWindow;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (cfg == null || owner == null || acc == null) return;
            var dlg = new Views.LoginWindow(acc);
            bool ok = await OverlayDialog.ShowAsync<bool>(dlg, owner);
            if (ok)
            {
                try { cfg.Save(); } catch { /* 忽略保存失败 */ }
                await RefreshAllAsync();
                MainViewModel.NotifyActiveAccountChanged();
            }
        }
        catch { /* 登录窗口异常不影响 */ }
    }

    [RelayCommand]
    private async Task QuickCheckin()
    {
        if (IsQuickChecking) return;   // 防重入：禁止重复点击导致重复记录
        IsQuickChecking = true;
        try
        {
            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            if (cfg == null || api == null)
            {
                RaiseNavigateRequested("checkin");
                return;
            }

            var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg.Accounts.FirstOrDefault();
            if (acc == null || string.IsNullOrEmpty(acc.Token))
            {
                RaiseNavigateRequested("checkin");
                return;
            }

            var name = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
            AccountHelpers.CheckinLog(name, $"[仪表盘快签] 开始签到，DeviceId={acc.DeviceId}");
            var result = await api.ClaimAsync(acc.Token, acc.DeviceId);
            if (result != null && result.code == 0)
            {
                CheckinStatus = "已完成 ✓";
                // 非会员 150，会员 150 + 50 连签
                TodayReward = "+" + (int)(result.credits + (acc.IsMember ? result.extra_credits : 0));

                // 同一天已签到过则不重复写历史（避免手动/自动重复产生多条记录）
                bool wasDoneToday = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today;
                acc.LastCheckinDate = DateTime.Now;
                cfg.LastCheckinDate = DateTime.Now;
                // 解析本次所得并写入签到历史（与签到页/自动签到同口径）
                try
                {
                    var after = await api.GetStatusAsync(acc.Token, acc.DeviceId);
                    double gained = TraeCheckin.CheckinEvaluator.ResolveGainedCredits(after ?? result, acc.IsMember);
                    if (!wasDoneToday) AccountHelpers.AppendHistory(acc, gained);
                    AccountHelpers.CheckinLog(name, $"[仪表盘快签] 签到成功，获得 {gained} 积分");
                }
                catch { /* 历史写入失败不影响 */ }
                // 刷新积分
                var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
                if (credits > 0 || (credits >= 0 && string.IsNullOrEmpty(api.LastError)))
                {
                    RemainingCredits = (int)credits;
                    acc.RemainingCredits = credits;   // 按账号持久化
                    cfg.LastRemaining = credits;
                }
                try { cfg.Save(); } catch { /* 忽略 */ }

                // 实时联动：刷新账号概览状态 + 积分趋势曲线（今日积分已变）
                try
                {
                    if (credits >= 0) AppendTotalToday(acc, credits);
                    BuildChartFromHistory(acc.Id);
                }
                catch { /* 联动刷新失败不影响签到 */ }
                PopulateAccounts();
            }
            else
            {
                AccountHelpers.CheckinLog(name, $"[仪表盘快签] Claim 失败：code={result?.code ?? -1}, message={result?.message ?? "null"}");
            }
        }
        catch
        {
            // 签到失败时跳转到签到页
            RaiseNavigateRequested("checkin");
        }
        finally
        {
            IsQuickChecking = false;
        }
    }
}










