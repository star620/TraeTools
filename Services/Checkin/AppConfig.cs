using System.Text.Json;
using TraeTools.Services.Json;

namespace TraeCheckin;

/// <summary>
/// 本地配置：存储 token、设备号、自动签到设置。
/// 保存到配置根目录下的 config.json（Windows: %APPDATA%\TraeTools；Android: 应用私有目录）。
/// 敏感字段（Token/Session/GitHubToken/DeviceId）以本平台的原生密钥加密落盘
/// （Windows=DPAPI，Android=Keystore，见 TraeTools.Platform.Secrets），加载时解密回内存；
/// 旧版明文在首次加载时自动迁移加密。
/// </summary>
public class AppConfig
{
    /// <summary>敏感字段加密版本标记："dpapi-v1"=Windows DPAPI、"and-v1"=Android Keystore；
    /// null=旧版明文（等待迁移）。两平台标记都会被识别，跨平台拷贝时解密失败由各字段兜底处理。</summary>
    public string? SecretsVaultVersion { get; set; }
    public string? Token { get; set; }
    /// <summary>X-Cloudide-Session 会话 Cookie 值（约 14 天有效），用于 token 失效时静默换新。</summary>
    public string? Session { get; set; }
    public DateTime? TokenUpdatedAt { get; set; }
    /// <summary>GitHub OAuth 设备码授权得到的 access_token（用于云端自动签到部署）。</summary>
    public string? GitHubToken { get; set; }
    /// <summary>GitHub 授权后的登录用户名（fork 目标 owner）。</summary>
    public string? GitHubLogin { get; set; }
    /// <summary>飞书机器人 webhook（签到结果推送；为空则关闭推送）。</summary>
    public string? FeishuWebhook { get; set; }
    public string DeviceId { get; set; } = GenerateDeviceId();
    public bool AutoCheckinEnabled { get; set; } = true;
    public string AutoCheckinTime { get; set; } = "08:00";
    /// <summary>多账号签到间隔（秒），每个账号签到完成后等待该时长再签到下一个；默认 5 秒。</summary>
    public int CheckinIntervalSeconds { get; set; } = 5;
    public DateTime? LastCheckinDate { get; set; }
    public double LastRemaining { get; set; } = -1;
    /// <summary>全部 Trae 账号（多账号模型主存储）。</summary>
    public List<TraeAccount> Accounts { get; set; } = new();
    /// <summary>仪表盘当前展示的账号 Id；为空时取 Accounts[0]。</summary>
    public string? ActiveAccountId { get; set; }
    /// <summary>主窗口上次关闭时的位置与尺寸（正常状态的边界），null/无效时用默认尺寸居中。</summary>
    public int? WindowLeft { get; set; }
    public int? WindowTop { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    /// <summary>是否已同意首次启动的用户协议（EULA）。同意后不再弹出。</summary>
    public bool EulaAccepted { get; set; }
    /// <summary>云端部署成功后是否已询问过用户「愿不愿意给源仓库点 star」。置 true 后不再打扰。</summary>
    public bool StarAskedAfterDeploy { get; set; }
    /// <summary>关闭主窗口时是否最小化到系统托盘（托盘驻留）。</summary>
    public bool MinimizeToTray { get; set; } = true;

    private static string ConfigDir => TraeTools.Services.DataPaths.Root;

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = AppJsonIndented.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    // 加密迁移：已是本平台的密文版本则解密回内存使用；旧明文标记版本，稍后统一加密落盘
                    var needSeal = cfg.SecretsVaultVersion != TraeTools.Platform.Secrets.CurrentVaultVersion;
                    UnsealSecrets(cfg);
                    cfg.SecretsVaultVersion = TraeTools.Platform.Secrets.CurrentVaultVersion;

                    // 多账号迁移：旧单账号转成 Accounts[0]
                    if (TryMigrateLegacy(cfg))
                    {
                        // 迁移后清空旧字段，避免双份数据源（DeviceId 已复制进新账号）
                        cfg.Token = null;
                        cfg.Session = null;
                        cfg.TokenUpdatedAt = null;
                        cfg.LastCheckinDate = null;
                        if (string.IsNullOrEmpty(cfg.ActiveAccountId))
                            cfg.ActiveAccountId = cfg.Accounts[0].Id;
                        cfg.Save();
                    }
                    // 迁移：优先复用官方客户端的真实 Aha 设备 ID，否则签到接口风控会返回 9074
                    var aha = TryResolveAhaDeviceId();
                    if (aha != null && aha != cfg.DeviceId)
                    {
                        cfg.DeviceId = aha;
                        cfg.Save();
                    }
                    // 旧明文首次加载：立即加密落盘，避免明文长期留存
                    if (needSeal) cfg.Save();
                    return cfg;
                }
            }
        }
        catch { /* 配置损坏时回退默认 */ }

        var fresh = new AppConfig();
        var resolved = TryResolveAhaDeviceId();
        if (resolved != null) fresh.DeviceId = resolved;
        return fresh;
    }

    /// <summary>
    /// 单账号 → 多账号迁移：当 Accounts 为空且存在旧版 Token/Session 时，
    /// 生成第一个账号并放入 Accounts。返回是否发生迁移。幂等（已有账号则不动）。
    /// </summary>
    public static bool TryMigrateLegacy(AppConfig cfg)
    {
        if (cfg.Accounts.Count > 0) return false;
        if (string.IsNullOrEmpty(cfg.Token) && string.IsNullOrEmpty(cfg.Session)) return false;

        cfg.Accounts.Add(new TraeAccount
        {
            Token = cfg.Token,
            Session = cfg.Session,
            DeviceId = string.IsNullOrEmpty(cfg.DeviceId) ? "" : cfg.DeviceId,
            TokenUpdatedAt = cfg.TokenUpdatedAt,
            LastCheckinDate = cfg.LastCheckinDate
        });
        return true;
    }

    /// <summary>
    /// 生成一个 16 位十进制设备 ID（与 TraeWork Aha SDK 的设备号格式一致）。
    /// 签到接口风控要求 x-device-id 为数字设备号，使用 GUID/UUID 会触发 9074。
    /// </summary>
    private static string GenerateDeviceId()
    {
        return Random.Shared.NextInt64(1_000_000_000_000_000L, 10_000_000_000_000_000L).ToString();
    }

    /// <summary>
    /// 从本机 TraeWork 官方客户端的数据目录解析 Aha 设备 ID（16 位数字）。
    /// storage.json 中存在形如 "iCubeAuthInfo://icube-dc:1234567890123456" 的键。
    /// </summary>
    internal static string? TryResolveAhaDeviceId()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // 顺序重要：现役客户端 Trae CN 必须排在最前。融合迁移后旧目录（TRAE SOLO CN / TRAE SOLO）
        // 可能仍残留 storage.json 且带着**失效的旧设备号**，先读旧目录会把过期设备号当成当前设备。
        var dirs = new[] { "Trae CN", "TRAE SOLO CN", "TRAE SOLO" };
        foreach (var dir in dirs)
        {
            var path = Path.Combine(appData, dir, "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    const string prefix = "iCubeAuthInfo://icube-dc:";
                    if (!prop.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var id = prop.Name.Substring(prefix.Length);
                    if (id.Length >= 8 && id.All(char.IsDigit)) return id;
                }
            }
            catch { /* 忽略读取/解析失败，继续尝试其他目录 */ }
        }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            // 写「加密副本」而非 this：敏感字段加密落盘，且不修改内存中的明文（各调用方无感）
            var json = AppJsonIndented.Serialize(CloneForStorage());
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* 保存失败（含 DPAPI 异常）时保留磁盘旧文件，下次启动仍可读取 */ }
    }

    /// <summary>生成一份敏感字段（Token/Session/GitHubToken/DeviceId）已平台加密的副本。</summary>
    private AppConfig CloneForStorage()
    {
        return new AppConfig
        {
            SecretsVaultVersion = TraeTools.Platform.Secrets.CurrentVaultVersion,
            Token = EncryptSecret(Token),
            Session = EncryptSecret(Session),
            TokenUpdatedAt = TokenUpdatedAt,
            GitHubToken = EncryptSecret(GitHubToken),
            GitHubLogin = GitHubLogin,
            FeishuWebhook = FeishuWebhook,
            // DeviceId 非空类型：加密失败（极端平台异常）时退回明文，避免设备号被静默清掉
            // 后在下次启动被重新生成，导致风控把新设备号当陌生设备
            DeviceId = EncryptSecret(DeviceId) ?? DeviceId,
            AutoCheckinEnabled = AutoCheckinEnabled,
            AutoCheckinTime = AutoCheckinTime,
            CheckinIntervalSeconds = CheckinIntervalSeconds,
            LastCheckinDate = LastCheckinDate,
            LastRemaining = LastRemaining,
            ActiveAccountId = ActiveAccountId,
            WindowLeft = WindowLeft,
            WindowTop = WindowTop,
            WindowWidth = WindowWidth,
            WindowHeight = WindowHeight,
            EulaAccepted = EulaAccepted,
            StarAskedAfterDeploy = StarAskedAfterDeploy,
            MinimizeToTray = MinimizeToTray,
            Accounts = Accounts.Select(a => new TraeAccount
            {
                Id = a.Id,
                Name = a.Name,
                Token = EncryptSecret(a.Token),
                Session = EncryptSecret(a.Session),
                DeviceId = EncryptSecret(a.DeviceId) ?? a.DeviceId,
                AccountUid = a.AccountUid,
                TokenUpdatedAt = a.TokenUpdatedAt,
                LastCheckinDate = a.LastCheckinDate,
                RemainingCredits = a.RemainingCredits,
                Enabled = a.Enabled,
                IsMember = a.IsMember,
                ScreenName = a.ScreenName,
                MobileMasked = a.MobileMasked,
                AvatarUrl = a.AvatarUrl,
                IsStudent = a.IsStudent,
            }).ToList(),
        };
    }

    /// <summary>已加密配置加载后：解密敏感字段回内存（仅已加密版本生效；旧明文保持原样等迁移）。</summary>
    private static void UnsealSecrets(AppConfig cfg)
    {
        // 两个平台的版本标记都认：配置文件可能被跨平台拷贝，此时解密必然失败，
        // 交由各字段的兜底策略处理（Token → null → 重新登录），而不是把密文当明文继续用。
        if (!TraeTools.Platform.Secrets.IsKnownVaultVersion(cfg.SecretsVaultVersion)) return;
        cfg.Token = DecryptSecret(cfg.Token);
        cfg.Session = DecryptSecret(cfg.Session);
        cfg.GitHubToken = DecryptSecret(cfg.GitHubToken);
        var dev = DecryptSecret(cfg.DeviceId);
        if (!string.IsNullOrEmpty(dev)) cfg.DeviceId = dev; // 解密失败（换机/换用户）保留原值，等重新登录刷新
        foreach (var a in cfg.Accounts)
        {
            a.Token = DecryptSecret(a.Token);
            a.Session = DecryptSecret(a.Session);
            var aDev = DecryptSecret(a.DeviceId);
            if (!string.IsNullOrEmpty(aDev)) a.DeviceId = aDev;
        }
    }

    /// <summary>平台加密（Windows=DPAPI / Android=Keystore，见 TraeTools.Platform.Secrets）。
    /// 加密失败返回 null，由调用方兜底（保留旧文件）。</summary>
    private static string? EncryptSecret(string? plain) => TraeTools.Platform.Secrets.Protect(plain);

    /// <summary>平台解密；失败（换 Windows 用户/重装系统/跨平台拷贝配置）返回 null，触发重新登录。</summary>
    private static string? DecryptSecret(string? enc) => TraeTools.Platform.Secrets.Unprotect(enc);
}