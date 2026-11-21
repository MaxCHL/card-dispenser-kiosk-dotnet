using System.Security.Cryptography;

namespace CardDispenser.Core.Models;

/// <summary>
/// 讀卡機端的 DESFire 3-pass 互相認證流程(模擬)。
/// 用 AES-CBC(零 IV)模擬「用金鑰加密挑戰值」這個結構,刻意省略真實 DESFire 的
/// CMAC、command counter、secure messaging 等細節,只示範「讀卡機與卡片互相證明
/// 持有正確金鑰」這個核心概念——這正是 DESFire 相對 Mifare Classic 單向金鑰比對
/// 的關鍵安全差異。
/// </summary>
public static class DesfireAuthenticator
{
    /// <summary>
    /// 跑完整的 3-pass 互相認證(讀卡機視角):
    /// step1 卡片給 E_k(RndB) → 讀卡機解密取得 RndB、自產 RndA,送出 E_k(RndA ‖ rotl(RndB)) →
    /// step2 卡片驗證通過後回 E_k(rotl(RndA)) → 讀卡機解密驗證 rotl(RndA) 是否相符。
    /// 任一步失敗回傳 (false, null);全部通過才回傳 (true, sessionKey)。
    /// </summary>
    public static (bool ok, byte[]? sessionKey) Authenticate(DesfireCard card, byte keyNo, byte[] key)
    {
        var step1Response = card.AuthenticateStep1(keyNo);
        var rndB = DecryptBlock(step1Response, key);

        var rndA = RandomNumberGenerator.GetBytes(16);
        var rndBRotated = RotateLeftOneByte(rndB);

        var readerToken = new byte[32];
        Array.Copy(rndA, 0, readerToken, 0, 16);
        Array.Copy(rndBRotated, 0, readerToken, 16, 16);
        var encryptedReaderToken = EncryptBlock(readerToken, key);

        var step2Response = card.AuthenticateStep2(encryptedReaderToken);
        if (step2Response is null)
        {
            return (false, null);
        }

        var rndARotatedFromCard = DecryptBlock(step2Response, key);
        var expectedRndARotated = RotateLeftOneByte(rndA);
        if (!CryptographicOperations.FixedTimeEquals(rndARotatedFromCard, expectedRndARotated))
        {
            return (false, null);
        }

        var sessionKey = DeriveSessionKey(rndA, rndB);
        return (true, sessionKey);
    }

    /// <summary>用 AES-128-CBC(零 IV,無 padding)加密一個 16 或 32 bytes 的區塊——模擬 3-pass 挑戰用的金鑰加密。</summary>
    internal static byte[] EncryptBlock(byte[] plaintext, byte[] key)
    {
        using var aes = CreateAes(key);
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    /// <summary>對應 <see cref="EncryptBlock"/> 的解密。金鑰錯誤時只會得到錯誤的明文,不會拋例外——由呼叫端比對內容判斷認證是否成立。</summary>
    internal static byte[] DecryptBlock(byte[] ciphertext, byte[] key)
    {
        using var aes = CreateAes(key);
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    /// <summary>16-byte 挑戰值左旋 1 byte,3-pass 認證的標準挑戰轉換(證明雙方都能正確處理對方送來的挑戰值)。</summary>
    internal static byte[] RotateLeftOneByte(byte[] value)
    {
        var result = new byte[value.Length];
        Array.Copy(value, 1, result, 0, value.Length - 1);
        result[^1] = value[0];
        return result;
    }

    /// <summary>
    /// session key 衍生(AES-128):RndA[0..4] ‖ RndB[0..4] ‖ RndA[12..16] ‖ RndB[12..16]。
    /// 這是真實 DESFire/Mifare 3-pass 認證中常見的 session key 組成方式——雙方各自
    /// 只知道自己產生的那個亂數的明文,以及對方亂數的明文(認證過程中交換過),
    /// 因此能各自獨立算出同一把 session key,而竊聽者只看得到密文。
    /// </summary>
    internal static byte[] DeriveSessionKey(byte[] rndA, byte[] rndB)
    {
        var sessionKey = new byte[16];
        Array.Copy(rndA, 0, sessionKey, 0, 4);
        Array.Copy(rndB, 0, sessionKey, 4, 4);
        Array.Copy(rndA, 12, sessionKey, 8, 4);
        Array.Copy(rndB, 12, sessionKey, 12, 4);
        return sessionKey;
    }

    private static Aes CreateAes(byte[] key)
    {
        var aes = Aes.Create();
        aes.Key = NormalizeKey(key);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.IV = new byte[16]; // 零 IV:demo 用單次挑戰-應答,不涉及多訊息重放,不需要隨機 IV
        return aes;
    }

    private static byte[] NormalizeKey(byte[] key)
    {
        // Demo 只走 AES128 路徑,金鑰須為 16 bytes;若呼叫端傳入長度不符的 key(如 demo 字串長度湊巧不是 16),
        // 截斷/補零到 16 bytes,避免因金鑰長度不符直接拋例外中斷 demo——真實 DESFire 對金鑰長度另有嚴格規範。
        if (key.Length == 16)
        {
            return key;
        }
        if (key.Length > 16)
        {
            return key[..16];
        }
        var padded = new byte[16];
        Array.Copy(key, padded, key.Length);
        return padded;
    }
}
