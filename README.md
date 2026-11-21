# 飯店自助報到機 + 自動發卡機 發卡流程模擬

技術展示用的 .NET 10 C# 模擬程式,模擬「飯店自助報到機(Kiosk)驅動自動發卡機
(Card Dispenser / 收發卡一體機)」完成一次房卡發卡交易的完整流程。

**這是純軟體模擬,沒有真實硬體、沒有真實廠商 SDK。** 所有機械動作(推卡、讀寫、
出卡、吞卡)與感測器(閘門 sensor、庫存 sensor、回收艙滿載 sensor)都以記憶體狀態
+ 人工延遲模擬,並提供「故障注入」機制讓每個 fallback 分支都能穩定重現。

## 系統流程圖

完整的視覺化流程圖(發卡流程含所有 fallback 分支、狀態管線、Fallback 矩陣、
Mifare 卡結構,以及飯店離線鎖 data-on-card 驗證模型)放在
[`docs/flow.html`](docs/flow.html)。

> 專案開啟 GitHub Pages 後,可直接線上瀏覽:<https://maxchl.github.io/card-dispenser-kiosk-dotnet/flow.html>
> (或把 `docs/flow.html` 下載後用瀏覽器開啟。)

> 誠信聲明:飯店電子鎖廠商的 encoder SDK 通常未公開文件,本專案的 encoder
> 介面為**代表性抽象**,非任何廠商的真實專有 API;採用的是可查證的真實架構模型
> (離線 autonomous 運作、Mifare Classic + DESFire、設定卡佈建、飯店 PMS + API 整合)。

## 情境說明

一台飯店自助報到 kiosk 內建收發卡一體機,旅客完成線上/現場報到後,系統呼叫發卡
流程 orchestrator,驅動發卡機完成:從卡匣抽出一張空白卡 → 讀取確認可用 → 寫入
本次入住的房卡資料 → 回讀驗證 → 出卡給旅客。過程中任何一步都可能失敗(卡匣空、
卡片卡住、讀寫失敗、旅客不取卡、機台通訊中斷……),流程必須能正確地重試、換卡、
吞卡或安全中止,不能卡死、也不能無限重試把整個卡匣耗光。

## Mifare Classic 1K 卡片結構簡介

飯店智慧門鎖常採 13.56MHz 高頻卡,本模擬以業界最常見的 **Mifare Classic 1K**
為主要建模對象:

- 容量 1024 bytes,劃分為 **16 個 sector**,每 sector **4 個 block**,每 block **16 bytes**。
- 每個 sector 的 **block 3(sector trailer)** 存放 `Key A(6B) + Access Bits(4B) + Key B(6B)`,
  讀寫該 sector 的任何 block 前都必須先用 Key A 或 Key B 通過驗證(authenticate)。
  這是 Mifare Classic 最基本、也相對脆弱的安全模型——單一 sector 的金鑰外洩,
  該 sector 就完全失守。
- **sector 0 的 block 0** 是製造商區塊(UID + BCC + SAK + ATQA + 製造商資料),
  出廠寫入、**唯讀**,無法透過一般寫卡流程改寫。
- 出廠預設金鑰為 `FF FF FF FF FF FF`(6 bytes 全 0xFF),這是業界公開已知的值,
  空白卡都是這組金鑰。**正式環境上線前必須依卡片 UID 做金鑰分散(key
  diversification),絕不可沿用出廠預設金鑰**,否則任何人持有讀卡機就能直接驗證
  通過、竄改卡片資料。本模擬的空白卡沿用預設金鑰是刻意示範「出廠狀態」,程式碼中
  有明確註解標示。

本專案的房卡資料固定寫入 **sector 1 的 block 4~6**(sector 內 blockInSector 0~2,
block 3 是該 sector 的 trailer),對應 `CardDispenser.Core/Models/HotelKeyData.cs`
定義的 48-byte 編碼格式(房號、入住/退房時間、門禁權限旗標、電梯樓層遮罩、CRC16
校驗碼)。

### DESFire:3-pass 互相認證已實作

`CardDispenser.Core/Models/DesfireCard.cs` 提供 DESFire 的輕量抽象(application/
file/AES 金鑰概念),`CardType` 列舉可分辨卡片是 Mifare Classic 還是 DESFire。與
Mifare Classic 的關鍵差異:

