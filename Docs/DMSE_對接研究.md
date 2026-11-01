# DMSE 對接研究

研究對象：`Mods/_Dead-Man-Switch-Expedition`（`Aoba.DeadManSwitch.Expedition` v0.6）
- 依賴：Odyssey、DMS Core（`Aoba.DeadManSwitch.Core`）
- 本機遊戲版本：1.7.4995。DMSE 的 `1.6/` 已凍結，所以 **DepthRim 只支援 1.7**。
- 來源：`.source/DMSE/` 原始碼與 `docs/` 設計文件（`CelestialTransfer_轉移飛行重構.md`、`BVR_Missile_系統流程.md`、`BVR_世界層偵測攔截重構.md`、`BVR_波次打擊規劃.md`）。

---

## 1. 結論摘要

| 系統 | 適合度 | 說明 |
|---|---|---|
| 轉移飛行（軌道船） | ⭐⭐⭐ 架構很合適，但有寫死的太空判定 | 「飛行中保留船體地圖」正是 DepthRim 航行需要的。不過有約 10 處寫死了太空層或 `TerrainDefOf.Space`，需要抽象化。 |
| BVR 世界層偵測與攔截 | ⭐⭐⭐ 幾乎可以直接換皮 | 雷達 → 聲納、導彈 → 魚雷、箔條 → 聲學誘餌，現成欄位都對得上。「主動聲納會暴露自己」可以直接用反輻射導引頭做到。 |
| 空投攔截（地圖層 BVR） | ⭐⭐ 可用，需確認細節 | 跳幫突擊艇做成敵對 Skyfaller 就會自動進入 BVR；要確認落地時會不會破壞屋頂與密閉性。 |
| 超視距火砲 | — | 用不到。 |

**最重要的架構結論：** 地下冰洋應該做成 **Odyssey 的 PlanetLayer**（和軌道層並列），而不是設計稿 v0.3 裡建議的 `PocketMapParent`。理由見 §4。

---

## 2. 轉移飛行（CelestialTransfer）

### 2.1 運作方式

```
結構樁（3×3）＋鋼樑連結 → 連通的樁網路 = 一艘船（footprint = 覆蓋範圍 ∩ 非虛空格）
   ▼
轉移控制台：制定計畫 → 小人操作 → 10 秒倒數暖機
   ▼
分離（ShipSeparationUtility）：建立同尺寸的虛空地圖（DMSE_ShipVoid），
   把船體範圍以「相同座標」搬過去；新地圖的父物件是 WorldObject_TransferShip
   ▼
飛行：離軌點火 → 滑行 → 入軌點火 → 抵達（地圖全程存在，殖民地照常運作）
   ▼
抵達：停在目標格，或選位降落到已有地圖（MoveHull 平移船體）
```

- `WorldObject_TransferShip : SpaceMapParent`：自帶地圖、可當玩家據點；`ShouldRemoveMapNow` 在飛行中回傳 false。
- 航線是貪婪鄰格路徑，`Tile` 隨顯示位置移動，所以 BVR 會自動把它當成移動目標追蹤。
- 抵達後有安全期（`DMSE_TransferGrace`），期間不觸發事件。
- 分離時 BVR 裝置會在新地圖重新註冊。

### 2.2 對應到 DepthRim

| DMSE | DepthRim | 處理方式 |
|---|---|---|
| 結構樁網路 = 船體範圍 | 耐壓船殼範圍 | 沿用。樁可以換皮成「龍骨 / 耐壓骨架」。 |
| 分離到虛空地圖 | 潛艇離港 | 沿用，但虛空地形要換成「深水」（見 §2.3）。 |
| 離軌點火 / 滑行 / 入軌點火 | 離港加速 / 巡航 / 減速 | 沿用狀態機。尾焰傷害改成**推進噪音**：點火期間被敵方偵測的範圍變大。 |
| 抵達後停在空格 | 懸停（不能出艙） | 沿用。 |
| 選位降落到已有地圖 | 下錨（淺海 / 海床） | 沿用 `MoveHull`，再加上 DepthRim 的「艙外可見、可出艙」切換。 |
| 安全期 | 同 | 沿用。 |
| — | **水深層級切換（下潛 / 上浮）** | DMSE 沒有這個概念，要自己做（見 §2.4）。 |
| — | **航行中封閉、艙外不渲染** | DMSE 沒有，DepthRim 自己做。 |

### 2.3 寫死太空的地方

