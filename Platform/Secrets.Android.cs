#if ANDROID
using System.Security.Cryptography;
using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;

namespace TraeTools.Platform;

/// <summary>
/// Android 实现：Keystore 生成 RSA-2048 密钥对（私钥不可导出），
/// 每次加密生成随机 AES-256-GCM 密钥加密数据，再用 RSA 公钥包裹该 AES 密钥。
/// 密文格式："and-v1:" + Base64(RSA 包裹的 AES 密钥) + ":" + Base64(nonce) + ":" + Base64(tag+ciphertext)。
/// 密钥随应用卸载/清除数据一同消失，因此密文不会在本机之外泄露。
/// </summary>
internal static partial class Secrets
{
    private const string KeyAlias = "TraeTools.Secrets.RSA.v1";
    private const int NonceSize = 12;   // AES-GCM 推荐 nonce 长度
    private const int TagSize = 16;     // 128-bit 认证标签

    private static partial string? ProtectCore(string plain)
    {
        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plain);

            // 随机 AES-256 会话密钥 + GCM 加密
            var key = RandomNumberGenerator.GetBytes(32);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plainBytes, cipher, tag);

            // RSA 公钥包裹 AES 密钥
            var pub = GetOrCreatePublicKey();
            if (pub == null) return null;
            var wrapCipher = Cipher.GetInstance("RSA/ECB/PKCS1Padding")!;
            // Cipher.EncryptMode 常量已废弃（CS0619，报错级），改用 CipherMode 枚举成员；
            // 本文件同时 using 了 System.Security.Cryptography（含同名 CipherMode），故写全称
            wrapCipher.Init(Javax.Crypto.CipherMode.EncryptMode, pub);
            var wrapped = wrapCipher.DoFinal(key);
            if (wrapped == null) return null;

            var payload = Convert.ToBase64String(wrapped) + ":" + Convert.ToBase64String(nonce);
            var tagAndCipher = new byte[tag.Length + cipher.Length];
            Buffer.BlockCopy(tag, 0, tagAndCipher, 0, tag.Length);
            Buffer.BlockCopy(cipher, 0, tagAndCipher, tag.Length, cipher.Length);
            payload += ":" + Convert.ToBase64String(tagAndCipher);
            return AndroidPrefix + payload;
        }
        catch { return null; }
    }

    private static partial string? UnprotectCore(string enc)
    {
        // 必须带本平台前缀：历史上出现过「未加密的明文被当作密文送来」的情况，
        // 此时应直接判失败让上层保留原值，而不是盲切前 7 个字符去解析。
        if (!enc.StartsWith(AndroidPrefix, StringComparison.Ordinal)) return null;
        try
        {
            var body = enc[AndroidPrefix.Length..];
            var parts = body.Split(':', 3);
            if (parts.Length != 3) return null;

            var wrapped = Convert.FromBase64String(parts[0]);
            var nonce = Convert.FromBase64String(parts[1]);
            var tagAndCipher = Convert.FromBase64String(parts[2]);
            if (tagAndCipher.Length < TagSize) return null;

            // RSA 私钥解出 AES 密钥
            var priv = GetPrivateKey();
            if (priv == null) return null;
            var unwrapCipher = Cipher.GetInstance("RSA/ECB/PKCS1Padding")!;
            unwrapCipher.Init(Javax.Crypto.CipherMode.DecryptMode, priv);
            var key = unwrapCipher.DoFinal(wrapped);
            if (key == null) return null;

            var tag = new byte[TagSize];
            var cipher = new byte[tagAndCipher.Length - TagSize];
            Buffer.BlockCopy(tagAndCipher, 0, tag, 0, TagSize);
            Buffer.BlockCopy(tagAndCipher, TagSize, cipher, 0, cipher.Length);

            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    /// <summary>取（不存在则创建）Keystore 中的 RSA 公钥。</summary>
    private static IPublicKey? GetOrCreatePublicKey()
    {
        var keyStore = KeyStore.GetInstance("AndroidKeyStore")!;
        keyStore.Load(null);
        if (!keyStore.ContainsAlias(KeyAlias))
            GenerateRsaKeyPair();
        var entry = keyStore.GetEntry(KeyAlias, null) as KeyStore.PrivateKeyEntry;
        return entry?.Certificate?.PublicKey;
    }

    /// <summary>取 Keystore 中的 RSA 私钥。</summary>
    private static IPrivateKey? GetPrivateKey()
    {
        var keyStore = KeyStore.GetInstance("AndroidKeyStore")!;
        keyStore.Load(null);
        var entry = keyStore.GetEntry(KeyAlias, null) as KeyStore.PrivateKeyEntry;
        return entry?.PrivateKey;
    }

    private static void GenerateRsaKeyPair()
    {
        var generator = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmRsa, "AndroidKeyStore")!;
        var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeEcb)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingRsaPkcs1)
            .SetKeySize(2048)
            .Build();
        generator.Initialize(spec);
        generator.GenerateKeyPair();
    }
}
#endif