| | Mifare Classic 1K | DESFire |
|---|---|---|
| 認證方式 | 單一 sector 金鑰比對,驗證通過即可讀寫 | 挑戰-應答式**雙向互相認證**(3-pass mutual authentication) |
| 權限粒度 | 以 sector 為單位 | application / file 架構,可對每個 file 個別設定讀/寫/讀寫/改鍵權限 |
| 金鑰演算法 | 固定的 Crypto-1(已知有弱點) | 支援 DES / 3DES / AES,安全強度視配置而定 |

本模擬**已實作 AES128 路徑的 3-pass 挑戰-應答互相認證**(見 Part C),流程對應
`DesfireCard.AuthenticateStep1` / `AuthenticateStep2`(卡片視角)搭配
`DesfireAuthenticator.Authenticate`(讀卡機視角):

1. 讀卡機發起驗證,卡片產生 16-byte 亂數 `RndB`,以金鑰加密回傳 `E_k(RndB)`。
2. 讀卡機解密取得 `RndB`,自產亂數 `RndA`,送出 `E_k(RndA ‖ rotl(RndB))`
   (`rotl` = 左旋 1 byte)。
3. 卡片解密驗證 `rotl(RndB)` 是否與自己記住的一致——不一致即代表對方不持有正確
   金鑰,驗證失敗;一致則回傳 `E_k(rotl(RndA))`。
4. 讀卡機解密驗證 `rotl(RndA)` 是否相符,通過才視為雙向互相認證成功。
5. 雙方各自用 `RndA[0..4] ‖ RndB[0..4] ‖ RndA[12..16] ‖ RndB[12..16]` 衍生出同一把
   AES-128 session key,且過程中雙方都只在密文形式下交換過挑戰值。

實作用 `System.Security.Cryptography.Aes`(CBC + 零 IV)模擬「金鑰加密挑戰值」這個
結構,**刻意省略**真實 DESFire 3-pass 協定裡的 CMAC、command counter、secure
messaging 等細節,也**不**實作完整的 application/file 權限矩陣(file 層級僅示範
「通過驗證才能讀寫」)——這些仍是本模擬的簡化範圍,程式碼上有明確註解標示。

## 完整發卡流程與 fallback 處置

| # | 狀態 | 說明 | 失敗時的 fallback |
|---|---|---|---|
| 1 | `Idle` | 待機 | - |
| 2 | `StockCheck` | 偵測卡匣庫存 | 無卡 → `OutOfStock`,通知櫃檯補卡、中止交易 |
| 3 | `Dispensing` | 馬達推卡到讀寫位置,閘門 sensor 判斷是否到位 | 重推(< `MaxDispenseRetries`)仍失敗 → `Jam`,停機告警(**不會**無限重推,因為每次推卡都會有一張卡physically 離開卡匣、卡進機構,盲目重推只會疊卡) |
| 4 | `Positioning` | 定位到 RF/IC 讀寫頭 | 純機構動作,demo 中視為必定成功 |
| 5 | `Reading` | 讀卡:辨識型別、讀 UID、驗證是否為出廠空白可用卡 | 重讀(< `MaxReadRetries`)仍失敗/非空白/未知卡 → 吞卡至回收艙(`CaptureFaulty`)→ 換新卡回到 `StockCheck` |
| 6 | `Writing` | authenticate sector → 寫入房卡資料 | 重寫(< `MaxWriteRetries`)仍失敗 → 吞卡 → 換新卡 |
| 7 | `Verifying` | 回讀剛寫入的 block,先驗證 CRC 結構完整性、再比對內容 | 不一致視為寫卡失敗,回到 `Writing` 重試;重試仍失敗 → 吞卡 → 換新卡 |
| 8 | `Ejecting` | 推送出卡,呈現在出卡口 | - |
| 9 | `WaitGuestTake` | 等待旅客取卡(有逾時) | 逾時未取 → `Recapture`,基於安全考量吞卡回收 + 通知櫃檯 |
| 10 | `Completed` | 交易完成 | 庫存 -1、回傳交易結果 |

其他錯誤/fallback 狀態:

