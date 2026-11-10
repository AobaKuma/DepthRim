# DMSE 調整計畫：讓 DepthRim 少掉 Harmony 補丁

整理日期：2026-11-10。目的：盤點 DepthRim 對 DMSE 的 Harmony 補丁與反射存取，找出哪些能靠直接修改 DMSE（加擴充點或修正行為）拿掉，並排出執行順序。

## 0. 實作結果（2026-11-10）

階段 0～6 都已實作。DepthRim 打在 DMSE 上的 Harmony 補丁從 17 個降到 **0 個**，讀 DMSE 私有成員的反射從 3 處降到 **0 處**；剩下的 14 個補丁都打在原版上（§5）。兩邊都還沒 commit。

| 項目 | DMSE 加的 | DepthRim 改用 | 和計畫不同的地方 |
|---|---|---|---|
| A1 | （不用改） | `SonarLayerModifier`，註冊在 `AirDefensePipeline` | 魚雷彈體原本的 `ModifyDefenseAsTarget` 也併進同一個修正，免得打兩次折；沒有地圖的節點（海盜潛艇）用它自己的巡航深度當觀測方深度 |
| B1 | `PlatformRemovalUtility.Remove` 讀 `TransferEnvironment.VoidTerrain(map)` | 刪掉 `Patch_PlatformRemoval` | — |
| B2 | `CompProperties_MissileBase.partOptions`（`MissileBasePartOption`：類別＋可選部件） | 魚雷艇、海盜潛艇的 XML；刪掉 `Patch_EnemyTorpedoLoadout` 與 `EnemyTorpedoLoadoutExtension` | — |
| B3 | `CompProperties_MissileBase.ConfigErrors`：`incomingWorldObjectDef` 的類別不是 `WorldObject_IncomingMissile` 時報錯 | 保留 `DepthRim_WorldObject_EnemyTorpedo` | 沒有改成接受 `MissileWorldObject` 基底：兩個類別的抵達流程不同，改成載入時就報錯，不會等到第一次開火才當掉 |
| B4 | `relocatedLetterLabelKey`／`relocatedLetterTextKey` | 「魚雷艇離開」「海盜潛艇離開」的信件 | — |
| B5 | `MissileBehaviorSet.Get<T>()` | `TorpedoUtility.BehaviorOf` | — |
| B6 | （由 E4～E6 取代） | 刪掉 `AccessTools.TypeByName("DMSE.ShipSeparationUtility")` | — |
| C1～C4 | `CombatDomain`（Air／Underwater）：`MissileBodyDef.domain`、`BVRTargetProps.domain`、`CompProperties_FireControlRadar.domains`、`CompProperties_TerminalCIWS.domains`、`IDomainAwareEffector`、`AirEngagement.Domain`、`BVRTarget.domain`（存檔）。世界層 `NodeFor` 要節點的火控涵蓋領域，`BestRadar`、`HasInterceptorAmmoFor`、`FireAutonomous` 比對領域；地圖層的火控、攔截發射器與 CIWS 也比對 | 魚雷、反魚雷魚雷、火控聲納、突擊艇設成水下；近防砲實作 `IDomainAwareEffector`；刪掉 `Patch_TorpedoInterceptors`、`Patch_TorpedoInterceptorLaunchers` | 「魚雷只給海洋層節點接戰」不再用圖層判斷，改成「節點要有能導引水下的火控」 |
| D1 | `MissileBodyDef.engageWorker`（`MissileEngageWorker`） | `TorpedoEngageWorker`；刪掉 `Patch_TorpedoEngage` | — |
| D2 | `IBVRPayloadHolder` | 突擊艇實作；刪掉 `Patch_BoardingCraftPawns` | — |
| D3 | `IInterceptResolver`（`BVRTarget.SpawnDebris` 先問它） | 突擊艇實作沉沒；刪掉 `Patch_BoardingCraftSinks` | — |
| D4 | `CompProperties_WorldAirDefense.ignoreNetworkResearch` | 海盜潛艇的 XML；刪掉 `Patch_PirateSubmarineNodes` 與 `nodes` 欄位的反射 | 選 (a)。另外發現 DMSE 今天的效能修改把節點分成完整清單與掃描清單，舊補丁只加進完整清單，其實已經失效了 |
| E1～E8 | `TransferEnvironmentExtension.routeWorker`／`energyWorker`／`landingWorker`（`TransferRouteWorker`、`TransferEnergyWorker`、`TransferLandingWorker`，預設等於現狀）；`TransferFlightMath.FuelRequired(survey, distance)` 給整備檢查、出發與估算共用 | `OceanRouteWorker`、`SubmarineEnergyWorker`、`SubmarineLandingWorker`；刪掉 8 個補丁與執行緒靜態旗標 | — |
| F1 | `IBVRContactLabel`、`BVRTarget.DisplayLabel`（「來襲空投」警報用它） | 突擊艇在抵達前 1200 tick 以外顯示「未知聲納接觸」 | — |

