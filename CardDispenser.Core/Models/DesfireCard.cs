using System.Security.Cryptography;

namespace CardDispenser.Core.Models;

/// <summary>DESFire 支援的金鑰演算法類型。</summary>
public enum DesfireKeyType
{
    Des,
    ThreeDes,
    Aes128,
}

/// <summary>
/// DESFire 卡片的輕量抽象模型。
///
/// 與 Mifare Classic 的關鍵差異:
/// - Mifare Classic 是單一 sector 金鑰比對即視為驗證通過(相對脆弱、金鑰易被破解重放);
///   DESFire 採挑戰-應答式雙向互相認證(mutual authentication,3-pass),
///   讀卡機與卡片互相證明持有正確金鑰,無法單純靠側錄金鑰就冒用。本類別的
///   <see cref="AuthenticateStep1"/> / <see cref="AuthenticateStep2"/>(卡片視角)搭配
///   <see cref="DesfireAuthenticator"/>(讀卡機視角)就是這條 3-pass 流程的模擬實作。
/// - Mifare Classic 的存取控制以「sector」為單位;DESFire 則是 application/file 架構,
///   可在同一張卡上劃分多個 application、每個 application 下再劃分多個 file,
///   並對每個 file 個別設定讀/寫/讀寫/變更金鑰等權限(檔案級權限,細粒度遠高於 Mifare Classic)。
/// - 金鑰演算法上,DESFire 支援 DES/3DES/AES,安全強度依配置而定。
///
/// 本模擬的 3-pass 認證只走 AES128 路徑,用 AES-CBC(零 IV)模擬「用金鑰加密挑戰值」的
/// 結構,刻意省略真實 DESFire 的 CMAC、command counter、secure messaging 等細節;
/// 也不實作完整的 application/file 權限矩陣(file 層級僅示範需通過驗證才能讀寫)。
/// </summary>
public sealed class DesfireCard
{
    private readonly Dictionary<byte, byte[]> _files = new();
    private byte[]? _masterKey;
    private DesfireKeyType _keyType;
    private bool _authenticated;

    // 3-pass 認證期間卡片端記住的挑戰值,只在 step1~step2 之間有效。
    private byte[]? _pendingRndB;
    private bool _awaitingStep2;

    public string Uid { get; }
    public uint ApplicationId { get; private set; }

    /// <summary>3-pass 互相認證通過後衍生的 session key(16 bytes,AES-128);尚未驗證通過時為 null。</summary>
    public byte[]? SessionKey { get; private set; }

    public DesfireCard(string uid)
    {
        Uid = uid;
    }

    /// <summary>模擬出廠寫入的預設 application 金鑰(demo 用,正式環境一樣須做金鑰分散)。</summary>
    public void ProvisionMasterKey(DesfireKeyType keyType, byte[] key)
    {
        _keyType = keyType;
        _masterKey = key;
    }

    public void SelectApplication(uint applicationId)
    {
        ApplicationId = applicationId;
        // 換 application 需重新驗證,與真實 DESFire 行為一致;連同進行中的認證狀態一併清空。
        _authenticated = false;
        SessionKey = null;
        ResetPendingAuth();
    }

    /// <summary>
    /// 3-pass 互相認證 step 1(卡片視角)。卡片產生 16-byte 亂數 RndB,以金鑰加密後
    /// 回傳給讀卡機(對應真實協定的 E_k(RndB)),並記住 RndB、進入「等待 step2」狀態。
    /// </summary>
    public byte[] AuthenticateStep1(byte keyNo)
    {
        if (_masterKey is null)
        {
            throw new InvalidOperationException("卡片尚未佈建金鑰(ProvisionMasterKey 尚未呼叫),無法驗證");
        }
        if (_keyType != DesfireKeyType.Aes128)
        {
            throw new NotSupportedException("本模擬的 3-pass 認證只示範 AES128 金鑰路徑");
        }

        _ = keyNo; // demo 單一 application 金鑰,keyNo 僅示意介面形狀,不影響金鑰選擇

        _authenticated = false;
        SessionKey = null;
        _pendingRndB = RandomNumberGenerator.GetBytes(16);
        _awaitingStep2 = true;

        return DesfireAuthenticator.EncryptBlock(_pendingRndB, _masterKey);
    }

    /// <summary>
    /// 3-pass 互相認證 step 2(卡片視角)。收到讀卡機回傳的 E_k(RndA ‖ RndB')
    /// (RndB' 為 RndB 左旋 1 byte),解密後驗證 RndB' 是否與自己記住的 RndB 左旋結果
    /// 相符——通過才代表讀卡機真的持有正確金鑰。通過後回傳 E_k(RndA'),並衍生
    /// session key、標記為已驗證;不通過清除待驗證狀態並回傳 null(不拋例外,由呼叫端
    /// 以 null 判斷失敗,對應真實協定「任一步失敗即視為驗證失敗」)。
    /// </summary>
    public byte[]? AuthenticateStep2(byte[] tokenFromReader)
    {
        if (_masterKey is null || !_awaitingStep2 || _pendingRndB is null)
        {
            ResetPendingAuth();
            return null;
        }

        byte[] decrypted;
        try
        {
            decrypted = DesfireAuthenticator.DecryptBlock(tokenFromReader, _masterKey);
        }
        catch (CryptographicException)
        {
            ResetPendingAuth();
            return null;
        }

        if (decrypted.Length != 32)
        {
            ResetPendingAuth();
            return null;
        }

        var rndA = decrypted[0..16];
        var rndBRotatedFromReader = decrypted[16..32];
        var expectedRndBRotated = DesfireAuthenticator.RotateLeftOneByte(_pendingRndB);

        if (!CryptographicOperations.FixedTimeEquals(rndBRotatedFromReader, expectedRndBRotated))
        {
            ResetPendingAuth();
            return null;
        }

        var response = DesfireAuthenticator.EncryptBlock(DesfireAuthenticator.RotateLeftOneByte(rndA), _masterKey);
        SessionKey = DesfireAuthenticator.DeriveSessionKey(rndA, _pendingRndB);
        _authenticated = true;
        ResetPendingAuth();
        return response;
    }

    private void ResetPendingAuth()
    {
        _awaitingStep2 = false;
        _pendingRndB = null;
    }

    public bool WriteFile(byte fileId, byte[] data)
    {
        if (!_authenticated)
        {
            return false;
        }
        _files[fileId] = (byte[])data.Clone();
        return true;
    }

    public byte[]? ReadFile(byte fileId)
    {
        if (!_authenticated)
        {
            return null;
        }
        return _files.TryGetValue(fileId, out var data) ? (byte[])data.Clone() : null;
    }
}