- **`CaptureBinFull`**:回收艙已滿。**吞卡前一定會先查詢回收艙是否已滿**(不論是
  異常吞卡還是旅客未取的吞卡),滿了就不會再吞,直接停機告警,避免把卡片硬吞進
  已滿的回收艙造成二次機構故障。
- **`CommunicationError`**:與發卡機控制板通訊逾時(`HardwareCommunicationException`)。
  無論在流程的哪一步發生,都會被外層攔截,讓流程收斂到這個安全狀態並中止交易,
  不會卡死在中間態、也不會對通訊本身做無限重試。
- **`Aborted`**(換卡次數超過交易上限):讀寫持續失敗會不斷觸發「吞卡換新卡」,
  但這個換卡動作本身也有交易層級的上限(`MaxCardSwapsPerTransaction`)——避免
  無限換卡把整個卡匣的卡都吞光,超過上限就直接中止交易並告警。

### 務必落實的不變式(程式碼位置)

| 不變式 | 實作位置 |
|---|---|
| 每個機械/IO 動作各自有 `MaxRetries` + 退避延遲 | `CardDispenser.Core/CheckInCardIssuer.cs` 的 `TryDispenseAsync` / `TryReadAsync` / `TryWriteAndVerifyAsync`,搭配 `IssuerOptions` 的 `MaxDispenseRetries` / `MaxReadRetries` / `MaxWriteRetries` / `RetryBackoff` |
| 交易層級「換卡次數上限」,避免無限換卡吞光卡匣 | `CheckInCardIssuer.IssueCardAsync` 主迴圈中的 `swapCount` 計數 + `IssuerOptions.MaxCardSwapsPerTransaction` 判斷 |
| 吞卡前一定先檢查回收艙是否已滿 | `CheckInCardIssuer.CaptureCurrentCardAsync` 開頭呼叫 `_hw.IsCaptureBinFullAsync()` |
| 通訊逾時要能收斂到安全狀態,不可卡死 | `CheckInCardIssuer.IssueCardAsync` 外層 `catch (HardwareCommunicationException)`,統一轉入 `CommunicationError` 並回傳失敗結果 |

## 方案結構

```
CardDispenserKioskSim.sln
CardDispenser.Core/                 # class library:業務模型 + 硬體介面 + orchestrator
  Models/
    CardType.cs                     # 卡別列舉(Mifare Classic 1K / DESFire / Unknown)
    AccessFlags.cs                  # 房卡門禁權限旗標([Flags])
    HotelKeyData.cs                 # 房卡業務資料 + 48-byte 編碼/解碼 + CRC16 校驗
    MifareClassicCard.cs            # Mifare Classic 1K 記憶體結構模擬(sector/block/authenticate)
    DesfireCard.cs                  # DESFire 輕量抽象,含 3-pass 互相認證(卡片視角)
    DesfireAuthenticator.cs         # DESFire 3-pass 互相認證(讀卡機視角)+ AES 加解密/session key 衍生工具
    CardStock.cs                    # 卡匣庫存快照
    DispenseState.cs                # 發卡流程狀態機列舉
    FaultCode.cs                    # 故障分類代碼
    IssueResult.cs                  # 一次發卡交易的最終結果
  Hardware/
    ICardDispenserHardware.cs       # 發卡機硬體抽象介面(全 async)
    HardwareContracts.cs            # 各硬體呼叫的回傳型別(GateResult/ReadResult/...)
    HardwareCommunicationException.cs
  Offline/                          # 離線 data-on-card 模型(飯店離線鎖擬真,Part B)
    CardSectors.cs                  # 房卡資料 sector / 離線信封 sector 的共用編號
    GuestCardCredential.cs          # 離線房卡憑證(PropertyId + CardSequence + HotelKeyData + Signature)
    CredentialSecurity.cs           # HMAC-SHA256 簽章/驗章(代表性模擬,非真實廠商演算法)
    SiteKeyRing.cs                  # PropertyId -> site key 對應(demo 記憶體版,正式應走 HSM/KMS)
    IHotelCardEncoder.cs             # 代表性 encoder SDK 抽象介面(非任何廠商真實 API)
    SimulatedHotelCardEncoder.cs     # IHotelCardEncoder 的記憶體模擬實作
    ProgrammingCard.cs              # 設定卡(母卡):佈建門鎖用,含門類型(DoorKind)/設施旗標/電梯樓層
    SimulatedOfflineLock.cs         # 離線門鎖模擬:六道離線驗證(含門群/樓層權限判斷)+ 序號蓋卡機制
    DoorKind.cs                     # 門類型列舉(GuestRoom / Facility / ElevatorFloor)
  Util/
    Crc16.cs                        # CRC-16/CCITT-FALSE 計算
  IssuerOptions.cs                  # 各項 MaxRetries / 換卡上限 / 逾時參數
  CheckInCardIssuer.cs              # 發卡流程 orchestrator(狀態機 + retry/fallback)
CardDispenser.Sim/                  # console app:模擬硬體 + demo 執行器
  FaultInjectionConfig.cs           # 故障注入設定(讓每個 fallback 分支可穩定重現)
  SimulatedDispenserHardware.cs     # ICardDispenserHardware 的記憶體模擬實作(可選疊加離線信封寫入)
  Program.cs                        # Part A:9 個發卡情境;Part B:B1~B9 離線模型情境;Part C:DESFire 3-pass 認證(C1/C2)
```