DMSE 的單元測試新增 `ExtensionPoints`（21 項），檢查所有擴充點的預設值都等於加入前的行為。DMSE 那邊的擴充點說明寫在 DMSE 的 `docs/外部模組擴充點.md`（DMSE 的 `.gitignore` 排除整個 `docs/`，所以這份只在本機）。

**測試結果：**

| 測試 | 結果 |
|---|---|
| DMSE 單元測試 | 1887 項全過（原本 1866 項＋擴充點 21 項）；內容檢查器的 3 個「missing def translation」改動前就有 |
| DepthRim 單元測試 | 3673 項全過 |
| DepthRim 遊戲內 | H1、S2、S3、S4、N1、T1、T2、B1、B2、X1 全過（T2 新增領域分工 4 項，B1 新增波次分類 3 項） |
| DMSE 遊戲內，不載 DepthRim，完整一輪 | 改動前（目前的 DMSE commit）116 過 41 失敗；改動後 122 過 35 失敗。只在改動後失敗的是 K2、K9，兩個都是「找不到殖民者」（前面的測試把殖民者打死了），不是 DMSE 邏輯；只在改動前失敗的有 8 個。W1、W2、W8 等在兩邊都失敗，是 DMSE 目前 commit 本身的狀態 |
| DMSE 遊戲內，載著 DepthRim | 玩家的模組清單現在有 DepthRim，DMSE 的測試也會載它。W4 的末端窗口差 21 tick（容許 20）：DepthRim 載入後遊戲較慢，測試觀察到抵達的時間晚了；不載 DepthRim 時通過 |

DMSE 的遊戲內驗收目前有大量既有失敗與不穩定的測試，要另外整理（和這次的擴充點無關）。

## 1. 總覽

DepthRim 目前有 **31 個 Harmony 補丁**：

| 類別 | 數量 | 能不能靠 DMSE 解決 |
|---|---|---|
| 打在 DMSE 上 | 17 | 1 個現在就能改用 DMSE 既有 API；16 個需要 DMSE 加擴充點 |
| 打在原版上 | 14 | 不行（音效、水位畫面、區域重建、Pawn 渲染、地形、世界圖層，見 §5） |

另外有 **3 處反射存取** DMSE 的私有成員，以及 **6 個已知問題**（[待補內容清單.md](待補內容清單.md) §4）也是 DMSE 那邊的行為，改 DMSE 就能解決。

全部做完後，DepthRim 打在 DMSE 上的補丁從 17 個降到 0 個，反射存取降到 0 處。

## 2. 逐項對照

「階段」對應 §3 的執行順序。

### 2.1 現在就能改（不用動 DMSE）

| # | DepthRim 補丁 | 現在做什麼 | 改法 |
|---|---|---|---|
| A1 | `Patch_SonarLayer`（postfix `BVRNode.DetectionRange`） | 溫躍層遮蔽：觀測方和目標在溫躍層不同側時打折 | DMSE 已有全域修正鏈 `AirDefensePipeline.Add(AirDefenseModifier)`，查詢帶 `Stat`、`Site`、`Track`。改寫成一個 `AirDefenseModifier`，只處理 `Stat == Detection`。魚雷那條已經走 `TorpedoBehaviorProps.ModifyDefenseAsTarget`，兩者合併成同一個修正 |

### 2.2 小修正（DMSE 改一兩個地方）