| 位置 | 內容 |
|---|---|
| [TransferReadiness.cs:81](../../_Dead-Man-Switch-Expedition/.source/DMSE/CelestialTransfer/TransferReadiness.cs) | 不在 `isSpace` 圖層就不能起飛 |
| [PlaceWorker_StructuralPile.cs:32](../../_Dead-Man-Switch-Expedition/.source/DMSE/CelestialTransfer/Piles/PlaceWorker_StructuralPile.cs) | `spaceMapOnly` 時樁只能蓋在太空圖層 |
| [PileNetwork.cs:396](../../_Dead-Man-Switch-Expedition/.source/DMSE/CelestialTransfer/Piles/PileNetwork.cs) | 「非虛空格」= 地形不是 `TerrainDefOf.Space` |
| [ShipSeparationUtility.cs:285](../../_Dead-Man-Switch-Expedition/.source/DMSE/CelestialTransfer/Ship/ShipSeparationUtility.cs) | 船體地圖固定用 `DMSE_ShipVoid` 生成 |
| ShipSeparationUtility.cs:891 / 954 / 1348 | 原地圖清空時鋪 `Space`；判斷底層地形、降落區時比對 `Space` |
| [TransferLandingUtility.cs:62](../../_Dead-Man-Switch-Expedition/.source/DMSE/CelestialTransfer/Ship/TransferLandingUtility.cs) | 降落可用格比對 `Space` |
| TransferPlanningUtility.cs:138 / 220 | 撞擊飛行指定地表層（DepthRim 用不到） |

**建議做法：** 你提到未來可能統一成同一個框架，所以建議**直接在 DMSE 裡加抽象層**，而不是讓 DepthRim 用 Harmony 去 patch 一個 1483 行、還沒完整實測的分離流程。

```xml
<!-- 掛在 PlanetLayerDef 上的 DefModExtension（名稱暫定） -->
<li Class="DMSE.TransferEnvironmentExtension">
  <allowTransfer>true</allowTransfer>             <!-- 取代 isSpace 判定 -->
  <voidTerrain>DepthRim_DeepWater</voidTerrain>   <!-- 取代 TerrainDefOf.Space -->
  <voidMapGenerator>DepthRim_ShipAbyss</voidMapGenerator>  <!-- 取代 DMSE_ShipVoid -->
  <shipWorldObject>DepthRim_Submarine</shipWorldObject>    <!-- 取代 DMSE_TransferShip -->
</li>
```

太空層不掛這個擴充就走原本的預設值，DMSE 現有行為完全不變。

### 2.4 DepthRim 要自己補的部分

1. **潛艇世界物件**：繼承 `WorldObject_TransferShip`，加上「目前水深層級」與「航行 / 懸停 / 下錨」狀態。不建議直接繼承 `SpaceMapParent`，因為它帶有真空語意；可能需要 DMSE 把父類別往上抽一層。
2. **水深層級切換**：不換格子的短程「轉移」，耗時、耗電，並檢查船殼等級是否足夠。
3. **航行封閉**：航行中禁止出艙，艙外不渲染（下錨時才解除）。
4. **進水系統**：與 DMSE 無關。魚雷命中只是在地圖上爆炸、打壞牆體，`MapComponent_Flooding` 監聽牆體受損即可，兩邊不需要直接耦合。

---

## 3. BVR 系統

### 3.1 運作方式（世界層）

```
發射 → 航跡世界物件（MissileWorldObject，可精確算出 ETA）
   ▼  WorldComponent_BVRNetwork 每 15 ticks 掃描
偵測：與敵方節點距離 ≤ 有效半徑（worldDetectionRange × 隱身係數 × 高度係數）
   ▼
鎖定：需要空閒火控通道、有攔截彈、ETA ≤ maxRangeTicks
   ▼
發射攔截彈 → 求會合點 → 擲骰命中
   ▼
漏網 → 抵達地圖 → CIWS 末端攔截 → 落地爆炸
```

- 節點 = 擁有運作中搜索雷達的地圖（也就是 `MapParent`）。
- 所有 `MapParent` 每 60 ticks 取樣位置，所以移動中的潛艇會自動被追蹤。
- 敵對空投艙由 `Patch_MakeDropPodAt`（postfix `SkyfallerMaker.SpawnSkyfaller`）攔下，排進地圖層 BVR 波次。

### 3.2 對應到 DepthRim