## 如何 Build / Run

前置需求:安裝 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)(LTS;各專案皆以 `net10.0` 為目標)。

可選:關閉 CLI 遙測與歡迎訊息:

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

Build 整個 solution:

```bash
cd card-dispenser-kiosk-sim
dotnet build
```

執行 demo(依序跑完 Part A 9 個情境 + Part B 9 個離線模型情境 + Part C 2 個 DESFire 3-pass 情境):

```bash
dotnet run --project CardDispenser.Sim
```

## 測試

```bash
dotnet test
```

請在 repo 根目錄執行 `dotnet test`(`global.json` 會選用 Microsoft.Testing.Platform);IDE 需啟用 MTP 支援,或改用隨附的 VSTest adapter。

`CardDispenser.Tests`(xUnit v3,跑在 Microsoft.Testing.Platform 上;repo 根目錄的 `global.json` 已啟用此 runner,需要 .NET 10 SDK)以 Sim 的故障注入為輸入,涵蓋:

- 發卡 fallback:機構卡住有上限重試(Jam)、壞卡吞卡換新卡與單一交易換卡上限、庫存耗盡與回收艙已滿即停機。
- 離線鎖:合法憑證開門、六道驗證(佈建/簽章/案場/效期/門禁權限/序號)各自拒絕對應的失敗案例、新序號卡蓋掉舊卡(免連網退房)、`CancelCard` 作廢。
- 門群與電梯樓層權限、DESFire 3-pass 認證(正確金鑰雙方衍生相同 session key、錯誤金鑰失敗)、`Crc16`(CRC-16/CCITT-FALSE,檢查值 `"123456789"` → `0x29B1`)。

測試不依賴系統時間與亂數(鎖端時鐘由設定卡指定),結果可重現。

## 預期 demo log(節錄)

```
---- 情境 1:正常發卡成功(happy path) ----
[..] <StockCheck        > 庫存剩餘 5/5 張
[..] <Dispensing        > 推卡到位(第 1 次嘗試): 閘門 sensor 偵測到卡到位(本交易第 1 張卡)
[..] <Reading           > 讀卡成功(第 1 次): 卡別=MifareClassic1K, UID=0401BEEF
[..] <Writing           > 寫入房卡資料(第 1 次): 房號=1203
[..] <Verifying         > 回讀驗證一致
[..] <Ejecting          > 推送出卡至出卡口
[..] <WaitGuestTake     > 等待旅客取卡,逾時秒數 1
[..] <Completed         > 發卡完成,房號 1203
結果: 成功 | 最終狀態=Completed | 故障碼=None | 本交易消耗卡片數=1

---- 情境 7:換卡次數超過交易上限 -> 中止交易 ----
...(連續 3 張卡讀卡失敗、依序吞卡)
[..] <Aborted           > 換卡次數超過交易上限,中止交易並告警
結果: 失敗 | 最終狀態=Aborted | 故障碼=CardSwapLimitExceeded | 本交易消耗卡片數=3
```

程式共內建 9 個情境(1~7 為規格要求,8、9 為額外補充,完整涵蓋所有 fallback 狀態):