| # | DepthRim 補丁／問題 | DMSE 要改的 | 拿掉的 |
|---|---|---|---|
| B1 | `Patch_PlatformRemoval`（postfix `PlatformRemovalUtility.Remove`） | 拆平台最後一行寫死 `SetTerrain(c, TerrainDefOf.Space)`。改成讀地圖所在圖層的 `TransferEnvironmentExtension.voidTerrain`（DMSE 已有這個欄位），沒有才用太空 | 1 個補丁 |
| B2 | `Patch_EnemyTorpedoLoadout`（postfix `WorldObjectComp_MissileBase.BuildFireConfig`） | `CompProperties_MissileBase` 只能指定彈體、用預設部件。加一個 `partOptions`（每個部件類別一組可選部件，發射時隨機），或直接接受一份 `MissileDesignBlueprint` 清單 | 1 個補丁、`EnemyTorpedoLoadoutExtension` |
| B3 | 魚雷艇需要另一份航跡 Def `DepthRim_WorldObject_EnemyTorpedo` | `IncomingMissileUtility.Make` 把世界物件強制轉成 `WorldObject_IncomingMissile`，玩家發射管用的 `ScorerProjectile_WorldObject` 就會當掉。改成接受 `MissileWorldObject` 基底，或在型別不對時給 ConfigError | 一份重複的 WorldObjectDef |
| B4 | 魚雷艇、海盜潛艇「離開」時信件寫的是「陣地轉移」 | `CompProperties_MissileBase` 加 `relocatedLetterLabelKey`／`relocatedLetterTextKey`（已有 inspect 字串鍵的作法可以照抄） | 已知問題 1 項 |
| B5 | 反射：`MissileBehaviorSet.behaviors`（`TorpedoUtility.BehaviorOf`） | `MissileBehaviorSet` 加公開的 `T Get<T>() where T : MissileBehavior` | 1 處反射 |
| B6 | 反射：`ShipSeparationUtility`（`AccessTools.TypeByName`，給 B 階段的能源補丁用） | 把 `ShipSeparationUtility` 改成 public，或併入 §2.5 的能源擴充點後就不需要 | 1 處反射 |

### 2.3 攔截的「領域」（空中／水下）

DepthRim 目前用三個補丁硬把魚雷和空中飛彈分開，還留下兩個 DMSE 那邊管不到的問題。DMSE 加一個「領域」概念就能一次處理。

| # | DepthRim 補丁／問題 | 現在做什麼 |
|---|---|---|
| C1 | `Patch_TorpedoInterceptors`（postfix `BVRNode.PolicyAllows`） | 魚雷只給海洋層上的節點接戰 |
| C2 | `Patch_TorpedoInterceptorLaunchers`（postfix `CompMissileLauncher_Interceptor.CanEngage`） | 反魚雷魚雷只打魚雷，其他攔截彈不打魚雷 |
| C3 | 已知問題：DMSE 地圖層的末端 CIWS（防空砲）也會打抵達地圖的魚雷 | — |
| C4 | 已知問題：火控聲納也能導引 DMSE 防空飛彈打空中目標 | — |

**DMSE 要改的：**
- `BVRTargetProps`（或 `MissileBodyDef`）加 `domain`，預設 `Air`。DepthRim 的魚雷彈體設成 `Underwater`。
- 能接戰的一方各自宣告能打哪些領域，預設只有 `Air`：
  - `CompProperties_FireControlRadar`、`CompProperties_MissileLauncher`、`CompProperties_TerminalCIWS`、`CompProperties_WorldAirDefense`、`SettlementAirDefenseProps` 加 `domains`。
  - 攔截彈彈體加 `domain`。
- `BVRNode.ReadyEffector`、`BestRadar`、`CanReachAltitude`、`FireAutonomous` 和地圖層的 CIWS 選目標時，比對航跡的領域。

**拿掉的：** 2 個補丁，並解決 2 個已知問題。DepthRim 的近防砲（`CompTorpedoPointDefense`）改成在 `IAirEffector` 上宣告領域，不用自己判斷是不是魚雷。

### 2.4 擴充點：交戰判定、攔截結果、地圖層酬載

