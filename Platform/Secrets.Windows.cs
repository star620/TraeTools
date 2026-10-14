#if !ANDROID
using System.Security.Cryptography;
using System.Text;

namespace TraeTools.Platform;

/// <summary>Windows 实现：DPAPI（当前用户作用域）加密敏感字段，密文为纯 Base64。</summary>
internal static partial class Secrets
{
    private static partial string? ProtectCore(string plain)
    {
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch { return null; }
    }

    private static partial string? UnprotectCore(string enc)
    {
        // 跨平台密文互不通用：DPAPI 不可能解开 Android Keystore 产生的密文。
        // 明确判失败（而不是把密文当明文用），让上层走「重新登录」流程。
        // 注意这条判断只在 Windows 实现里成立 —— Android 密文本身就是这个前缀。
        if (enc.StartsWith(AndroidPrefix, StringComparison.Ordinal)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }
}
#endif