1. 正常發卡成功
2. 卡匣空 → `OutOfStock`
3. 讀卡失敗 → 吞卡 → 換新卡成功
4. 寫卡後回讀驗證不一致 → 吞卡 → 換新卡成功
5. 旅客逾時未取 → 吞卡回收
6. 推卡卡住 → 停機告警(`Jam`)
7. 換卡次數超過上限 → 中止交易(`Aborted` / `CardSwapLimitExceeded`)
8.(附加)回收艙已滿 → 無法吞卡,停機告警(`CaptureBinFull`)
9.(附加)機台通訊逾時 → 進入安全狀態並中止(`CommunicationError`)

## 離線 data-on-card 模型(飯店離線鎖擬真)

在 Part A 的發卡流程之上,Part B 疊加了一層更貼近**飯店智慧門鎖**真實產品架構的
模型:離線 data-on-card。程式碼全部放在
`CardDispenser.Core/Offline/` 命名空間,採加法式設計——Part A 原有 9 個情境、
`ICardDispenserHardware` 介面、`CheckInCardIssuer` orchestrator 都完全不受影響。

### 離線鎖運作原理

- **鎖不連網。** 飯店這類離線門鎖是 autonomous(自主式)離線鎖,沒有網路,
  也沒有跟後端即時同步。門禁權限、入住/退房效期,全部**寫在卡片上**,鎖只在
  刷卡當下用自己的離線電路驗證這張卡。
- **鎖有自己的時鐘。** 效期是否有效,鎖靠自己的 RTC(即時時鐘)離線判斷,不會去
  問任何伺服器「現在幾點、這張卡還有效嗎」。本模擬對應
  `SimulatedOfflineLock.ClockUtc`。
- **序號蓋卡完成退房,免連網。** 每張卡都帶一個單調遞增序號
  (`GuestCardCredential.CardSequence`)。旅客退房、新旅客入住同一間房時,系統發一張
  序號更大的新卡;鎖第一次讀到新卡時,就把「已記錄序號」更新成新卡的序號
  (`SimulatedOfflineLock.PresentCardAsync` 最後一步)。從此舊卡序號永遠小於已記錄
  序號,再刷會被拒絕——**不需要鎖連網、也不需要人去碰鎖**,舊卡片自動失效。
- **設定卡(母卡 / programming card)佈建鎖。** 鎖的身分(案場代碼、門/房代號)、
  金鑰、時鐘,全部靠一張特殊的設定卡寫入——這張卡由工程人員持卡到現場逐一設定
  每個門鎖(`SimulatedOfflineLock.ProvisionWith`)。沒被佈建過的鎖會拒絕任何卡片
  (見情境 B1)。
- **門群 / 樓層權限已實作(`DoorKind`)。** 一把鎖除了記房號,還記自己的「門類型」
  (`ProgrammingCard.Kind` / `SimulatedOfflineLock.Kind`),不同門類型看卡片上不同
  的授權資料:
  - `GuestRoom`(客房門):需卡片 `AccessFlags.GuestRoom` 旗標 + 房號與本門相符。
  - `Facility`(設施門,如健身房/泳池/SPA):需卡片具備本門要求的
    `AccessFlags`(`ProgrammingCard.RequiredFacility`)。
  - `ElevatorFloor`(電梯樓層門):需卡片具備 `AccessFlags.Elevator` 旗標,且本門
    樓層(`ProgrammingCard.ElevatorFloor`,1-based)落在卡片的 `ElevatorFloorMask`
    位元內。

  判斷邏輯集中在 `SimulatedOfflineLock.CheckDoorAuthority`,是六道離線驗證的第 5
  關,取代舊版「只比對房號」的簡化寫法(見情境 B8)。
- **卡片作廢已實作。** `SimulatedHotelCardEncoder.CancelCardAsync` 會抹除離線信封
  的格式辨識碼,使該卡日後任何讀取都判定為結構無效,對應「卡片印錯 / 旅客臨時
  取消入住」需要立即讓已發出的卡失效的場景(見情境 B9)。

### 代表性 encoder SDK 聲明

