namespace CardDispenser.Core.Offline;

/// <summary>
/// 案場(PropertyId)→ site key 的對應。同一案場的所有鎖與卡共用一把金鑰
/// (key diversification 以案場為單位,而非以卡片 UID 為單位)。
/// Demo 用記憶體字典儲存;正式環境金鑰不會這樣明碼存在應用程式記憶體裡,
/// 而是由 HSM(Hardware Security Module)或雲端 KMS 管理,簽章/驗章動作
/// 透過 HSM/KMS API 呼叫完成,金鑰本身永遠不落地、不外流到應用層。
/// </summary>
public sealed class SiteKeyRing
{
    private readonly Dictionary<ushort, byte[]> _keys = new();

    /// <summary>設定(或覆寫)某案場的 site key。對應現實中的母卡佈建/金鑰輪替流程。</summary>
    public void SetKey(ushort propertyId, byte[] siteKey) => _keys[propertyId] = siteKey;

    /// <summary>嘗試取得案場金鑰;找不到回傳 false(不拋例外),供讀取/驗證流程優雅地判定失敗。</summary>
    public bool TryGetKey(ushort propertyId, out byte[]? siteKey) => _keys.TryGetValue(propertyId, out siteKey);

    /// <summary>取得案場金鑰;找不到視為設定錯誤,直接拋例外(用於發卡流程——沒有金鑰不該繼續發卡)。</summary>
    public byte[] GetKeyOrThrow(ushort propertyId)
    {
        if (!_keys.TryGetValue(propertyId, out var key))
        {
            throw new InvalidOperationException($"案場 {propertyId} 尚未配置 site key(母卡佈建流程尚未完成)");
        }
        return key;
    }
}