| # | DepthRim 補丁 | DMSE 要加的擴充點 | 拿掉的 |
|---|---|---|---|
| D1 | `Patch_TorpedoEngage`（prefix `MissileWorldUtility.CanEngage`） | 魚雷只走海路、射程用海路距離。`MissileBodyDef` 加 `engageWorker`（可被 XML `Class=` 替換的類別，預設就是現在的直線射程與跨圖層規則），`CanEngage` 先問它 | 1 個補丁 |
| D2 | `Patch_BoardingCraftPawns`（postfix `BVRDetection.PawnsIn`） | 現在只認得 `ActiveTransporter` 和 `CompTransporter`。加一個介面 `IBVRPayloadHolder { IEnumerable<Pawn> PawnsAboard }`，`PawnsIn` 先問它 | 1 個補丁 |
| D3 | `Patch_BoardingCraftSinks`（prefix `BVRTarget.SpawnDebris`） | 攔截成功時一律用殘骸空投艙裝遺體。加一個介面（例如 `IInterceptResolver`，掛在酬載或 `BVRTargetProps.interceptResolverClass` 上），`SpawnDebris` 先交給它，沒有才走預設 | 1 個補丁 |
| D4 | `Patch_PirateSubmarineNodes`（postfix 私有的 `RebuildNodes`＋反射 `nodes` 欄位） | NPC 抽象節點要玩家研究了雷達網（`DMSE_RadarNetwork`）才建立。改成以下其一：(a) `CompProperties_WorldAirDefense` 加 `ignoreNetworkResearch`；(b) 開放一個節點提供者註冊表（`BVRNodeProvider`），讓別的 mod 自己加節點 | 1 個補丁、1 處反射 |

D4 選 (a) 最小；(b) 比較通用，之後的海盜基地（7.5）也能用。

### 2.5 轉移飛行（潛艇）的環境擴充

潛艇建在 DMSE 的轉移飛行上，DepthRim 用了 8 個補丁把「太空裡走直線、燒電池當燃料」改成「海裡走海路、只算推進器用電、下錨有深度規則」。DMSE 已有 `TransferEnvironmentExtension`（掛在圖層上，現在只有 `voidTerrain`、`shipMapGenerator`、`shipWorldObject`），把這些行為也做成它的欄位或可替換的 worker：

| # | DepthRim 補丁 | 在 `TransferEnvironmentExtension` 加的 | 備註 |
|---|---|---|---|
| E1 | `Patch_TransferFlightMath_Distance`（prefix `TransferFlightMath.Distance`） | `routeWorker`：`Distance(from, to)` | 海路距離 |
| E2 | `Patch_TransferShip_BuildPath`（prefix `WorldObject_TransferShip.BuildPath`） | `routeWorker`：`BuildPath(from, to)` | 海路航線 |
| E3 | `Patch_TransferPlanning_ValidateDestination`（postfix `TransferPlanningUtility.ValidateDestination`） | `routeWorker`：`ValidateDestination(origin, tile, out reason)` | 只能去海路到得了的海洋格 |
| E4 | `Patch_SubmarineEnergy_FuelRequired`（prefix `TransferFlightMath.FuelRequired`） | `energyWorker`：`FuelRequired(...)` | 海洋層不收出發時的一次性燃料 |
| E5 | `Patch_SubmarineEnergy_Readiness`（`TransferReadiness.Check` 的 prefix＋finalizer） | 同上 | 現在靠執行緒靜態旗標包住呼叫範圍，E4 有擴充點後就不需要 |
| E6 | `Patch_SubmarineEnergy_Separate`（私有的 `ShipSeparationUtility.Separate`，用 `TargetMethod` 找） | 同上 | 同上；也拿掉 B6 的反射 |
| E7 | `Patch_SubmarineEnergy_Estimate`（postfix `TransferEstimate.For`） | `energyWorker`：`Estimate(survey)`，回傳需要的能量與最大航程 | 航程照推進器用電、電池存量算 |
| E8 | `Patch_SubmarineLanding`（prefix `TransferLandingUtility.BeginLanding`） | `landingValidator`：`CanLand(ship, tile, out reason)` | 下錨深度規則；也可以改成 `WorldObject_TransferShip` 的 virtual 方法讓子類別覆寫 |

**拿掉的：** 8 個補丁、1 處反射，以及執行緒靜態旗標這個脆弱的作法。

### 2.6 新功能需要的擴充點（不是拿掉補丁，是避免新增）

| # | 需求 | DMSE 要加的 |
|---|---|---|
| F1 | 地圖層來襲的突擊艇（BVR 波次）也要有「未知接觸」的聲納分類（[待補內容清單.md](待補內容清單.md) §3 M7） | 波次目標的顯示名稱與預警信件走一個可覆寫的 `labelWorker`，或在 `AirCombatEvents` 的 `TrackDetected` 事件允許改寫顯示名稱 |
| F2 | 海盜基地（7.5）要沿用抽象防空與導彈陣地 | 同 D4 的 (b)，加上 B2、B4 |