| DMSE | DepthRim | 備註 |
|---|---|---|
| `CompSearchRadar`（`worldDetectionRange`） | **主動聲納** | 世界地圖視野可以直接拿這個半徑來算。 |
| `immuneToAntiRadiationSeeker`（DMSE 文件已提到「聲學雷達」） | **被動聲納** | 範圍小，但不會被歸向魚雷追蹤。 |
| 反輻射導引頭（打開機中功率最高的敵方雷達） | **被動歸向魚雷** | 開主動聲納就會被這種魚雷鎖定：設計稿的「主動聲納會暴露位置」直接現成。 |
| `CompFireControlRadar`（通道數） | 火控聲納 | 同時能導引幾枚魚雷 / 攔截幾個目標。 |
| 導彈裝配（彈體 / 戰鬥部 / 導引 / 酬載） | 魚雷裝配 | 整套沿用；新增魚雷彈體 Def，世界速度調慢。 |
| 反艦導引（+2 終端更新，追蹤移動目標） | 反潛 / 線導魚雷 | 直接適用於追打移動中的潛艇。 |
| SAM 攔截彈 | 反魚雷魚雷 | 沿用。 |
| CIWS | 近防（例如超空泡機砲） | 沿用。 |
| 箔條（`jamStrength` / `chaffCharges`） | **聲學誘餌（噪音彈）** | 欄位現成。 |
| `BVRTargetProps.altitude` | 深度 / 溫躍層 | 溫躍層遮蔽可以 patch `CompSearchRadar.EffectiveWorldRange`：觀測方與目標在不同層時半徑打折。 |
| 敵對空投 → 地圖層 BVR 波次 | **跳幫突擊艇** | 突擊艇 Def 掛 `BVRTargetProps` 就能被攔截。 |
| `WorldObjectComp_AirDefense`（無地圖據點的抽象防空） | 海盜基地 / 敵艦的抽象反魚雷防禦 | 沿用。 |

### 3.3 和 DepthRim 規則的配合

- **「只有開主動聲納的地圖才會被襲擊」：** DMSE 的 `ResolveDefender` 本來就要有運作中的雷達才會攔截。襲擊觸發條件由 DepthRim 的 IncidentWorker 自己判斷，兩者邏輯一致。
- **魚雷命中 → 船殼受損 → 進水：** 魚雷落點爆炸由 DMSE 處理，進水由 DepthRim 監聽牆體 HP 處理，不需要改 DMSE。

### 3.4 待確認

1. 漏網的突擊艇落地時（`ResolveWave` → `TryDrop`），是不是保留我們自訂的 Skyfaller 子類別？這決定突擊艇能不能「不破壞密閉性」穿入船艙。
2. 下錨時的襲擊：突擊艇要降在艙外海床，放下重裝潛水兵，所以落點選擇要另外處理。
3. CIWS 擊落時會用 `SpawnDebris` 殺死艙內所有 pawn，要確認這在水下是否合理（例如改成「沉沒」演出）。
4. DMSE 文件記載「未測：軌道圖層、玩家移動船」。DepthRim 的核心情境就是玩家移動船，可能會先踩到這些沒測過的路徑。

---

## 4. 架構修正：地下冰洋改做成 PlanetLayer

v0.3 設計稿建議用 `PocketMapParent` 做地下冰洋，但研究 DMSE 之後，改做成 **Odyssey 的 PlanetLayer** 比較好：

| 考量 | PocketMap | PlanetLayer（建議） |
|---|---|---|
| DMSE 轉移飛行 | 以圖層判定能不能起飛；口袋地圖沒有自己的世界格子，潛艇沒辦法在世界上移動 | 潛艇在冰洋層的格子間航行，和軌道船在軌道層航行一樣 |
| 襲擊池控制 | 要自己 patch | `PlanetLayerDef` 本來就有 `onlyAllowWhitelistedIncidents`、`raidPointsFactor` 等欄位 |
| 水深層級資料 | 沒有地方存 | `PlanetLayerDef.tileType` 可以指定自訂 Tile 類別，直接存每一格的水深層級 |
| 聲納世界視野 | 沒有世界座標 | BVR 的世界層偵測可以直接用 |
| 升降井 | 原生支援 | 做成連結地表地圖與冰洋層地圖的 `MapPortal`（DMSE 分離流程本來就會處理 `MapPortal`） |

`PlanetLayerDef` 原版欄位（已用源碼確認）：`isSpace`、`onlyAllowWhitelistedIncidents / Biomes / Quests / ArrivalModes`、`raidPointsFactor`、`backgroundBiome`、`tileType`、`layerType`、`worldDrawLayers` 等。

---

## 5. 建議的下一步

1. **決定 DMSE 抽象層由誰改：** 直接改 DMSE（建議，符合未來統一框架的方向），還是 DepthRim 自己用 Harmony patch。
2. **原型 1：** 自訂 `PlanetLayerDef`（冰洋層）＋ 自訂 Tile（水深層級）＋ 升降井 `MapPortal`，驗證地表到冰洋的連結。
3. **原型 2：** `MapComponent_Flooding`（每間艙室一個水位值），不依賴 DMSE，可以和原型 1 並行開發。
4. **原型 3：** 加上 §2.3 的抽象層後，讓一艘最簡單的潛艇從冰洋層地圖分離、航行、抵達。
5. **原型 4：** BVR 換皮（聲納、魚雷、誘餌）與跳幫突擊艇。