**真實飯店鎖廠商的 encoder SDK 通常沒有公開文件。** 本專案的
`IHotelCardEncoder` / `SimulatedHotelCardEncoder` 是依可查證的產業通用架構(飯店
encoder 透過 USB/序列埠連線讀寫卡片,典型支援 detect / read / issue / cancel /
event-log 這組操作)自行設計的**代表性抽象**,型別與方法命名在 XML 註解上都明確
標註「representative abstraction of a hotel card encoder SDK」。**這不是任何廠商
的真實 API**,也未曾參考任何廠商的真實型別或方法簽章,純粹用來示範「離線
data-on-card」這個可查證的真實架構模型:離線 autonomous 鎖、Mifare Classic /
DESFire 卡片、設定卡佈建、以及背後典型會有的 飯店 PMS + API 串接關係。

離線安全信封的簽章機制(`CredentialSecurity`,用 HMAC-SHA256)同樣是代表性模擬——
真實飯店鎖廠商多使用其專有加密演算法,細節未公開,本專案沒有、也無從重建。

### 卡片上的資料佈局

沿用既有 Mifare Classic 1K sector/block 結構,新增一個 sector 放離線信封:

| Sector | 內容 | 說明 |
|---|---|---|
| Sector 1(block 4~6) | `HotelKeyData`(既有格式,不變) | 房號、入住/退房時間、門禁權限旗標、電梯樓層 |
| Sector 2(block 8~10) | 離線安全信封(`GuestCardCredential.ToPayload`) | PropertyId(案場代碼)+ CardSequence(遞增序號)+ Signature(HMAC 簽章),格式仿照 `HotelKeyData` 的 magic/version/CRC 慣例 |

兩個 sector 合起來才是完整的離線憑證,鎖與 encoder 都各自認證兩個 sector 之後,
把讀回的 block 分別交給 `HotelKeyData.TryParse` 與 `GuestCardCredential.TryParse`
重組。

### Part B 情境列表

| # | 情境 | 預期結果 |
|---|---|---|
| B1 | 未佈建的鎖 | 拒絕任何卡片(尚未用母卡設定) |
| B2 | 母卡佈建門鎖 | PropertyId、房號 1203、site key、校時完成,`IsProvisioned=True` |
| B3 | 發房卡(seq=1) | 鎖驗證放行,開門 |
| B4 | 退房重發新卡(seq=2) | 新卡放行;**舊卡(seq=1)再刷被拒**(序號已被較新卡蓋掉)——這就是離線退房蓋卡機制 |
| B5 | 過期卡(ValidTo 早於鎖 RTC) | 拒絕(已過期或尚未生效) |
| B6 | 錯誤案場金鑰 / 竄改簽章 | 簽章驗證失敗拒絕(encoder 自身回讀就已驗章失敗,門鎖端再次獨立驗證同樣失敗) |
| B7(壓軸整合) | 報到機跑完整機構流程發卡(`SimulatedDispenserHardware` 帶 `PropertyId`/`SiteKeyRing`/序號產生器)→ 取 `LastIssuedCard` → 交給已佈建的房門鎖 `PresentCardAsync` | 開門成功——串起「自助報到 → 房門開啟」完整路徑 |
| B8 | 門群 / 樓層權限:佈建房門(`GuestRoom`)、健身房門(`Facility`,需 `Gym`)、電梯 12 樓門(`ElevatorFloor`)三把鎖;基本房卡(僅 `GuestRoom`,樓層遮罩只含 3/4 樓)與套房卡(`GuestRoom\|Elevator\|Gym`,樓層遮罩含 3/4/12 樓)分別刷三道門 | 基本房卡僅房門放行、健身房與電梯 12 樓皆拒絕;套房卡三道門全部放行 |
| B9 | 卡片作廢:發一張有效房卡並驗證可開門 → `encoder.CancelCardAsync` 作廢 → 同一張卡再刷 | 作廢前開門成功;作廢後因離線信封結構失效被拒——對應「卡片印錯 / 旅客臨時取消入住」的作廢流程 |

其中 B3~B6、B8、B9 透過 `SimulatedHotelCardEncoder` 發卡(展示 Connect /
DetectCard / IssueGuestCard / ReadCredential / CancelCard / ReadEventLog),B7 則
刻意不經過 encoder,直接由 `SimulatedDispenserHardware` 依 Part A 既有的報到機
流程寫卡,示範「離線信封」是可以疊加在既有房卡發卡流程之上、而不必更換整條
orchestrator 的路徑。

