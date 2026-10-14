using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Input.Platform;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Services;
using TraeTools.Services.Avatar;
using TraeTools.Views;

namespace TraeTools.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _autoCheckinEnabled = true;

    [ObservableProperty]
    private string _autoCheckinTime = "08:00";

    [ObservableProperty]
    private int _checkinIntervalSeconds = 5;

    [ObservableProperty]
    private bool _autoStartEnabled;

    [ObservableProperty]
    private bool _minimizeToTray = true;

    [ObservableProperty]
    private string _tokenString = "（未登录）";

    /// <summary>Token 仅展示首尾各 8 位，中间打码；过短全打码（避免 UI 明文泄露）。</summary>
    private static string MaskToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "（无 Token）";
        if (token.Length <= 20) return new string('*', 8);
        return token[..8] + "…" + token[^8..];
    }

    [ObservableProperty]
    private string _tokenUpdateTime = "—";

    /// <summary>选中账号的设备号（x-device-id，16 位数字；风控关键）。</summary>
    [ObservableProperty]
    private string _deviceIdText = "—";

    [ObservableProperty]
    private string _feishuWebhook = "";

    [ObservableProperty]
    private bool _pushEnabled = true;

    [ObservableProperty]
    private string _pushStatus = "";

    /// <summary>底部状态条是否显示（非空状态消息时才显示，消息自动消失后隐藏）。</summary>
    [ObservableProperty]
    private bool _showStatusBar;

    private CancellationTokenSource? _statusClearCts;

    /// <summary>状态消息自动消失：非空显示，5 秒后若无新消息则清空隐藏；新消息会重置计时。</summary>
    partial void OnPushStatusChanged(string value)
    {
        ShowStatusBar = !string.IsNullOrEmpty(value);
        _statusClearCts?.Cancel();
        _statusClearCts = null;
        if (string.IsNullOrEmpty(value)) return;
        var cts = new CancellationTokenSource();
        _statusClearCts = cts;
        _ = AutoClearStatusAsync(cts.Token);
    }

    private async Task AutoClearStatusAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        catch (TaskCanceledException) { return; }
        if (!ct.IsCancellationRequested) PushStatus = "";
    }

    /// <summary>是否正在进行「检查更新」（防止并发下载/安装）。</summary>
    [ObservableProperty]
    private bool _updateChecking;

    private int _lastUpdatePct = -1;

    public ObservableCollection<AccountInfo> Accounts { get; } = new();

    /// <summary>账号管理中当前选中的账号（联动 Token 面板显示对应凭证）。</summary>
    [ObservableProperty]
    private AccountInfo? _selectedAccount;

    // 关于区块属性
    public string OriginalRepoUrl => "https://github.com/star620/TraeTools";
    public string ForkRepoUrl => "https://github.com/star620/TraeTools";
    public string IssuesUrl => "https://github.com/star620/TraeTools/issues";
    public string AppVersion => TraeTools.Services.AppVersion.Display;

    public SettingsViewModel()
    {
        // 从 AutoStartManager 读取开机自启状态
        try
        {
            AutoStartEnabled = AutoStartManager.IsEnabled();
        }
        catch
        {
            AutoStartEnabled = false;
        }

        // 从 AppConfig 读取飞书 webhook / 到点签到 / 最小化到托盘等设置
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg != null)
            {
                if (!string.IsNullOrEmpty(cfg.FeishuWebhook))
                    FeishuWebhook = cfg.FeishuWebhook;
                AutoCheckinEnabled = cfg.AutoCheckinEnabled;
                AutoCheckinTime = cfg.AutoCheckinTime;
                CheckinIntervalSeconds = Math.Clamp(cfg.CheckinIntervalSeconds > 0 ? cfg.CheckinIntervalSeconds : 5, 1, 60);
                MinimizeToTray = cfg.MinimizeToTray;

                var acc = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                          ?? cfg.Accounts.FirstOrDefault();
                if (acc != null && !string.IsNullOrEmpty(acc.Token))
                {
                    TokenString = MaskToken(acc.Token);
                    TokenUpdateTime = acc.TokenUpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未更新";
                }
            }
        }
        catch { /* 保留默认值 */ }

        PopulateAccounts();

        // 默认选中激活账号，Token 面板随之联动
        try
        {
            var cfg = MainViewModel.AppConfig;
            var active = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId) ?? cfg?.Accounts.FirstOrDefault();
            if (active != null)
                SelectedAccount = Accounts.FirstOrDefault(a => a.Id == active.Id);
            if (SelectedAccount == null && Accounts.Count > 0)
                SelectedAccount = Accounts[0];
        }
        catch { /* 保持未选中 */ }
    }

    /// <summary>
    /// 对齐源 _stateMarks：刷新每个账号的会话/签到状态标记（已签到/待签到/需重登/未登录）。
    /// 供账号管理列表显示，与仪表盘当前激活账号保持一致。
    /// </summary>
    public async Task RefreshAccountStatesAsync()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var api = MainViewModel.CheckinApi;
            if (cfg == null || api == null || cfg.Accounts.Count == 0) return;

            foreach (var acc in cfg.Accounts)
            {
                var item = Accounts.FirstOrDefault(a => a.Id == acc.Id);
                if (item == null) continue;

                // 同步当前激活账号高亮（账号切换后实时框出）
                item.IsCurrent = acc.Id == cfg.ActiveAccountId;

                // 账号资料（昵称/脱敏手机号/学生认证）后台刷新并回填展示
                await AccountHelpers.RefreshProfileAsync(acc);
                item.MobileText = acc.MobileMasked ?? "";
                item.IsStudent = acc.IsStudent;
                var accName = string.IsNullOrEmpty(acc.Name)
                    ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
                    : acc.Name;
                if (!string.Equals(item.Name, accName)) item.Name = accName;

                if (acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today)
                {
                    item.Status = "已签到";
                    item.StatusType = "ok";
                    continue;
                }
                if (string.IsNullOrEmpty(acc.Token))
                {
                    item.Status = "未登录";
                    item.StatusType = "warn";
                    continue;
                }
                bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);   // 失效自动换新
                if (!valid)
                {
                    item.Status = "需重登";
                    item.StatusType = "warn";
                    continue;
                }
                bool checkedIn = false;
                var st = await api.GetStatusAsync(acc.Token ?? "", acc.DeviceId);
                if (st is { } s2 && s2.code == 0) checkedIn = s2.checked_in;
                item.Status = checkedIn ? "已签到" : "待签到";
                item.StatusType = checkedIn ? "ok" : "info";
            }
        }
        catch { /* 状态刷新失败保留原样 */ }

        // 顺手把新出现的头像加载进卡片
        LoadAvatars();
    }

    /// <summary>为账号列表卡片异步加载头像（有 AvatarUrl 且未加载过才拉，内存缓存）。</summary>
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

    /// <summary>
    /// 选中账号变化 → Token 面板联动显示该账号的凭证与更新时间；
    /// 同时把该账号设为全局激活账号，仪表盘/签到页随之切换显示。
    /// </summary>
    partial void OnSelectedAccountChanged(AccountInfo? value)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == value?.Id);
            if (acc != null)
            {
                TokenString = MaskToken(acc.Token);
                TokenUpdateTime = acc.TokenUpdatedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
                DeviceIdText = acc.DeviceId;
                // 设置页仅用于查看/编辑该账号，不再切换全局激活账号
            }
            else
            {
                TokenString = "（无账号数据）";
                TokenUpdateTime = "—";
                DeviceIdText = "—";
            }
        }
        catch { /* 联动失败不影响 */ }
    }

    /// <summary>
    /// 手动更换选中账号的设备号（生成全新 16 位数字）。
    /// 设备号被 Trae 风控标记（签到返回 9074「参与用户太多」）时更换即可解除；
    /// 更换后云端自动签到需重新「云端部署」以同步新设备号。
    /// </summary>
    [RelayCommand]
    private void RegenerateDeviceId()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id);
            if (acc == null)
            {
                PushStatus = "请先选择账号";
                return;
            }
            acc.DeviceId = Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();
            try { cfg?.Save(); } catch { /* 保存失败提示仍展示 */ }
            DeviceIdText = acc.DeviceId;
            PushStatus = $"已更换设备号：{acc.DeviceId}（本地生效；云端需重新部署同步）";
        }
        catch (Exception ex)
        {
            PushStatus = "更换失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 一键为全部账号更换全新设备号（#40）：多账号逐个换太麻烦，批量换发规避风控。
    /// 使用 AccountStore.RenewAllDeviceIds 保证每个新号与其它账号互不重复（避免共用触发 9074）。
    /// 换号后云端部署的 TRAE_DEVICE_ID* 需重新「云端部署」同步。
    /// </summary>
    [RelayCommand]
    private async Task RenewAllDeviceIds()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var owner = UiHost.MainWindow;
            if (cfg == null || owner == null || cfg.Accounts.Count == 0)
            {
                PushStatus = cfg == null || cfg.Accounts.Count == 0 ? "暂无账号，请先添加/登录账号" : "主窗口未就绪";
                return;
            }

            var ask = new TraeTools.Views.PromptWindow(
                "一键更换全部设备码",
                $"将为全部 {cfg.Accounts.Count} 个账号重新生成设备码（互不重复）。\n\n" +
                "用于多账号被风控（-9074 参与用户太多）时批量规避。\n" +
                "更换后云端部署需重新「云端部署」同步新的 TRAE_DEVICE_ID*。\n\n是否继续？",
                "全部更换", "取消");
            bool ok = owner != null ? await OverlayDialog.ShowAsync<bool>(ask, owner) : false;
            if (!ok)
            {
                PushStatus = "已取消";
                return;
            }

            var store = new TraeCheckin.AccountStore(cfg);
            int count = store.RenewAllDeviceIds();
            try { cfg.Save(); } catch { /* 保存失败提示仍展示 */ }

            // 联动 Token 面板显示当前选中账号的新设备号
            var sel = cfg.Accounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id);
            if (sel != null) DeviceIdText = sel.DeviceId;

            PushStatus = $"已为全部 {count} 个账号更换新设备码（互不重复）✓";
            AccountHelpers.AppLog("account", "", $"一键换设备码：已更换全部 {count} 个账号");
            MainViewModel.NotifyActiveAccountChanged();
        }
        catch (Exception ex)
        {
            PushStatus = "更换失败：" + ex.Message;
        }
    }

    private void PopulateAccounts()
    {
        // 重载前必须清空，否则追加导致旧账号重复显示
        Accounts.Clear();
        try
        {
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
                    Accounts.Add(new AccountInfo
                    {
                        Id = acc.Id,
                        Name = name,
                        Initial = name.Length > 0 ? name[0].ToString() : "?",
                        Color = colors[idx % colors.Length],
                        Status = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today ? "已签到" : "待签到",
                        StatusType = acc.LastCheckinDate.HasValue && acc.LastCheckinDate.Value.Date == DateTime.Today ? "ok" : "info",
                        MobileText = acc.MobileMasked ?? "",
                        IsStudent = acc.IsStudent,
                        CreatedAt = acc.TokenUpdatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—",
                        IsCurrent = acc.Id == cfg.ActiveAccountId
                    });
                    idx++;
                }
                LoadAvatars();
                return;
            }
        }
        catch { /* 加载失败保持空列表 */ }

        // 无真实账号时不展示示例账号（示例无法被删除，会造成「账号不存在」误导）
    }

    /// <summary>开关变动立即持久化（写注册表），避免不开「保存设置」直接关窗导致状态丢失。#25</summary>
    partial void OnAutoStartEnabledChanged(bool value)
    {
        try { AutoStartManager.SetEnabled(value); } catch { /* 写注册表失败不阻断 */ }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            // 保存开机自启
            AutoStartManager.SetEnabled(AutoStartEnabled);

            // 保存到 AppConfig
            var cfg = MainViewModel.AppConfig;
            if (cfg != null)
            {
                cfg.AutoCheckinEnabled = AutoCheckinEnabled;
                cfg.AutoCheckinTime = AutoCheckinTime;
                cfg.CheckinIntervalSeconds = Math.Clamp(CheckinIntervalSeconds, 1, 60);
                cfg.FeishuWebhook = FeishuWebhook;
                cfg.MinimizeToTray = MinimizeToTray;
                cfg.Save();
                AccountHelpers.AppLog("account", "", $"设置已保存：自动签到={AutoCheckinEnabled}，时间={AutoCheckinTime}，间隔={cfg.CheckinIntervalSeconds}秒，托盘={MinimizeToTray}");
            }

            PushStatus = "已保存 ✓";
        }
        catch
        {
            PushStatus = "保存失败";
        }
    }

    [RelayCommand]
    private async Task CopyToken()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == SelectedAccount?.Id);
            var token = acc?.Token;
            if (string.IsNullOrEmpty(token))
            {
                PushStatus = "当前账号尚未登录，无 Token 可复制";
                return;
            }
            var top = UiHost.MainWindow;
            if (top?.Clipboard is { } cb)
            {
                await cb.SetTextAsync(token);
                PushStatus = "Token 已复制到剪贴板";
            }
            else
            {
                PushStatus = "无法访问剪贴板";
            }
        }
        catch (Exception ex)
        {
            PushStatus = "复制失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private async Task TestPush()
    {
        try
        {
            if (string.IsNullOrEmpty(FeishuWebhook))
            {
                PushStatus = "Webhook 为空";
                return;
            }
            bool ok = await FeishuNotifier.SendTextAsync(FeishuWebhook, "【TraeTools】飞书推送测试消息");
            PushStatus = ok ? "测试成功 ✓" : "测试失败";
        }
        catch
        {
            PushStatus = "测试异常";
        }
    }

    /// <summary>添加/登录账号：打开登录窗口（内嵌 WebView2 自动登录优先），成功后写入配置并更新列表。</summary>
    [RelayCommand]
    private async Task AddAccount()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var owner = UiHost.MainWindow;
            if (cfg == null || owner == null)
            {
                PushStatus = "无法打开登录窗口（配置或主窗口未就绪）";
                return;
            }

            // 新建一个空账号作为登录目标（Token 校验通过后回填）
            var prevActiveId = cfg.ActiveAccountId;
            var acc = new TraeCheckin.TraeAccount();
            cfg.Accounts.Add(acc);
            cfg.ActiveAccountId = acc.Id;
            cfg.Save();

            var dlg = new Views.LoginWindow(acc);
            bool ok = await OverlayDialog.ShowAsync<bool>(dlg, owner);
            if (ok)
            {
                // 同一手机号判重（JWT data.id 跨会话恒定）：重复则拒绝保存本次登录
                try
                {
                    var uid = TraeCheckin.TokenUtils.ParseAccountUid(acc.Token);
                    var store = new TraeCheckin.AccountStore(cfg);
                    var dup = store.FindAccountWithUid(uid, acc.Id);
                    if (dup != null)
                    {
                        cfg.Accounts.Remove(acc);
                        cfg.Save();
                        AccountHelpers.CheckinLog(acc.Id[..6], $"添加账号取消：同一手机号已存在（{dup.Name}）");
                        PushStatus = "该账号已存在（" + dup.Name + "），取消重复添加";
                        PopulateAccounts();
                        return;
                    }
                }
                catch { /* 判重失败不阻断 */ }

                // 新账号登录成功：补发 16 位数字设备号（接口风控要求，避免签到报"参数错误"）
                if (string.IsNullOrWhiteSpace(acc.DeviceId))
                    acc.DeviceId = Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();
                cfg.Save();
                var accName = string.IsNullOrEmpty(acc.Name) ? acc.Id[..6] : acc.Name!;
                AccountHelpers.CheckinLog(accName, $"添加账号成功，DeviceId={acc.DeviceId}，Token 长度={acc.Token?.Length ?? 0}");
                // 立即拉取账号资料（昵称/手机尾号/学生认证），让列表马上展示出真名（force：跳过当日缓存）
                await AccountHelpers.RefreshProfileAsync(acc, force: true);
                PushStatus = "账号登录成功 ✓，Token 已保存";
                PopulateAccounts();
                // 新账号加入后让仪表盘/用量等全局页立即呈现新账号
                MainViewModel.NotifyActiveAccountChanged();
            }
            else
            {
                // 取消：移除刚创建的空账号，并恢复切换前的激活账号
                cfg.Accounts.Remove(acc);
                if (string.IsNullOrEmpty(prevActiveId) || cfg.Accounts.All(a => a.Id != prevActiveId))
                    cfg.ActiveAccountId = cfg.Accounts.FirstOrDefault()?.Id ?? "";
                else
                    cfg.ActiveAccountId = prevActiveId;
                cfg.Save();
                AccountHelpers.CheckinLog("?", "添加账号已取消");
                PushStatus = "已取消添加账号";
            }
        }
        catch (Exception ex)
        {
            PushStatus = "添加账号失败：" + ex.Message;
        }
    }

    /// <summary>删除当前选中账号（AccountStore.Remove 自动复位激活账号，不影响其它账号）。</summary>
    [RelayCommand]
    private void DeleteAccount()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            if (cfg == null || SelectedAccount == null)
            {
                PushStatus = "请先在上方列表选中要删除的账号";
                return;
            }
            var store = new TraeCheckin.AccountStore(cfg);
            if (!store.Remove(SelectedAccount.Id))
            {
                PushStatus = "删除失败：账号不存在";
                return;
            }
            AccountHelpers.CheckinLog(SelectedAccount.Name ?? SelectedAccount.Id[..6], "账号已删除");
            cfg.Save();
            PushStatus = "账号已删除";
            PopulateAccounts();
            if (Accounts.Count > 0)
            {
                var active = cfg.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId) ?? cfg.Accounts.First();
                SelectedAccount = Accounts.FirstOrDefault(a => a.Id == active.Id);
            }
            else
            {
                SelectedAccount = null;
                TokenString = "（未登录或未添加账号）";
                TokenUpdateTime = "—";
            }
            // 账号增删影响全局账号与仪表盘/用量展示，触发统一刷新（不再通过选中切换）
            MainViewModel.NotifyActiveAccountChanged();
        }
        catch (Exception ex)
        {
            PushStatus = "删除账号失败：" + ex.Message;
        }
    }
    [RelayCommand]
    private void OpenOriginalRepo()
    {
        try
        {
            Process.Start(new ProcessStartInfo(OriginalRepoUrl) { UseShellExecute = true });
        }
        catch { /* 忽略 */ }
    }

    [RelayCommand]
    private void OpenForkRepo()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ForkRepoUrl) { UseShellExecute = true });
        }
        catch { /* 忽略 */ }
    }

    [RelayCommand]
    private void OpenIssues()
    {
        try
        {
            Process.Start(new ProcessStartInfo(IssuesUrl) { UseShellExecute = true });
        }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 重置「用户协议与免责声明」的同意状态：清除本程序与 TraeSwitch 的接受标记后退出，
    /// 下次启动会重新弹出协议要求阅读。调用方（UI 按钮）负责二次确认。
    /// </summary>
    [RelayCommand]
    private async Task ResetAgreement()
    {
        var owner = UiHost.MainWindow;
        var ask = new TraeTools.Views.PromptWindow(
            "重置用户协议",
            "将清除「用户协议与免责声明」的同意记录并退出软件。\n下次启动时会要求你重新阅读并同意。\n\n确定重置并退出？",
            "重置并退出", "取消");
        bool ok = owner != null ? await OverlayDialog.ShowAsync<bool>(ask, owner) : false;
        if (!ok)
        {
            PushStatus = "已取消重置";
            return;
        }

        // 清除本程序的 EULA 标记（MainWindow.OnOpenedEulaCheck 读同路径）
        try
        {
            var flag = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TraeTools", "eula_accepted");
            if (File.Exists(flag)) File.Delete(flag);
        }
        catch { /* 删除失败不阻塞退出 */ }

        // 一并重置 TraeSwitch 侧的协议标记
        try
        {
            var s = MainViewModel.SwitchSettings;
            if (s != null)
            {
                s.Data.EulaAccepted = false;
                s.Save();
            }
        }
        catch { /* 忽略 */ }

        try { MainViewModel.AppConfig?.Save(); } catch { /* 忽略 */ }
        PushStatus = "已重置用户协议，即将退出…";
        await Task.Delay(300);
        Environment.Exit(0);
    }

    /// <summary>
    /// 重置软件：清空所有账号、签到历史、日志、数据库、配置等用户数据，恢复到初始安装状态。
    /// 需要两次确认以防止误操作。
    /// </summary>
    [RelayCommand]
    private async Task ResetSoftware()
    {
        var owner = UiHost.MainWindow;

        // ---- 第一次确认：告知影响范围 ----
        var ask1 = new PromptWindow(
            "重置软件",
            "此操作将永久删除以下所有数据：\n\n" +
            "  · 全部账号及登录凭证（Token / Session）\n" +
            "  · 签到历史记录与数据库\n" +
            "  · 用量统计记录\n" +
            "  · 所有调试日志\n" +
            "  · 账号切换配置与登录态备份\n" +
            "  · 所有个性化设置\n\n" +
            "重置后软件将退出，重新启动后回到初始状态。\n\n确定要继续吗？",
            "继续", "取消");
        bool go1 = owner != null ? await OverlayDialog.ShowAsync<bool>(ask1, owner) : false;
        if (!go1) { PushStatus = "已取消重置"; return; }

        // ---- 第二次确认：最终警告 ----
        var ask2 = new PromptWindow(
            "最终确认",
            "最后警告：此操作不可撤销！\n\n所有账号数据、签到记录、设置将被永久删除。\n\n确认执行重置？",
            "确认重置", "取消");
        bool go2 = owner != null ? await OverlayDialog.ShowAsync<bool>(ask2, owner) : false;
        if (!go2) { PushStatus = "已取消重置"; return; }

        // ---- 执行清理 ----
        PushStatus = "正在重置…";
        try
        {
            // 关闭数据库连接
            try { MainViewModel.CheckinDb?.Dispose(); } catch { /* 忽略 */ }

            var root = DataPaths.Root;

            // 删除整个数据根目录
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            // 删除 LocalAppData 下的缓存目录
            var localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TraeTools");
            if (Directory.Exists(localRoot))
            {
                try { Directory.Delete(localRoot, recursive: true); } catch { /* 忽略 */ }
            }

            PushStatus = "重置完成，即将退出…";
            await Task.Delay(500);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            PushStatus = "重置失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 为源仓库点 star：已授权 GitHub 时调用 GitHub API 点赞（star620/TRAE-Checkin）；
    /// 未授权或仓库 owner 本人时打开仓库主页由用户手动点赞。
    /// </summary>
    [RelayCommand]
    private async Task StarSourceRepo()
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var gh = MainViewModel.GitHubApi;
            if (cfg == null || gh == null)
            {
                // 服务未初始化：直接打开仓库主页
                Process.Start(new ProcessStartInfo(OriginalRepoUrl) { UseShellExecute = true });
                return;
            }

            if (string.IsNullOrEmpty(cfg.GitHubToken))
            {
                PushStatus = "尚未授权 GitHub，已打开仓库主页，请手动点赞";
                Process.Start(new ProcessStartInfo(OriginalRepoUrl) { UseShellExecute = true });
                return;
            }

            string login = cfg.GitHubLogin ?? "";
            if (string.IsNullOrEmpty(login))
            {
                login = await gh.GetLoginAsync(cfg.GitHubToken) ?? "";
                if (!string.IsNullOrEmpty(login)) { cfg.GitHubLogin = login; cfg.Save(); }
            }

            if (GitHubApiClient.ShouldSkipStar(login))
            {
                // 仓库 owner 本人无需自赞，打开主页浏览
                PushStatus = "您是该仓库的维护者，无需为自己点赞";
                Process.Start(new ProcessStartInfo(OriginalRepoUrl) { UseShellExecute = true });
                return;
            }

            PushStatus = "正在为源仓库点赞…";
            bool ok = await gh.StarSourceRepoAsync(cfg.GitHubToken, login);
            PushStatus = ok ? "已为源仓库点赞 ★ 感谢支持！" : "点赞失败（可能已点过），请到网页确认";
        }
        catch (Exception ex)
        {
            PushStatus = "点赞失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 检查更新并安装：查询最新版 → 有新版询问 → 下载 → 后台替换并退出。
    /// 结果写入底部状态栏（PushStatus）。
    /// </summary>
    [RelayCommand]
    private async Task CheckUpdate()
    {
        // 联网检查/下载/安装进行中禁止再次触发，避免并发下载相同新版本
        if (UpdateChecking)
        {
            PushStatus = "正在获取或安装更新，请稍候；完成后可再次检查。";
            return;
        }
        UpdateChecking = true;
        try
        {
            PushStatus = "正在检查更新…";
            UpdaterService.ReleaseInfo? rel = null;
            try { rel = await UpdaterService.GetNewerAsync(useCache: false); }
            catch (Exception ex) { PushStatus = "检查更新失败：" + ex.Message; return; }

            if (rel == null)
            {
                PushStatus = $"已是最新版本（{AppVersion}）";
                return;
            }

#if ANDROID
            // Android 无法像桌面端那样替换正在运行的 exe（Zip 整包 + 后台脚本），
            // 改为引导用户到 Release 页下载 APK 覆盖安装。
            var goDownload = await OverlayDialog.ShowAsync<bool>(
                new PromptWindow("发现新版本",
                    $"发现新版本 {rel.Tag}（当前 {AppVersion}，约 {Math.Max(1, (long)(rel.Size / 1048576.0))} MB）。\n\n"
                    + "手机端不支持应用内自动安装，是否现在打开下载页？",
                    "打开下载页", "取消"),
                UiHost.MainWindow);
            if (goDownload)
            {
                TraeTools.Platform.BrowserLauncher.Open(
                    $"https://github.com/{UpdaterService.Repo}/releases/tag/{rel.Tag}");
                PushStatus = "已打开下载页，请下载 APK 后覆盖安装";
            }
            else
            {
                PushStatus = "已取消更新";
            }
            return;
#else
            PushStatus = $"发现新版本 {rel.Tag}";
            var owner = UiHost.MainWindow;
            var ask = new PromptWindow("发现新版本",
                $"发现新版本 {rel.Tag}\n（当前 {AppVersion}，约 {Math.Max(1, (long)(rel.Size / 1048576.0))} MB）\n\n是否下载并自动安装？",
                "下载并安装", "取消");
            bool ok = owner != null ? await OverlayDialog.ShowAsync<bool>(ask, owner) : false;
            if (!ok)
            {
                PushStatus = "已取消更新";
                return;
            }

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe))
            {
                PushStatus = "无法定位当前程序路径，更新中止";
                return;
            }
            var destDir = Path.GetDirectoryName(currentExe)!;

            // 更新包先下到独立临时目录（不污染安装目录），校验通过后再解到安装目录内的暂存目录
            var pkgDir = Path.Combine(Path.GetTempPath(), "TraeTools_update_" + Guid.NewGuid().ToString("N"));
            PushStatus = "开始下载新版…（请稍候）";
            try
            {
                var p = new Progress<int>(pct =>
                {
                    if (pct / 10 != _lastUpdatePct) { _lastUpdatePct = pct / 10; PushStatus = $"下载 {pct}%…"; }
                });
                var pkg = await Task.Run(() => UpdaterService.DownloadAsync(rel.AssetUrl, pkgDir, rel.AssetName, p));

                if (!UpdaterService.VerifySha256(pkg, rel.Sha256Hex))
                {
                    PushStatus = "更新包校验失败（SHA-256 不一致），已中止更新，请稍后重试。";
                    try { Directory.Delete(pkgDir, recursive: true); } catch { /* 清理失败忽略 */ }
                    return;
                }

                string staging;
                if (rel.IsZip)
                {
                    PushStatus = "正在解包更新…";
                    staging = UpdaterService.PreparePackage(pkg, destDir);
                }
                else
                {
                    // 兼容只发单文件 exe 的历史 Release：造一个只含 exe 的暂存目录，走同一套替换逻辑
                    staging = Path.Combine(destDir, ".~update");
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                    Directory.CreateDirectory(staging);
                    File.Move(pkg, Path.Combine(staging, UpdaterService.AppExeName), overwrite: true);
                }

                PushStatus = "下载完成：即将退出并自动替换安装。";
                UpdaterService.ApplyInBackground(staging, destDir);
                // 必须彻底退出进程以释放目标 exe 文件锁，否则后台脚本替换永远失败
                await Task.Delay(400);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                PushStatus = "更新失败：" + ex.Message;
            }
#endif
        }
        catch (Exception ex)
        {
            PushStatus = "更新异常：" + ex.Message;
        }
        finally
        {
            UpdateChecking = false;
        }
    }
}