## 3. 執行順序

每個階段都是：先改 DMSE → 跑 DMSE 自己的測試（`DMSE.BVRTests`、`DMSE.BVRAcceptance`）→ DepthRim 拿掉對應補丁、改用新擴充點 → 跑 DepthRim 的單元測試與遊戲內驗收。DMSE 的預設行為要和現在一樣，DMSE 自己的遊戲內容不能有變化。

| 階段 | 內容 | 拿掉的補丁 | DepthRim 要跑的驗收 | 風險 |
|---|---|---|---|---|
| 0 | A1：溫躍層遮蔽改用 `AirDefensePipeline`（只改 DepthRim） | 1 | N1、T1 | 低 |
| 1 | B1～B6：小修正 | 2（B1、B2）＋2 處反射 | H1（平台拆除）、T2、X1 | 低：都是加欄位或改成讀既有欄位 |
| 2 | D2、D3、D4：酬載介面、攔截結果介面、節點提供者 | 3＋1 處反射 | B1、B2、X1 | 中：D4 動到節點重建，要確認 DMSE 的 NPC 警戒系統不受影響 |
| 3 | C1～C4：攔截領域 | 2，並解決 2 個已知問題 | T2、X1、B1（突擊艇攔截）；DMSE 的防空驗收全跑 | 中高：碰到 DMSE 所有接戰路徑，預設值一定要等於現狀 |
| 4 | D1：交戰判定 worker | 1 | T1、T2、X1 | 低 |
| 5 | E1～E8：轉移飛行環境擴充 | 8＋1 處反射 | S2、S3、S4（潛艇航行、下錨、推力） | 高：DMSE 太空轉移飛行的主流程，一定要跑 DMSE 的轉移飛行測試 |
| 6 | F1：波次分類 | 0（新功能） | 新增驗收 | 低 |

建議先做 0～2：風險低、立刻少掉 6 個補丁和 3 處反射。3 和 5 影響 DMSE 主流程，要排在 DMSE 自己有空回歸測試的時候。

## 4. 相容性

- **版本相依：** DepthRim 會依賴新版 DMSE 的擴充點。DepthRim 的 `About.xml` 要標最低 DMSE 版本，或在啟動時檢查擴充點存不存在（`AccessTools.TypeByName`／欄位存在），舊版 DMSE 就退回原本的補丁。過渡期保留補丁，等 DMSE 新版穩定後再刪。
- **存檔：** 新增的 `domain` 等欄位都是 Def 欄位，不進存檔。`IInterceptResolver`、`IBVRPayloadHolder` 是介面，也不影響存檔。轉移飛行的 worker 不存狀態。
- **其他 mod：** 預設值必須等於現在的行為，否則其他依賴 DMSE 的 mod 會變。

## 5. 不在這份計畫裡的補丁（打在原版上）

| 補丁 | 用途 |
|---|---|
| `Patch_SampleOneShot_TryMakeAndPlay`、`Patch_Sample_Volume` | 水中音效悶住 |
| `FloodingOverlay`（`PlaySettings.DoPlaySettingsGlobalControls`） | 水位疊加圖開關 |
| `Patch_RegionDirtyer_Walkability`、`_Thing`、`_All` | 牆體水壓：區域變動時重算 |
| `Patch_MouseoverReadout` | 左下角讀數加水位 |
| `Patch_PawnRenderNodeWorker_GetFinalizedMaterial`、`Patch_PawnRenderer_ParallelGetPreRenderResults` | Pawn 在水中的水線 |
| `Patch_SeabedMemory`、`Patch_AbyssSwallowsItems`（都在 `TerrainGrid.SetTerrain`） | 海床記憶；深淵吞掉物品 |
| `Patch_SectionLayer_TerrainEdges` | 海洋層的地形邊緣 |
| `Patch_WorldGrid_CreateRequiredLayers` | 每個世界自動加上海洋圖層 |
| `Patch_PlanetLayer_Visible` | 檢視海洋層時藏起地表 |

這些是 DepthRim 自己對原版的修改，和 DMSE 無關。
