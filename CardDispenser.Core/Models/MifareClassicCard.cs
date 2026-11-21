namespace CardDispenser.Core.Models;

/// <summary>驗證時使用 Key A 或 Key B。</summary>
public enum MifareKeyType
{
    KeyA,
    KeyB,
}

/// <summary>
/// Mifare Classic 1K 卡片的記憶體結構模擬。
/// 結構:16 個 sector,每 sector 4 個 block,每 block 16 bytes(共 1024 bytes)。
/// 每個 sector 的 block 3(sector trailer)存放 Key A(6B) + Access Bits(4B) + Key B(6B)。
/// Sector 0 的 block 0 是製造商區塊(UID + BCC + SAK + ATQA + 製造商資料),出廠寫入、唯讀。
/// 讀寫任何 sector 前都必須先用 Key A 或 Key B 通過驗證(authenticate),
/// 這是 Mifare Classic 最基本(也相對脆弱)的安全模型——單一 sector 金鑰外洩,整個 sector 就失守。
/// </summary>
public sealed class MifareClassicCard
{
    public const int SectorCount = 16;
    public const int BlocksPerSector = 4;
    public const int BlockSize = 16;
    public const int TotalBlocks = SectorCount * BlocksPerSector;

    /// <summary>
    /// Mifare Classic 出廠預設金鑰,全球通用、業界公開已知的值。
    /// 正式上線前務必依卡片 UID 做金鑰分散(key diversification),絕不能沿用出廠預設金鑰,
    /// 否則任何人持有讀卡機都能直接驗證通過、竄改卡片資料。
    /// </summary>
    public static readonly byte[] FactoryDefaultKey = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

    // 出廠預設 access bits(FF 07 80 69):代表尚未設定存取權限限制的「全開放」狀態。
    private static readonly byte[] FactoryDefaultAccessBits = { 0xFF, 0x07, 0x80, 0x69 };

    private readonly byte[][] _blocks;
    private readonly HashSet<int> _authenticatedSectors = new();

    public string Uid { get; }

    private MifareClassicCard(string uid, byte[][] blocks)
    {
        Uid = uid;
        _blocks = blocks;
    }

    /// <summary>建立一張出廠狀態的空白卡(所有 sector 皆為預設金鑰、資料區清空)。</summary>
    public static MifareClassicCard CreateBlank(string uid)
    {
        var blocks = new byte[TotalBlocks][];
        for (int i = 0; i < TotalBlocks; i++)
        {
            blocks[i] = new byte[BlockSize];
        }

        // Sector 0 / Block 0:製造商區塊,出廠寫入、唯讀
        var uidBytes = ParseUid(uid);
        var mfgBlock = blocks[0];
        Array.Copy(uidBytes, 0, mfgBlock, 0, 4);
        mfgBlock[4] = (byte)(uidBytes[0] ^ uidBytes[1] ^ uidBytes[2] ^ uidBytes[3]); // BCC
        mfgBlock[5] = 0x08; // SAK:Mifare Classic 1K
        mfgBlock[6] = 0x04; // ATQA low byte
        mfgBlock[7] = 0x00; // ATQA high byte
        // byte 8~15:製造商資料,demo 中維持 0

        for (int sector = 0; sector < SectorCount; sector++)
        {
            var trailer = blocks[sector * BlocksPerSector + 3];
            Array.Copy(FactoryDefaultKey, 0, trailer, 0, 6);
            Array.Copy(FactoryDefaultAccessBits, 0, trailer, 6, 4);
            Array.Copy(FactoryDefaultKey, 0, trailer, 10, 6);
        }

        return new MifareClassicCard(uid, blocks);
    }

    private static byte[] ParseUid(string uid)
    {
        var hex = uid.Replace(":", "").Replace("-", "");
        var bytes = Convert.FromHexString(hex);
        if (bytes.Length != 4)
        {
            throw new ArgumentException("Mifare Classic 1K UID 須為 4 bytes(單尺寸 UID)", nameof(uid));
        }
        return bytes;
    }

    private static int AbsoluteIndex(int sector, int blockInSector)
    {
        if (sector < 0 || sector >= SectorCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sector));
        }
        if (blockInSector < 0 || blockInSector >= BlocksPerSector)
        {
            throw new ArgumentOutOfRangeException(nameof(blockInSector));
        }
        return sector * BlocksPerSector + blockInSector;
    }

    /// <summary>對指定 sector 用 Key A 或 Key B 驗證;金鑰錯誤時模擬驗證失敗(回傳 false,不拋例外)。</summary>
    public bool Authenticate(int sector, MifareKeyType keyType, byte[] key)
    {
        var trailer = _blocks[AbsoluteIndex(sector, 3)];
        var expected = keyType == MifareKeyType.KeyA ? trailer[0..6] : trailer[10..16];
        bool ok = key.Length == 6 && expected.SequenceEqual(key);
        if (ok)
        {
            _authenticatedSectors.Add(sector);
        }
        else
        {
            _authenticatedSectors.Remove(sector);
        }
        return ok;
    }

    public bool IsAuthenticated(int sector) => _authenticatedSectors.Contains(sector);

    public byte[] ReadBlock(int sector, int blockInSector)
    {
        if (!IsAuthenticated(sector))
        {
            throw new InvalidOperationException($"Sector {sector} 尚未通過驗證,無法讀取");
        }
        return (byte[])_blocks[AbsoluteIndex(sector, blockInSector)].Clone();
    }

    public void WriteBlock(int sector, int blockInSector, byte[] data)
    {
        if (data.Length != BlockSize)
        {
            throw new ArgumentException($"Block 資料須為 {BlockSize} bytes", nameof(data));
        }
        if (sector == 0 && blockInSector == 0)
        {
            throw new InvalidOperationException("Sector 0 Block 0 為製造商區塊,唯讀");
        }
        if (blockInSector == BlocksPerSector - 1)
        {
            throw new InvalidOperationException("Sector Trailer 須透過金鑰異動流程寫入,一般資料寫入不可直接覆蓋");
        }
        if (!IsAuthenticated(sector))
        {
            throw new InvalidOperationException($"Sector {sector} 尚未通過驗證,無法寫入");
        }
        Array.Copy(data, _blocks[AbsoluteIndex(sector, blockInSector)], BlockSize);
    }

    /// <summary>
    /// 判斷是否仍是「出廠空白可用卡」。
    /// 模擬簡化邏輯:以目標資料 sector 的 Key A 是否仍為出廠預設金鑰作為依據。
    /// 正式環境不能只憑金鑰判斷是否空白(金鑰可能被重複使用或惡意還原),
    /// 應搭配後端卡片使用狀態資料庫或卡片內專屬的「已發卡」旗標一併確認。
    /// </summary>
    public bool IsFactoryBlank(int dataSector)
    {
        var trailer = _blocks[AbsoluteIndex(dataSector, 3)];
        return trailer[0..6].SequenceEqual(FactoryDefaultKey);
    }
}
