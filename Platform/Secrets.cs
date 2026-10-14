namespace TraeTools.Platform;

/// <summary>
/// 敏感字段加密抽象（跨平台 partial）。
/// Windows：DPAPI（当前用户作用域），密文为纯 Base64（历史格式，无前缀）。
/// Android：Keystore（RSA 包裹 AES-256-GCM 密钥），密文带 <see cref="AndroidPrefix"/> 前缀。
/// Protect 返回 null 表示加密失败（调用方按各自策略兜底）。
/// </summary>
internal static partial class Secrets
{
    /// <summary>Windows 侧落盘标记（沿用历史值，保证老配置可直接解密读回）。</summary>
    public const string WindowsVaultVersion = "dpapi-v1";

    /// <summary>Android 侧落盘标记：与 Windows 的 DPAPI 密文互不通用，用版本号区分来源。</summary>
    public const string AndroidVaultVersion = "and-v1";

    /// <summary>Android 密文前缀。</summary>
    public const string AndroidPrefix = AndroidVaultVersion + ":";

    /// <summary>当前平台写入配置时使用的密文版本标记。</summary>
    public static string CurrentVaultVersion =>
        OperatingSystem.IsAndroid() ? AndroidVaultVersion : WindowsVaultVersion;

    /// <summary>
    /// 该版本标记是否为本程序认识的密文格式。
    /// 两个平台都要认识：配置目录可能被跨平台拷贝（手机 → 电脑），
    /// 此时解密必然失败，交由各字段的兜底策略处理（Token 失效 → 重新登录），
    /// 而不是把密文当明文继续用。
    /// </summary>
    public static bool IsKnownVaultVersion(string? version) =>
        version == WindowsVaultVersion || version == AndroidVaultVersion;

    /// <summary>加密敏感字段；null/空串原样返回（无需加密）。</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return plain;
        return ProtectCore(plain);
    }

    /// <summary>解密敏感字段；null/空串原样返回。</summary>
    public static string? Unprotect(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return enc;
        // ⚠️ 「跨平台密文互不通用」这条规则必须写在具体平台的实现里，不能写在这层共享代码里：
        // Android 自己的密文就是 "and-v1:" 前缀，若在此处按前缀拦截，Android 将永远解不开
        // 自己加密的数据（症状：DeviceId 退化成密文被当作设备号发给服务端 → 风控拦截）。
        // 现在的分工是：这里只负责分发，平台差异由各自的 UnprotectCore 处理。
        return UnprotectCore(enc);
    }

    private static partial string? ProtectCore(string plain);
    private static partial string? UnprotectCore(string enc);
}
