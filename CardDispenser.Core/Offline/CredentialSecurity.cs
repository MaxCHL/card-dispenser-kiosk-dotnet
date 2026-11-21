using System.Security.Cryptography;

namespace CardDispenser.Core.Offline;

/// <summary>
/// 離線憑證簽章/驗章工具。
/// 這裡用 HMAC-SHA256 只是「代表性」模擬「用案場金鑰簽章、鎖端能離線驗章」這個概念——
/// 真實飯店鎖廠商多使用其專有加密演算法,細節未公開,不在本模擬的
/// 重建範圍內。HMAC-SHA256 在這裡只是一個「可查證、業界通用」的替代實作。
/// </summary>
public static class CredentialSecurity
{
    /// <summary>對 payload 以 site key 計算 HMAC-SHA256,取前 <see cref="GuestCardCredential.SignatureLength"/> bytes 當簽章。</summary>
    public static byte[] Sign(byte[] payload, byte[] siteKey)
    {
        using var hmac = new HMACSHA256(siteKey);
        var fullHash = hmac.ComputeHash(payload);
        return fullHash[..GuestCardCredential.SignatureLength];
    }

    /// <summary>
    /// 驗證簽章是否與 payload、site key 相符。
    /// 用固定時間比較(<see cref="CryptographicOperations.FixedTimeEquals"/>),
    /// 避免逐 byte 比較時的時序差異洩漏簽章正確前綴長度(timing attack)。
    /// </summary>
    public static bool Verify(byte[] payload, byte[] signature, byte[] siteKey)
    {
        if (signature.Length != GuestCardCredential.SignatureLength)
        {
            return false;
        }
        var expected = Sign(payload, siteKey);
        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }
}
