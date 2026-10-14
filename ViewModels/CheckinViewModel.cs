using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Views;

namespace TraeTools.ViewModels;

public partial class CheckinViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _todayReward = "--";

    /// <summary>今日是否已签到（驱动"今日已签到"强调与一键签到按钮禁用联动）。</summary>
    [ObservableProperty]
    private bool _checkedInToday;

    /// <summary>一键签到按钮文案：今日已签则显示"今日已签到"。</summary>
    public string CheckinButtonText => CheckedInToday ? "今日已签到 ✓" : "一键签到";

    partial void OnCheckedInTodayChanged(bool value) => OnPropertyChanged(nameof(CheckinButtonText));

    [ObservableProperty]
    private int _streakDays = 0;

    [ObservableProperty]
    private string _memberMultiplier = "×1.0";

    [ObservableProperty]
    private bool _isMember;

    [ObservableProperty]
    private string _memberHint = "非会员：基础签到 150 积分";

    [ObservableProperty]
    private double _progressRatio = 0;

    [ObservableProperty]
    private string _progressText = $"0/{DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)}";

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>是否有状态消息需要显示（空则隐藏反馈区）。</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    /// <summary>标题副标题（动态日期）。</summary>
    [ObservableProperty]
    private string _pageSubtitle = "";

    /// <summary>本月标题（“2026 年 9 月”），随月份动态显示。</summary>
    [ObservableProperty]
    private string _monthTitle = $"{DateTime.Now.Year} 年 {DateTime.Now.Month} 月";

    public ObservableCollection<CalendarDay> CalendarDays { get; } = new();
    public ObservableCollection<CheckinRecord> Records { get; } = new();

    public CheckinViewModel()
    {
        PageSubtitle = DateTime.Today.ToString("yyyy-MM-dd · dddd");

        var cfg = MainViewModel.AppConfig;
        try
        {
            if (cfg != null)
            {
                var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                          ?? cfg.Accounts.FirstOrDefault();
                if (acc != null)
                {
                    IsMember = acc.IsMember;
                    MemberHint = acc.IsMember ? "会员：基础 150 + 连签 50" : "非会员：基础签到 150 积分";
                    MemberMultiplier = acc.IsMember ? "×1.33" : "×1.0";
                }
            }
        }
        catch { /* 保留默认 */ }

        // 真实接入：日历按当月实际签到日期构建，连签天数从历史计算
        AccountHelpers.CleanupLegacyHistoryFiles();   // 顺手清理旧版每账号历史文件（幂等）
        RefreshTodayState();
        ReloadCalendar();
        LoadHistory();
    }

    /// <summary>历史记录目录（唯一真源在 AccountHelpers，这里仅转发）。</summary>
    private static string HistoryDir => AccountHelpers.HistoryDir;

    /// <summary>读取全部历史文件，解析出「日期集合」（用于日历与连签）与「记录行」（用于记录列表）。</summary>
    private List<(DateTime Date, string Line)> ReadAllHistory()
    {
        var result = new List<(DateTime Date, string Line)>();
        try
        {
            lock (HistoryIoLock)   // 与 TryAppendHistory 同锁，避免并发读到半截行
            {
                if (!Directory.Exists(HistoryDir)) return result;
                foreach (var file in Directory.GetFiles(HistoryDir, "history_*.txt"))
                {
                    try
                    {
                        foreach (var line in File.ReadAllLines(file))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var parts = line.Split('|', StringSplitOptions.TrimEntries);
                            if (parts.Length < 1) continue;
                            if (TryParseHistoryDate(line, out var dt))
                                result.Add((dt.Date, line));
                            else
                                result.Add((DateTime.MinValue, line));
                        }
                    }
                    catch { /* 跳过损坏文件 */ }
                }
            }
        }
        catch { /* 目录不可读 */ }
        return result.OrderByDescending(r => r.Date).ToList();
    }

    private HashSet<DateTime> CollectSignedDates()
    {
        var signed = new HashSet<DateTime>();
        var cfg = MainViewModel.AppConfig;
        var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                  ?? cfg?.Accounts.FirstOrDefault();
        if (acc?.LastCheckinDate is DateTime lc) signed.Add(lc.Date);
        if (cfg?.LastCheckinDate is DateTime clc) signed.Add(clc.Date);

        // 优先从 SQLite 读取
        try
        {
            var dbDates = MainViewModel.CheckinDb?.GetSignedDates();
            if (dbDates != null && dbDates.Count > 0)
            {
                foreach (var d in dbDates) signed.Add(d);
                return signed;
            }
        }
        catch { /* 数据库读取失败回退到文本文件 */ }

        foreach (var (date, line) in ReadAllHistory())
        {
            if (date == DateTime.MinValue) continue;
            if (TryParseRecord(line, out var rec) && rec.Type == "签到失败") continue;
            signed.Add(date);
        }
        return signed;
    }

    /// <summary>重建本月签到日历：今天=today，历史/配置中有记录的=checked，其余=empty。</summary>
    private void ReloadCalendar()
    {
        CalendarDays.Clear();
        try
        {
            // 月首工作日偏移补位（周标头：日一...六；Sunday=0..Saturday=6），保证每月1号对齐正确星期列
            var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            for (int i = 0; i < (int)first.DayOfWeek; i++)
                CalendarDays.Add(new CalendarDay { Day = 0, State = "blank" });

            var signed = CollectSignedDates();

            // 当月签到记录按日期分组（用于悬浮提示：当日签到账号及积分）
            var byDate = new Dictionary<DateTime, List<string>>();
            try
            {
                var records = MainViewModel.CheckinDb?.GetRecords(DateTime.Today.Year, DateTime.Today.Month, 500) ?? new();
                foreach (var r in records)
                {
                    if (DateTime.TryParse(r.Date, out var rd))
                    {
                        if (!byDate.TryGetValue(rd.Date, out var lst)) byDate[rd.Date] = lst = new();
                        var name = string.IsNullOrEmpty(r.AccountName) ? "账号" : r.AccountName;
                        lst.Add($"{name} +{(int)r.Credits}");
                    }
                }
            }
            catch { /* 悬浮数据失败不影响日历 */ }

            int days = DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month);
            for (int day = 1; day <= days; day++)
            {
                var d = new DateTime(DateTime.Today.Year, DateTime.Today.Month, day);
                string state;
                if (signed.Contains(d.Date)) state = "checked";          // 已签（含今日已签）→ 蓝色
                else if (d.Date == DateTime.Today) state = "today";      // 今天未签 → 今日高亮
                else state = "empty";
                var item = new CalendarDay { Day = day, State = state, Date = d };
                if (byDate.TryGetValue(d.Date, out var list) && list.Count > 0)
                    item.ToolTipText = $"{d:yyyy-MM-dd}\n" + string.Join("\n", list);
                else
                    item.ToolTipText = signed.Contains(d.Date) ? $"{d:yyyy-MM-dd}\n已签到" : $"{d:yyyy-MM-dd}\n未签到";
                CalendarDays.Add(item);
            }

            // 连签天数：从今天（或昨天）向前连续计数
            var signedDates = signed;
            int streak = 0;
            var cursor = DateTime.Today;
            if (!signedDates.Contains(cursor.Date)) cursor = cursor.AddDays(-1);
            while (signedDates.Contains(cursor.Date))
            {
                streak++;
                cursor = cursor.AddDays(-1);
            }
            StreakDays = streak;

            // 月进度
            int signedCount = signedDates.Count(d => d.Year == DateTime.Today.Year && d.Month == DateTime.Today.Month);
            ProgressText = $"{signedCount}/{days}";
            ProgressRatio = days > 0 ? (double)signedCount / days : 0;
        }
        catch { /* 失败保持空日历 */ }
    }

    /// <summary>读取签到历史列表（优先 SQLite，回退文本文件；无数据时用示例）。</summary>
    private void LoadHistory()
    {
        Records.Clear();

        // 优先从 SQLite 读取
        try
        {
            var dbRecords = MainViewModel.CheckinDb?.GetRecords(limit: 50);
            if (dbRecords != null && dbRecords.Count > 0)
            {
                foreach (var r in dbRecords)
                {
                    Records.Add(new CheckinRecord
                    {
                        Date = $"{r.Date} {r.Time}",
                        Account = r.AccountName,
                        Type = "每日签到",
                        Result = $"+{(int)r.Credits}",
                    });
                }
                return;
            }
        }
        catch { /* 数据库读取失败回退到文本文件 */ }

        var all = ReadAllHistory().Where(r => r.Date != DateTime.MinValue).ToList();
        if (all.Count == 0) return;
        foreach (var (_, line) in all.Take(50))
        {
            if (!TryParseRecord(line, out var rec)) continue;
            Records.Add(rec);
        }
    }

    /// <summary>解析历史日期：兼容新版管道格式（首段即日期）与旧版空格格式（yyyy-MM-dd HH:mm 开头）。</summary>
    private static bool TryParseHistoryDate(string line, out DateTime date)
    {
        var pipe = line.Split('|', StringSplitOptions.TrimEntries);
        if (pipe.Length >= 1 && DateTime.TryParse(pipe[0], out date)) return true;
        var m = System.Text.RegularExpressions.Regex.Match(line,
            @"^\s*(\d{4}-\d{2}-\d{2})\s+(\d{1,2}:\d{2})");
        if (m.Success && DateTime.TryParse(m.Groups[1].Value + " " + m.Groups[2].Value, out date)) return true;
        date = DateTime.MinValue;
        return false;
    }

    /// <summary>
    /// 解析一条历史记录为展示模型。兼容：
    /// 新版管道格式 "yyyy-MM-dd HH:mm | 账号 | 类型 | +N"；
    /// 旧版空格格式 "2026-09-08 16:38  [账号 8D21]  签到成功  +150 积分"。
    /// </summary>
    private static bool TryParseRecord(string line, out CheckinRecord rec)
    {
        rec = new CheckinRecord();
        var parts = line.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length >= 4)
        {
            // 过滤早期版本遗留的 +0 空记录
            if (string.Equals(parts[3], "+0", StringComparison.OrdinalIgnoreCase)) return false;
            rec.Date = parts[0];
            rec.Account = parts[1];
            rec.Type = parts[2];
            rec.Result = parts[3];
            return !string.IsNullOrEmpty(parts[0]);
        }
        // 旧版空格格式
        var m = System.Text.RegularExpressions.Regex.Match(line,
            @"^\s*(\d{4}-\d{2}-\d{2})\s+(\d{1,2}:\d{2})\s*\[([^\]]+)\].*?([+-]?\d+(?:\.\d+)?)\s*积分");
        if (!m.Success) return false;
        rec.Date = $"{m.Groups[1].Value} {m.Groups[2].Value}";
        rec.Account = m.Groups[3].Value.Trim();
        rec.Type = "每日签到";
        rec.Result = m.Groups[4].Value.StartsWith("+") || m.Groups[4].Value.StartsWith("-")
            ? m.Groups[4].Value
            : "+" + m.Groups[4].Value;
        return true;
    }

    /// <summary>
    /// 自动签到检查：到点、启用且当天未签到时，对每个 Enabled 账号逐一执行签到。
    /// </summary>
    public async Task<bool> TryAutoCheckinAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || !cfg.AutoCheckinEnabled) return false;
            if (string.IsNullOrWhiteSpace(cfg.AutoCheckinTime)) return false;
            if (!TimeSpan.TryParse(cfg.AutoCheckinTime, out var due)) return false;
            if (DateTime.Now.TimeOfDay < due) return false;
            if (_lastAutoCheckDate == DateTime.Today) return false;
            if (MainViewModel.CheckinApi == null) return false;
            _lastAutoCheckDate = DateTime.Today;  // 到点时置位，本日不再重复触发

            RebuildCheckList();   // 勾选与账号保持同步（新增账号也能进入自动签到）
            var (any, results) = await CheckinAllAccountsAsync();
            if (any) { StatusMessage = "自动签到完成 ✓"; RefreshTodayState(); ReloadCalendar(); LoadHistory(); }
            if (results.Any(r => r.Ok)) await NotifyFeishuBatchAsync(results);   // 全失败不打扰
            return any;
        }
        catch
        {
            return false;
        }
    }



    /// <summary>自动签到结束后，把本轮多账号结果批量推送到飞书（对齐 TraeCheckin.NotifyFeishuBatchAsync）。</summary>
    private async Task NotifyFeishuBatchAsync(List<(string Name, bool Ok, double Gained, string Reason)> results)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || string.IsNullOrWhiteSpace(cfg.FeishuWebhook) || results.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Trae 多账号签到结果");
            foreach (var r in results)
            {
                sb.AppendLine((r.Ok ? "✅ " : "⚠️ ") + r.Name + (r.Ok ? $"  获得 {r.Gained:0} 积分" : $"  失败：{r.Reason}"));
            }
            await TraeCheckin.FeishuNotifier.SendTextAsync(cfg.FeishuWebhook, sb.ToString());
        }
        catch { /* 推送失败不影响签到 */ }
    }

    /// <summary>
    /// 对齐 TraeCheckin.DoCheckinAsync：遍历全部 Enabled 账号签到，单账号失败不阻断。
    /// 返回是否至少成功一个 + 各账号结果（供汇总文案/飞书推送）。
    /// </summary>
    private async Task<(bool Any, List<(string Name, bool Ok, double Gained, string Reason)> Results)> CheckinAllAccountsAsync()
    {
        var cfg = MainViewModel.AppConfig;
        var api = MainViewModel.CheckinApi;
        var results = new List<(string Name, bool Ok, double Gained, string Reason)>();
        bool any = false;
        if (cfg == null || api == null) return (false, results);

        AccountHelpers.CheckinLog("", $"===== 批量签到开始，共 {cfg.Accounts.Count(a => a.Enabled)} 个启用账号 =====");
        int idx = 0;
        foreach (var acc in cfg.Accounts.Where(a => a.Enabled))
        {
            var display = string.IsNullOrEmpty(acc.Name)
                ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id)
                : acc.Name!;

            // 账号间间隔：从配置读取基准秒数，附加 ±2 秒随机抖动
            if (idx++ > 0)
            {
                int baseSec = cfg.CheckinIntervalSeconds > 0 ? cfg.CheckinIntervalSeconds : 5;
                int jitter = Random.Shared.Next(-2000, 2001);
                int delay = Math.Max(1000, baseSec * 1000 + jitter);
                AccountHelpers.CheckinLog(display, $"等待 {delay}ms 后签到下一个账号");
                await Task.Delay(delay);
            }

            AccountHelpers.EnsureDeviceId(acc);
            AccountHelpers.CheckinLog(display, $"开始签到，DeviceId={acc.DeviceId}，Token={(string.IsNullOrEmpty(acc.Token) ? "无" : "有")}，IsMember={acc.IsMember}");

            // 未登录：计入失败汇总并给出原因（失败详情已由 checkin_log 记录）
            if (string.IsNullOrEmpty(acc.Token))
            {
                AccountHelpers.CheckinLog(display, "跳过：未登录（Token 为空）");
                results.Add((display, false, 0, "未登录"));
                continue;
            }
            // 已签（本地记录）：计入成功、不计本日新增积分（历史已有当日记录）
            if (acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today)
            {
                AccountHelpers.CheckinLog(display, $"跳过：本地记录今日已签到（{acc.LastCheckinDate:HH:mm}）");
                results.Add((display, true, 0, "今日已签"));
                continue;
            }
            try
            {
                // token 校验/换新（内部会做 status 探测）
                bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
                if (!valid)
                {
                    AccountHelpers.CheckinLog(display, "Token 校验失败且 Session 换新失败，登录态彻底失效");
                    results.Add((display, false, 0, "会话失效，请重新登录"));
                    continue;
                }
                AccountHelpers.CheckinLog(display, "Token 校验通过");
                // 真实状态判定：已真签（本地漏记）→ 补记并计入成功；未签才执行 claim
                var st = await api.GetStatusAsync(acc.Token ?? "", acc.DeviceId);
                if (st is { } s && s.code == 0 && s.checked_in)
                {
                    acc.LastCheckinDate = DateTime.Now;
                    double already = TraeCheckin.CheckinEvaluator.ResolveGainedCredits(s, acc.IsMember);
                    try { cfg.Save(); } catch { /* 忽略 */ }
                    AccountHelpers.CheckinLog(display, $"服务器返回已签到，补记历史，积分={already}");
                    if (already > 0) TryAppendHistory(acc, already);   // 服务器已签而本地漏记 → 补写历史
                    results.Add((display, true, already, "今日已签"));
                    continue;
                }
                if (st != null)
                    AccountHelpers.CheckinLog(display, $"Status 查询返回：code={st.code}, checked_in={st.checked_in}, message={st.message}");
                else
                    AccountHelpers.CheckinLog(display, "Status 查询返回 null（网络异常）");

                var (g, reason) = await CheckinOneAccountAsync(acc);
                if (g > 0) any = true;
                results.Add((display, g > 0, g, reason));
            }
            catch (Exception ex)
            {
                AccountHelpers.CheckinLog(display, $"签到异常：{ex.Message}");
                results.Add((display, false, 0, "网络或接口异常")); // 单账号失败继续下一个
            }
        }
        AccountHelpers.CheckinLog("", $"===== 批量签到结束，成功 {results.Count(r => r.Ok)} / {results.Count} =====");
        return (any, results);
    }

    /// <summary>对指定账号执行一次签到；返回 (本次获得的积分, 失败原因)。失败原因用于界面/推送明确提示。</summary>
    private async Task<(double Gained, string Reason)> CheckinOneAccountAsync(TraeCheckin.TraeAccount acc)
    {
        var cfg = MainViewModel.AppConfig;
        var api = MainViewModel.CheckinApi;
        var displayName = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        if (cfg == null || api == null) return (0, "服务未初始化");
        AccountHelpers.EnsureDeviceId(acc);
        // token 失效则先用 Session 静默换新，避免 claim 因鉴权失败
        bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
        if (!valid || string.IsNullOrEmpty(acc.Token))
        {
            AccountHelpers.CheckinLog(displayName, "Claim 前 Token 校验失败");
            return (0, "会话失效，请重新登录");
        }

        AccountHelpers.CheckinLog(displayName, $"调用 ClaimAsync，DeviceId={acc.DeviceId}");
        var result = await api.ClaimAsync(acc.Token, acc.DeviceId);
        if (result == null || result.code != 0)
        {
            var code = result?.code ?? -1;
            var msg = result?.message ?? "null response";
            var reason = api.LastError;
            if (string.IsNullOrEmpty(reason))
                reason = $"签到失败：{msg}（code={code}）";
            AccountHelpers.CheckinLog(displayName, $"Claim 失败：code={code}, message={msg}, LastError={api.LastError ?? "null"}");
            return (0, reason);
        }
        AccountHelpers.CheckinLog(displayName, $"Claim 成功：code={result.code}, checked_in={result.checked_in}");

        // claim 响应不含本次所得积分（源注释明确），签到成功后再查 status 解析（对齐源 CheckinOneAsync）
        double gained = 0;
        try
        {
            var after = await api.GetStatusAsync(acc.Token, acc.DeviceId);
            gained = TraeCheckin.CheckinEvaluator.ResolveGainedCredits(after ?? result, acc.IsMember);
            if (after != null)
                AccountHelpers.CheckinLog(displayName, $"签到后 Status 查询：code={after.code}, credits={after.credits}, extra_credits={after.extra_credits}, checked_in={after.checked_in}，解析 gained={gained}");
        }
        catch (Exception ex) { AccountHelpers.CheckinLog(displayName, $"签到后 Status 查询异常：{ex.Message}"); }

        if (gained <= 0)
            AccountHelpers.CheckinLog(displayName, "签到成功但本次积分解析为 0（如实记录，不虚构）");

        acc.LastCheckinDate = DateTime.Now;
        if (acc.Id == cfg.ActiveAccountId)
        {
            cfg.LastCheckinDate = DateTime.Now;
            TodayReward = "+" + (int)gained;
        }
        try
        {
            var credits = await api.GetRemainingCreditsAsync(acc.Token, acc.DeviceId);
            if (credits >= 0 && string.IsNullOrEmpty(api.LastError))
            {
                acc.RemainingCredits = credits;   // 按账号持久化
                cfg.LastRemaining = credits;
            }
        }
        catch { /* 积分刷新失败不影响 */ }
        try { cfg.Save(); } catch { /* 忽略 */ }
        TryAppendHistory(acc, gained);
        AccountHelpers.CheckinLog(displayName, $"签到完成，获得 {gained} 积分，剩余 {cfg.LastRemaining}");
        return (gained, "");
    }

    /// <summary>历史读写锁（唯一真源在 AccountHelpers，这里仅转发）。</summary>
    private static object HistoryIoLock => AccountHelpers.HistoryIoLock;

    /// <summary>签到页账号勾选列表（与 TraeAccount.Enabled 双向同步，勾谁签谁）。</summary>
    public ObservableCollection<AccountCheckItem> CheckAccounts { get; } = new();

    /// <summary>按当前账号重建勾选列表：新增的补上、删除的移除、显示名刷新，勾选状态保留。</summary>
    private void RebuildCheckList()
    {
        if (MainViewModel.AppConfig?.Accounts is not { } accounts) return;
        var ids = accounts.Select(a => a.Id).ToHashSet();
        foreach (var item in CheckAccounts.Where(i => !ids.Contains(i.Id)).ToList())
            CheckAccounts.Remove(item);
        foreach (var acc in accounts)
        {
            var display = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
            var existing = CheckAccounts.FirstOrDefault(i => i.Id == acc.Id);
            if (existing == null)
            {
                var item = new AccountCheckItem { Id = acc.Id, Display = display, IsChecked = acc.Enabled };
                item.OnChanged = c =>
                {
                    var a = MainViewModel.AppConfig?.Accounts.FirstOrDefault(x => x.Id == c.Id);
                    if (a == null || a.Enabled == c.IsChecked) return;
                    a.Enabled = c.IsChecked;
                    try { MainViewModel.AppConfig?.Save(); } catch { /* 忽略 */ }
                };
                CheckAccounts.Add(item);
            }
            else existing.Display = display;
        }
    }

    /// <summary>自动签到上次已触发日期（每日仅触发一次）。</summary>
    private static DateTime _lastAutoCheckDate = DateTime.MinValue;

    /// <summary>把签到结果写入本地历史文件（统一走 AccountHelpers，格式 date | name | type | 结果）。</summary>
    private void TryAppendHistory(TraeCheckin.TraeAccount acc, double gained)
        => AccountHelpers.AppendHistory(acc, gained);

    /// <summary>今日奖励 + 今日已签状态：跨所有账号统计——任一账号未签即可一键签到，今日奖励为所有账号今日实际获得总积分。</summary>
    private void RefreshTodayState()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var accounts = cfg?.Accounts;
            if (accounts == null || accounts.Count == 0)
            {
                TodayReward = "--";
                CheckedInToday = false;
                return;
            }

            // 一键签到 vs 今日已签：任一账号未签则允许点击（CheckedInToday=false）
            bool allChecked = accounts.All(a => a.LastCheckinDate.HasValue && a.LastCheckinDate.Value.Date == DateTime.Today);
            CheckedInToday = allChecked;

            // 今日奖励：统计所有账号今日实际获得的总积分
            double total = MainViewModel.CheckinDb?.GetTodayTotalCredits() ?? 0;
            TodayReward = total > 0 ? "+" + (int)total : "--";
        }
        catch
        {
            TodayReward = "--";
            CheckedInToday = false;
        }
    }

    /// <summary>账号切换联动：按新激活账号刷新会员/奖励/日历/记录。</summary>
    public void Reload()
    {
        RebuildCheckList();
        try
        {
            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                      ?? cfg?.Accounts.FirstOrDefault();
            if (acc != null)
            {
                IsMember = acc.IsMember;
                MemberHint = acc.IsMember ? "会员：基础 150 + 连签 50" : "非会员：基础签到 150 积分";
                MemberMultiplier = acc.IsMember ? "×1.33" : "×1.0";
                StatusMessage = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today
                    ? "今日已签到"
                    : "";
            }
            RefreshTodayState();
            ReloadCalendar();
            LoadHistory();
        }
        catch { /* 保留原值 */ }
    }

    /// <summary>弹出登录窗口；登录成功返回 true。未登录时一键签到、手动登录共用。</summary>
    private async Task<bool> PromptLoginAsync(TraeCheckin.TraeAccount? acc)
    {
        try
        {
            var owner = UiHost.MainWindow;
            if (owner == null) return false;
            var dlg = new LoginWindow(acc);
            return await OverlayDialog.ShowAsync<bool>(dlg, owner);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 一键签到（对齐 TraeCheckin.DoCheckinAsync）：为全部 Enabled 账号签到。
    /// 存在未登录账号时先弹登录窗；完成后汇总文案与飞书推送，并同步仪表盘/账号状态。
    /// </summary>
    [RelayCommand]
    private async Task DoCheckin()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || MainViewModel.CheckinApi == null)
            {
                StatusMessage = "服务未初始化";
                return;
            }
            RebuildCheckList();   // 勾选与账号保持同步（含刚添加的账号）
            if (cfg.Accounts.Count == 0)
            {
                StatusMessage = "没有可用账号，请先在「设置」中添加";
                return;
            }

            // 存在未登录账号：先弹登录窗（对齐源：登录取消则中止，登录成功继续）
            var firstNoToken = cfg.Accounts.FirstOrDefault(a => a.Enabled && string.IsNullOrEmpty(a.Token));
            if (firstNoToken != null)
            {
                StatusMessage = "检测到未登录账号，正在打开登录窗口…";
                bool logged = await PromptLoginAsync(firstNoToken);
                if (!logged)
                {
                    StatusMessage = "已取消登录";
                    return;
                }
                StatusMessage = "登录成功，正在为所有启用账号签到…";
            }

            var (any, results) = await CheckinAllAccountsAsync();
            int ok = results.Count(r => r.Ok);
            double total = results.Sum(r => r.Gained);
            string summary = results.Count == 0
                ? "今日所有账号均已签到"
                : $"共 {results.Count} 个账号，成功 {ok} 个，获得 {total:0} 积分";
            var fails = results.Where(r => !r.Ok).ToList();
            if (fails.Count > 0)
                summary += "；失败：" + string.Join("、", fails.Select(f => $"{f.Name}：{f.Reason}"));
            StatusMessage = summary;

            RefreshTodayState();
            ReloadCalendar();
            LoadHistory();
            if (results.Count > 0) await NotifyFeishuBatchAsync(results);
            if (any) await MainViewModel.NotifyAsyncRefresh();
        }
        catch (Exception ex)
        {
            StatusMessage = $"签到异常：{ex.Message}";
        }
    }
}
