新增檔案(全部在 `CardDispenser.Core/Offline/`):`CardSectors.cs`、
`GuestCardCredential.cs`、`CredentialSecurity.cs`、`SiteKeyRing.cs`、
`IHotelCardEncoder.cs`、`SimulatedHotelCardEncoder.cs`、`ProgrammingCard.cs`、
`SimulatedOfflineLock.cs`、`DoorKind.cs`。`CardDispenser.Sim/SimulatedDispenserHardware.cs`
與 `Program.cs` 為既有檔案的加法式修改。

## DESFire 3-pass 互相認證(Part C)

在 Part A/B 之外,Part C 獨立示範 DESFire 的挑戰-應答式雙向互相認證(見前面
「DESFire:3-pass 互相認證已實作」一節的協定說明),新增檔案
`CardDispenser.Core/Models/DesfireAuthenticator.cs`(讀卡機視角),並改寫既有
`CardDispenser.Core/Models/DesfireCard.cs`(卡片視角,`Authenticate` 方法升級為
`AuthenticateStep1` / `AuthenticateStep2`)。

| # | 情境 | 預期結果 |
|---|---|---|
| C1 | 正確金鑰:走完整 3-pass 流程 | 認證通過;讀卡機(`DesfireAuthenticator.Authenticate` 回傳值)與卡片(`DesfireCard.SessionKey`)各自衍生出**相同**的 session key(斷言檢查);驗證後可寫檔/讀檔且內容一致 |
| C2 | 錯誤金鑰:讀卡機用錯的金鑰跑同一套流程 | Step2 卡片端驗證失敗,`Authenticate` 回傳 `(false, null)`;卡片 `SessionKey` 仍為 `null`;未驗證狀態下寫檔/讀檔皆被拒 |

Demo 金鑰使用明碼字串常數(`demo-desfire-key`),程式碼中已註明「僅供模擬展示,
正式環境須由 HSM/KMS 管理並做金鑰分散」。

## 模擬 vs 正式硬體對照

| 面向 | 本模擬 | 正式硬體 |
|---|---|---|
| 硬體通訊 | `SimulatedDispenserHardware` 記憶體狀態 + `Task.Delay` 模擬耗時 | 透過 RS-232 / USB 走廠商指令集(例如 CRT-591 類收發卡機常見的二進位指令協定),需處理鮑率、逾時、CRC/checksum 校驗、指令重送 |
| 卡片金鑰 | 空白卡固定用出廠預設 Key `FFFFFFFFFFFF` | 上線前必須做 key diversification(依 UID 或發卡序號衍生每張卡專屬金鑰),絕不可全卡匣共用同一組金鑰 |
| 卡匣庫存 | 記憶體計數器 `_hopperCount` | 實體光電/機械式 sensor 偵測卡匣有無卡片,並回報給後端庫存服務(供補卡排程、耗用量報表使用) |
| 回收艙滿載 | 記憶體計數器 `_captureBinCount` 與 `CaptureBinCapacity` 比較 | 實體滿載 sensor(常見為微動開關或光電式),觸發後需現場人員清空回收艙才能恢復發卡 |
| 卡片辨識/驗證 | Mifare Classic 軟體物件模擬金鑰比對;DESFire 已模擬 AES128 3-pass 挑戰-應答(見 Part C),但省略 CMAC / command counter / secure messaging | 讀卡機模組實際發送 RF 指令與卡片交握,依卡別走 Crypto-1(Classic)或完整 3-pass mutual authentication + secure messaging(DESFire) |
| 故障注入 | `FaultInjectionConfig` 明確指定第幾張卡/第幾次動作失敗,確保 demo 可重現 | 正式環境的故障是真實的機構磨損、粉塵卡紙、RF 干擾等,無法預先指定,只能靠日誌與 sensor 歷史資料回溯 |

## 安全性說明

- 空白卡使用 Mifare Classic 出廠預設金鑰 `FF FF FF FF FF FF` 是公開的業界預設值,
  本模擬使用它是為了示範「出廠狀態」;**正式環境上線前必須改用金鑰分散後的專屬
  金鑰**,程式碼中已於 `MifareClassicCard.FactoryDefaultKey` 加上對應註解。
- 專案中沒有任何硬編碼的真實密碼、金鑰或其他敏感資料。

## 授權

本專案以 [MIT License](LICENSE) 授權。
