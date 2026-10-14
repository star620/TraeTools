using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeSwitch.Services;
using TraeTools.Models;
using TraeTools.Views;

namespace TraeTools.ViewModels;

public partial class SwitchViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _userDirectory = "";

    [ObservableProperty]
    private string _clientName = "—";

    [ObservableProperty]
    private int _carrierCount;

    [ObservableProperty]
    private string _vaultPath = "";

    [ObservableProperty]
    private string _logText = "[守护进程] 已就绪";

    /// <summary>账号总数文案（真实建档数）。</summary>
    [ObservableProperty]
    private string _accountCountText = "0 个账号";

    /// <summary>当前切换是否进行中（防止并发切换）。</summary>
    [ObservableProperty]
    private bool _isSwitching;

    public ObservableCollection<AccountInfo> Accounts { get; } = new();
    public ObservableCollection<SwitchStep> Steps { get; } = new();

    [ObservableProperty]
    private AccountInfo? _selectedAccount;

    public SwitchViewModel()
    {
        Steps.Add(new SwitchStep { Index = 1, Title = "结束客户端", Description = "关闭 Trae 客户端进程", Status = "idle" });
        Steps.Add(new SwitchStep { Index = 2, Title = "快照回滚区", Description = "备份当前账号状态", Status = "idle" });
        Steps.Add(new SwitchStep { Index = 3, Title = "写回 vault", Description = "写入目标账号载体", Status = "idle" });
        Steps.Add(new SwitchStep { Index = 4, Title = "拉起客户端", Description = "启动 Trae 客户端", Status = "idle" });
        Steps.Add(new SwitchStep { Index = 5, Title = "判定结果", Description = "校验登录态", Status = "idle" });

        LoadAccounts();
        EnsureFingerprint();   // 指纹为空/过期时自动重探回填，避免「未配置载体指纹」卡死建档/切换
        ReportAutoFix();       // 旧版 TRAE SOLO CN 配置被自动修正时提示一次
    }

    /// <summary>settings.json 自愈（旧路径/旧进程名/旧指纹）只提示一次，避免用户以为"改了没生效"。</summary>
    private void ReportAutoFix()
    {
        var note = MainViewModel.SwitchSettings?.ConsumeAutoFixNote();
        if (!string.IsNullOrEmpty(note)) AppendLog(note);
    }

    /// <summary>
    /// 载体指纹为空、或**过期**（缺 state.vscdb 及其 -wal/-shm/.backup 边车）时，
    /// 重置为固定 15 项白名单并落盘。
    /// 旧版把"目录扫描出来的窄清单"固化进 settings.json，之后改白名单也不生效（P0-3 根因）；
    /// 这里每次进本页自愈一次，老用户升级后无需手动删配置文件。
    /// </summary>
    private void EnsureFingerprint()
    {
        var settings = MainViewModel.SwitchSettings;
        if (settings?.Data == null) return;
        if (!CarrierDefaults.IsStaleFingerprint(settings.Data.Fingerprint)) return;

        var detected = CarrierDefaults.DetectFingerprint();
        if (detected.Count == 0)
        {
            AppendLog("未检测到客户端登录态载体（可能未安装 Trae CN 客户端），请确认后再建档/切换");
            return;
        }
        if (settings.Data.Fingerprint.Count > 0)
            AppendLog("已固化的载体指纹不完整（缺 state.vscdb 或其边车），已重置为固定白名单");

        settings.Data.Fingerprint.Clear();
        settings.Data.Fingerprint.AddRange(detected);
        try { settings.Save(); } catch { /* 保存失败下次再写 */ }
        AppendLog($"载体指纹已就绪：{detected.Count} 项（当前实际存在 {CarrierDefaults.CountPresent(settings.Data.RootDir)} 项）");
        CarrierCount = settings.Data.Fingerprint.Count;
        LoadAccounts();
    }

    private IClientController? BuildClient()
    {
        var settings = MainViewModel.SwitchSettings;
        if (settings == null) return null;
        return new ClientController(settings.Data.ProcessName, ResolveClientExe(settings.Data.ClientExe));
    }

    /// <summary>
    /// 客户端 exe 解析：配置路径失效（被移动/改名/历史遗留错误值）时，自动回退到自动探测结果，
    /// 避免因 settings.json 里的旧路径把切换/启动卡死（#25）。
    /// </summary>
    private static string ResolveClientExe(string configured)
    {
        try
        {
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;
        }
        catch { /* 校验失败走探测 */ }
        return TraeSwitch.Services.CarrierDefaults.DefaultClientExe;
    }

    /// <summary>当前生效的客户端路径（配置值或探测回退）。</summary>
    [ObservableProperty]
    private string _clientExePath = "";

    /// <summary>
    /// 手动选择客户端 exe（文件对话框），写入 settings.json 的 ClientExe 并持久化。
    /// 覆盖自动探测：适用于绿色版 / 自定义安装目录 / 多版本并存等探测无法命中的场景。
    /// </summary>
    [RelayCommand]
    private async Task BrowseClientExe()
    {
        try
        {
            var win = UiHost.MainWindow;
            if (win == null)
            {
                AppendLog("无法打开文件选择（主窗口未就绪）");
                return;
            }
            var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 Trae 客户端程序（Trae CN.exe）",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("可执行文件 (*.exe)") { Patterns = new[] { "*.exe" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
                },
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                AppendLog("未选择有效文件，已取消");
                return;
            }
            SetClientExe(path, $"已手动设置客户端路径：{path}");
        }
        catch (Exception ex)
        {
            AppendLog("选择客户端失败：" + ex.Message);
        }
    }

    /// <summary>重新自动探测客户端路径并写入配置（忽略之前缓存）。</summary>
    [RelayCommand]
    private void ReDetectClientExe()
    {
        try
        {
            CarrierDefaults.ResetDetection();
            var exe = CarrierDefaults.DefaultClientExe;
            SetClientExe(exe, $"已重新探测客户端路径：{exe}");
        }
        catch (Exception ex)
        {
            AppendLog("重新探测失败：" + ex.Message);
        }
    }

    /// <summary>将客户端路径写入 settings.json 并刷新展示。</summary>
    private void SetClientExe(string path, string log)
    {
        var settings = MainViewModel.SwitchSettings;
        if (settings == null)
        {
            AppendLog("切换服务未初始化，路径未保存");
            return;
        }
        settings.Data.ClientExe = path;
        try { settings.Save(); } catch { /* 保存失败仍展示 */ }
        ClientExePath = path;
        ClientName = Path.GetFileNameWithoutExtension(path);
        AppendLog(log);
    }

    /// <summary>加载账号列表：优先读取 TraeSwitch settings.json 真实账号，逐账号容错。</summary>
    private void LoadAccounts()
    {
        var settings = MainViewModel.SwitchSettings;
        var vault = MainViewModel.Vault;

        if (vault != null)
        {
            VaultPath = TraeTools.Services.DataPaths.VaultDir;
        }

        if (settings == null)
        {
            AddMockAccounts("TraeSwitch 服务未初始化");
            return;
        }

        UserDirectory = settings.Data.RootDir;
        var exe = ResolveClientExe(settings.Data.ClientExe);
        if (!string.Equals(exe, settings.Data.ClientExe, StringComparison.OrdinalIgnoreCase))
            AppendLog($"设置中的客户端路径无效（{settings.Data.ClientExe}），已自动回退到探测路径：{exe}");
        ClientName = Path.GetFileNameWithoutExtension(exe);
        ClientExePath = exe;
        CarrierCount = settings.Data.Fingerprint.Count;

        var accountNames = settings.Data.Accounts;
        if (accountNames == null || accountNames.Count == 0)
        {
            AddMockAccounts("尚未建档任何账号");
            return;
        }

        Accounts.Clear();
        var colors = new[] { "#3B82F6", "#10B981", "#F59E0B", "#8B5CF6", "#EC4899" };
        int idx = 0;

        // 手机号索引：切换页账号名（建档名）→ 脱敏手机号（#14）。
        // 与设置页账号管理同源（TraeAccount.MobileMasked），按备注名/平台昵称忽略大小写匹配。
        var mobileByAccount = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var appAccounts = MainViewModel.AppConfig?.Accounts;
        if (appAccounts != null)
        {
            foreach (var ta in appAccounts)
            {
                if (string.IsNullOrWhiteSpace(ta.MobileMasked)) continue;
                if (!string.IsNullOrWhiteSpace(ta.Name)) mobileByAccount.TryAdd(ta.Name, ta.MobileMasked);
                if (!string.IsNullOrWhiteSpace(ta.ScreenName)) mobileByAccount.TryAdd(ta.ScreenName, ta.MobileMasked);
            }
        }

        foreach (var acc in accountNames)
        {
            idx++;
            try
            {
                VaultInfo? info = null;
                try { info = vault?.GetInfo(acc); } catch { /* 按未建档处理 */ }

                double score = 0;
                try { score = vault?.MatchScore(acc) ?? 0; } catch { /* 评分失败忽略 */ }
                bool isCurrent = score >= 0.75;

                string status;
                string statusType;
                if (isCurrent)
                {
                    status = "当前 ✓";
                    statusType = "ok";
                }
                else if (info != null)
                {
                    status = "已建档";
                    statusType = "info";
                }
                else
                {
                    status = "未建档";
                    statusType = "warn";
                }

                Accounts.Add(new AccountInfo
                {
                    Name = acc,
                    Initial = acc.Length > 0 ? acc[0].ToString() : "?",
                    Color = colors[(idx - 1) % colors.Length],
                    Status = status,
                    StatusType = statusType,
                    MobileText = mobileByAccount.TryGetValue(acc, out var m) ? m : "",
                    CreatedAt = info?.CreatedLocal?.ToString("yyyy-MM-dd HH:mm") ?? "未建档",
                    Carriers = info?.EntryCount ?? 0,
                    Similarity = isCurrent ? "100%" : (info != null ? $"{(int)(score * 100)}%" : "—"),
                    IsCurrent = isCurrent
                });
            }
            catch (Exception ex)
            {
                LogText += $"\n[{DateTime.Now:HH:mm:ss}] 加载账号「{acc}」失败：{ex.Message}";
            }
        }

        if (Accounts.Count == 0)
        {
            AddMockAccounts("账号数据读取失败");
            return;
        }

        AccountCountText = $"{Accounts.Count} 个账号";
        LogText = $"[{DateTime.Now:HH:mm:ss}] 已加载 {Accounts.Count} 个账号档案\n选中目标账号后点击「切换至此」";
    }

    private void AddMockAccounts(string reason)
    {
        Accounts.Clear();
        LogText = $"[{DateTime.Now:HH:mm:ss}] {reason}\n请先在 TraeSwitch 或本页「建档」中备份账号登录态";
    }

    private void AppendLog(string line)
    {
        // 防无界增长（#33）：常驻托盘 7×24 运行，日志只进不出会持续涨内存；超限裁掉头部只留尾部
        const int MaxLogChars = 20_000;
        var text = LogText + $"\n[{DateTime.Now:HH:mm:ss}] {line}";
        if (text.Length > MaxLogChars)
            text = "…（日志已截断，只保留最近部分）\n" + text[^MaxLogChars..];
        LogText = text;
        AccountHelpers.AppLog("switch", "", line);
    }

    private void ResetSteps()
    {
        foreach (var s in Steps) s.Status = "idle";
    }

    [RelayCommand]
    private void RefreshList()
    {
        LoadAccounts();
        AppendLog("账号列表已刷新");
    }

    /// <summary>
    /// 一键冷切换（真实执行）：复用 TraeSwitch SwitcherService 编排——
    /// 结束客户端 → 快照回滚区 → 写回目标账号 vault → 拉起客户端 → 守护判定。
    /// 失败（RolledBack）时服务内部已自动回滚并重启；NeedsConfirm 需要人工确认。
    /// </summary>
    [RelayCommand]
    private async Task SwitchAccount()
    {
        if (IsSwitching) { AppendLog("已在切换中，请等待完成"); return; }
        if (SelectedAccount == null) { AppendLog("请先选择目标账号"); return; }

        var settings = MainViewModel.SwitchSettings;
        var vault = MainViewModel.Vault;
        var client = BuildClient();
        if (settings == null || vault == null || client == null)
        {
            AppendLog("切换服务未初始化（TraeSwitch 配置缺失）");
            return;
        }
        EnsureFingerprint();   // 切前兜底：仍为空则说明确实没有载体，转报明确提示
        if (settings.Data.Fingerprint.Count == 0)
        {
            AppendLog("尚未配置载体指纹，且未检测到客户端登录态载体，无法切换");
            return;
        }

        IsSwitching = true;
        ResetSteps();
        TraeSwitch.Services.SwitchOutcome? outcome = null;
        try
        {
            var name = SelectedAccount.Name;
            var sw = new SwitcherService(settings.Data.RootDir, vault, client);
            AppendLog($"开始切换至 {name}…");
            AppendLog("正在结束客户端并快照回滚区…");
            Steps[0].Status = "progress";
            await Task.Delay(150);
            Steps[0].Status = "ok"; Steps[1].Status = "progress";

            outcome = await sw.SwitchWithGuardAsync(name, settings.Data.Fingerprint,
                progress: elapsed =>
                {
                    // 轮询期间的守护进度，输出到日志
                    int sec = (int)elapsed.TotalSeconds;
                    if (sec >= 4 && sec % 4 == 0)
                        AppendLog($"守护中：等待客户端写入会话…（{sec} 秒 / 最长 20 秒）");
                });

            Steps[1].Status = "ok"; Steps[2].Status = "ok"; Steps[3].Status = "ok"; Steps[4].Status = "progress";

            switch (outcome.Kind)
            {
                case SwitchOutcomeKind.Active:
                    Steps[4].Status = "ok";
                    AppendLog($"切换成功：已进入 {name}（{outcome.Detail}）");
                    SyncActiveAccount(name);
                    // P0-5：切换成功后立即把 live 的 storage.json 回写该账号 vault，
                    // 存下服务端刚轮换的新 refresh token，避免下次切号因旧凭据已作废而 401。
                    if (vault.SyncBackStorageToVault(name, settings.Data.Fingerprint))
                        AppendLog($"已回写「{name}」的登录凭据到 vault（下次切号可复用新会话）");
                    else
                        AppendLog($"提示：未能回写「{name}」的登录凭据到 vault，该账号下次切换可能失效，建议重新登录后「建档」");
                    break;
                case SwitchOutcomeKind.RolledBack:
                    Steps[4].Status = "warn";
                    AppendLog($"切换失败（{outcome.Detail}），已自动回滚到切换前账号并重启客户端");
                    AppendLog("处理：如需切换到该账号，请先在其上登录一次并「建档」更新会话备份");
                    break;
                case SwitchOutcomeKind.NeedsConfirm:
                    Steps[4].Status = "warn";
                    AppendLog($"无法自动判定结果：{outcome.Detail}。");
                    AppendLog($"请人工确认：若界面已进入「{name}」，点「校验」复核；若未进入，请重新切换或先在该账号上登录建档。");
                    break;
            }

            AppendLog("本次切换流程结束");
            LoadAccounts();
        }
        catch (Exception ex)
        {
            AppendLog("切换失败：" + ex.Message);
        }
        finally
        {
            // 仅已在本次流程内完结（成功 或 已自动回滚）才清理快照目录；
            // NeedsConfirm 仍需保留待用户人工"校验/回滚"，否则快照没了会无法恢复旧会话。
            if (outcome is { RollbackDir: { } dir }
                && outcome.Kind is SwitchOutcomeKind.Active or SwitchOutcomeKind.RolledBack)
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* 忽略清理失败 */ }
            }
            IsSwitching = false;
        }
    }

    /// <summary>切换成功（Active）后，把 TraeCheckin 的激活账号同步为目标账号，并刷新账号列表，保证仪表盘/签到页与当前客户端账号一致。</summary>
    private void SyncActiveAccount(string name)
    {
        try
        {
            var cfg = MainViewModel.AppConfig;
            var acc = cfg?.Accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (cfg == null || acc == null)
            {
                AppendLog($"同步激活账号失败：未在本地配置中找到「{name}」");
                return;
            }
            if (cfg.ActiveAccountId == acc.Id) { LoadAccounts(); return; }
            cfg.ActiveAccountId = acc.Id;
            try { cfg.Save(); } catch { /* 保存失败不阻断切换 */ }
            AppendLog($"已同步激活账号为「{name}」");
            MainViewModel.NotifyActiveAccountChanged();
        }
        catch (Exception ex) { AppendLog($"同步激活账号异常：{ex.Message}"); }
    }

    [RelayCommand]
    private void VerifyAccount()
    {
        if (SelectedAccount == null) { AppendLog("请先选择账号"); return; }
        try
        {
            var vault = MainViewModel.Vault;
            double? score = null;
            try { score = vault?.MatchScore(SelectedAccount.Name); } catch { /* 忽略 */ }
            AppendLog(score.HasValue
                ? $"校验 {SelectedAccount.Name}：相似度 {(int)(score.Value * 100)}%"
                : $"校验 {SelectedAccount.Name}：无建档数据");
        }
        catch (Exception ex) { AppendLog("校验异常：" + ex.Message); }
    }

    [RelayCommand]
    private void DeleteAccount()
    {
        if (SelectedAccount == null) { AppendLog("请先选择账号"); return; }
        try
        {
            var settings = MainViewModel.SwitchSettings;
            if (settings != null)
            {
                settings.Data.Accounts.Remove(SelectedAccount.Name);
                settings.Save();
                AppendLog($"已从列表移除「{SelectedAccount.Name}」（vault 目录请手动清理）");
                LoadAccounts();
            }
            else { AppendLog("删除失败：设置服务未初始化"); }
        }
        catch (Exception ex) { AppendLog("删除失败：" + ex.Message); }
    }

    [RelayCommand]
    private async Task CreateProfile()
    {
        EnsureFingerprint();   // 建档前先确保指纹存在，否则只会备份出一个空壳 meta
        var settings = MainViewModel.SwitchSettings;
        var vault = MainViewModel.Vault;
        if (vault == null || settings == null) { AppendLog("建档失败：服务未初始化"); return; }

        // 修复：账号列表已删空时，建档不再卡死——改为弹窗输入新账号名直接建档
        string account = SelectedAccount?.Name ?? "";
        if (string.IsNullOrEmpty(account))
        {
            var owner = UiHost.MainWindow;
            var dlg = new InputWindow(
                "建档新账号",
                "账号列表为空。\n请为当前登录的 Trae 客户输入一个账号名称（用于区分多个账号的登录态备份，如「主号 / 小号」）。已删除的账号如需恢复，用原来的名称即可。",
                "如：主号 / 小号");
            string? name = owner != null ? await OverlayDialog.ShowAsync<string>(dlg, owner) : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                AppendLog("已取消建档（未输入账号名）");
                return;
            }
            account = name.Trim();
            if (settings.Data.Accounts.Contains(account, StringComparer.OrdinalIgnoreCase))
            {
                AppendLog($"账号「{account}」已存在于列表中，请先在列表选择后点击「建档」更新备份");
                LoadAccounts();
                return;
            }
            settings.Data.Accounts.Add(account);
            settings.Save();
            AppendLog($"已新建账号「{account}」，开始建档…");
        }

        try
        {
            // 与 TraeSwitch 一致：建档前若客户端运行则先退出，以解除登录态文件占用
            var client = BuildClient();
            if (client != null && client.IsRunning())
            {
                AppendLog("检测到客户端运行中，正在自动退出（解除登录态文件占用）…");
                // KillAll 内部是 12s 优雅等待 + 8s 强杀等待的 Thread.Sleep 轮询，同步调用会把界面冻住十几秒。
                await Task.Run(() => client.KillAll());
                await Task.Delay(500);
            }

            AppendLog($"开始建档：{account}…");
            int count = await vault.BackupAsync(account, settings.Data.Fingerprint);
            bool ok = await vault.VerifyAsync(account);
            AppendLog($"建档完成：{count} 个载体文件，校验 {(ok ? "通过" : "未通过")}");
            LoadAccounts();
            // 建档后自动选中新账号，用户可直接点「校验」复核，避免误报「无建档数据」（#34）
            var created = Accounts.FirstOrDefault(a => string.Equals(a.Name, account, StringComparison.OrdinalIgnoreCase));
            if (created != null) SelectedAccount = created;
        }
        catch (Exception ex) { AppendLog("建档失败：" + ex.Message); }
    }

    /// <summary>快照刷新运行锁（防重入，见 TryAutoRefreshSnapshotAsync）。</summary>
    private bool _refreshBusy;

    /// <summary>最近一次快照检查时间；10 分钟内不重复全量哈希（#33：原先每 30 秒全量跑，一晚 2880 轮）。</summary>
    private DateTime _lastRefreshCheckUtc = DateTime.MinValue;

    /// <summary>
    /// 快照自动刷新（TraeSwitch「会话保鲜」）：客户端已退出且能唯一识别当前账号、
    /// 且该账号备份与当前会话不一致时，用当前会话静默刷新其备份。
    /// </summary>
    internal async Task TryAutoRefreshSnapshotAsync()
    {
        if (_refreshBusy) return;
        var now = DateTime.UtcNow;
        if ((now - _lastRefreshCheckUtc).TotalMinutes < 10) return;
        _lastRefreshCheckUtc = now;
        _refreshBusy = true;
        try
        {
            var settings = MainViewModel.SwitchSettings;
            var vault = MainViewModel.Vault;
            var client = BuildClient();
            if (settings == null || vault == null || client == null) return;
            if (client.IsRunning()) return;                       // 运行中不读登录态
            if (settings.Data.Fingerprint.Count == 0) return;

            // 识别当前 live 属主：最高分 ≥0.75 且显著领先次高
            var accs = settings.Data.Accounts;
            if (accs == null || accs.Count == 0) return;
            var scores = accs
                .Select(a => (a, v: vault.MatchScore(a)))
                .Where(x => x.v.HasValue)
                .OrderByDescending(x => x.v!.Value)
                .ToList();
            if (scores.Count == 0) return;
            var best = scores[0];
            if (best.v!.Value < 0.75) return;
            if (scores.Count > 1 && best.v!.Value - scores[1].v!.Value < 0.2) return;

            string account = best.a;
            var guid = best.v!.Value;
            if (await vault.VerifyAsync(account))
            {
                AppendLog($"「{account}」备份与当前会话一致，无需刷新");
                return;
            }
            int count = await vault.BackupAsync(account, settings.Data.Fingerprint);
            AppendLog($"已用当前会话自动刷新「{account}」的备份（载体 {count} 个）");
            LoadAccounts();
        }
        catch (Exception ex)
        {
            AppendLog("快照自动刷新失败：" + ex.Message);
        }
        finally { _refreshBusy = false; }
    }
}





