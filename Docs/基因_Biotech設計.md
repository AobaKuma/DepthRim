# DepthRim 基因（Biotech）設計稿

整理日期：2026-11-18。狀態：**計畫中，尚未實作**。

目標：讓 Biotech 的基因系統接上 DepthRim 已有的水下機制（溺水、壓力適應、水壓抗性、海況、黑暗），做出「為深海改造的人」與「天生不適合下海的人」。基因只改既有公式的參數，不另起一套平行系統。

---

## 0. 和現有系統的接點

| 系統 | 現在的公式／位置 | 基因要改的地方 |
|---|---|---|
| 溺水 | `FloodingPawnEffects.ApplyBreathing`：沒入水中、沒有氣瓶 → `DepthRim_Drowning` 每次更新加 `DrowningPerUpdate` | 鰓：直接不溺水；屏息：溺水累積 × 倍率 |
| 氣瓶 | `CompDivingAir.TryConsume(ticks)` | 屏息、鰓：耗氣 × 倍率（鰓為 0） |
| 水壓抗性 | 統計 `DepthRim_PressureResistance`（潛水服 +2） | 耐壓基因用 `statOffsets` 直接加，不寫程式 |
| 出艙水壓傷害 | `FloodingMath.PressureShortfall(tier, 抗性, 適應值)` → `DepthRim_Barotrauma` | 不耐壓：傷害 × 1.5 |
| 壓力適應 | `PressureAdaptationMath`：容許差 `Tolerance = 1`、每級 `1 ÷ 血液過濾` 天（0.5～2 天）、上限 3 | 容許差、適應速度、可適應的上限改成每個人的參數 |
| 水密門通行 | `PressureAdaptationMath.DoorAllows` 用 `Mismatch` | 跟著容許差一起改 |
| 海況 | `SeaSway.WeatherFactor`（1～2.5）、`SeaStateExtension`、`VoyageMotion.DiveMix` | 暈船的暴露量來源 |
| 光照 | 原版 `GlowGrid`；潛水服頭燈 `CompSuitLamp`（半徑 4.5） | 畏光的判定；畏光者的頭燈 |
| 涉水 | `DepthRim_Wading` 的 Moving 減益 | 善泳：減益減半 |

---

## 1. 相依與載入

- `GeneDef` 是核心類別，Odyssey 自己的基因（`VacuumResistance_*`）就放在一般 Defs 裡，只在 `displayCategory` 加 `MayRequire="Ludeon.RimWorld.Biotech"`。
- DepthRim **不把 Biotech 加為相依**。基因、種族（Xenotype）、相關想法放在條件資料夾：

```xml
<v1.7>
  <li>/</li>
  <li>1.7</li>
  <li IfModActive="Ludeon.RimWorld.Biotech">1.7/Biotech</li>
</v1.7>
```

- 檔案：`1.7/Biotech/Defs/Genes/DepthRim_Genes.xml`、`DepthRim_Xenotypes.xml`、`DepthRim_GeneCategory.xml`。
- 暈船的 hediff、統計與 C# 鉤子放主資料夾：沒有 Biotech 時基準值照樣運作（見 §3），只是沒有基因去改它。
- C# 只用核心的 `pawn.genes`（沒有 Biotech 時為空清單），不需要條件編譯。

### 1.1 程式結構

- `GeneExtension_DepthRim`（`DefModExtension`，掛在 GeneDef 上）：所有數值參數，見各節。
- `DepthRimGenes`（靜態工具）：彙整一個角色所有基因的參數，結果快取在 `Pawn` 的 thingIDNumber 上，`Pawn_GeneTracker.Notify_GenesChanged` 的後置補丁清快取。
- 純計算放進現有的 `FloodingMath`／`PressureAdaptationMath`／新的 `SeasicknessMath`，維持單元測試可直接驗。
- 基因分類：新增 `GeneCategoryDef DepthRim_Marine`（「海洋適應」），排在 `ResistanceAndWeakness` 之後。

---

## 2. 水下呼吸（exclusionTag `DepthRim_WaterBreathing`）

| defName | 名稱 | 效果 | Cpx | Met | Arc |
|---|---|---|---|---|---|
| `DepthRim_LongBreath` | 屏息 long breath | 溺水累積 × 0.35；氣瓶耗氣 × 0.6 | 1 | −1 | 0 |
| `DepthRim_Gills` | 鰓 gills | 沒入水中不會溺水，也不耗氣瓶；**離水**時見下 | 3 | −2 | 0 |

- 鰓的代價：離開水的時間累積 `DepthRim_GillDrying`（鰓乾燥），乾燥、炎熱（> 30 °C）時加快，泡水或下雨（`SoakingWet`）重設。嚴重度到 0.5 以上 Breathing −0.15、Consciousness −0.1；不致命。
  - 理由：DepthRim 大部分時間在乾艙裡，鰓若沒有代價就是白拿的強基因。乾燥速度預設 3 天到 0.5，所以有月池或淹水艙的潛艇可以輕鬆照顧，陸地殖民地要特地蓋水池。
- 實作：`ApplyBreathing` 在溺水前問 `DepthRimGenes.DrowningFactor(pawn)`：0 → 直接 return（鰓）；其他值乘在 `DrowningPerUpdate` 上。`CompDivingAir.TryConsume` 加一個 `rate` 參數。
- 驗證：Odyssey 的 `VacuumResistance_Total`（breathless）要不要也算屏息？見 §9 Q2。

---

## 3. 暈船（新系統＋基因，exclusionTag `DepthRim_Seasickness`）

### 3.1 基準機制（沒有基因的人也會有）

- 新統計 `DepthRim_SeasicknessSensitivity`（暈船敏感度），預設 1，最小 0。
- 新 hediff `DepthRim_Seasick`，每 250 ticks 由 `MapComponent_Seasickness` 對地圖上的血肉人形更新：
  - **暴露量** `exposure`：
    - 海面地圖的水面層／平台層：`max(0, SeaSway.WeatherFactor − 1.3) ÷ 1.2`（平靜海 0、惡劣海況 2.0 約 0.58、暴風雨 2.5 為 1）；
    - 航行地圖：上浮／下潛中 `VoyageMotion.DiveMix × 0.5`；淺海層航行另加 0.2；
    - 破冰上浮演出期間固定 0.8；
    - 其他地圖 0。
  - 嚴重度每天往 `exposure × 敏感度` 移動（上升每天 2、下降每天 4，離開暴露後半天內恢復）。睡著時上升速度減半。
  - 每個人另有「適應」：連續暴露 3 天以上，敏感度 × 0.7（存在 hediff 上，離開海面 5 天後重置）。
- 階段：

| 嚴重度 | 名稱 | 效果 |
|---|---|---|
| 0.15 | 頭暈 queasy | Consciousness −0.05；心情 −3 |
| 0.4 | 暈船 seasick | Consciousness −0.1、Manipulation −0.1；`vomitMtbDays` 0.5；心情 −6 |
| 0.75 | 嚴重暈船 severe seasickness | Consciousness −0.2、Moving −0.1；`vomitMtbDays` 0.15；食慾 −30%；心情 −10 |

- 基準平衡：一般人只在暴風雨中撐到「暈船」，惡劣海況停在頭暈；不致命，不會倒地。
- 機械體、`IsFlesh == false`、動物不受影響（動物另議，見 §9 Q4）。

### 3.2 基因

| defName | 名稱 | 效果 | Cpx | Met |
|---|---|---|---|---|
| `DepthRim_SeasickProne` | 易暈船 seasick-prone | 敏感度 +1（基準的 2 倍）；不會產生 3 天適應 | 1 | +2 |
| `DepthRim_SeaLegs` | 海腳 sea legs | 敏感度 × 0（免疫）；海面地圖上 MoveSpeed +0.1、ShootingAccuracyPawn +0.05（擺動中站得穩） | 1 | −1 |

- 海腳的加成用新的 `ConditionalStatAffecter_OnSeaSurface`（地圖是海面地圖的水面層／平台層，`SeaLevels` 判定）。

---

## 4. 耐壓性（四級，exclusionTag `DepthRim_PressureTolerance`）

沒有基因的人是「基準」，不在四級裡。四級裡最低一級是負面基因。

| defName | 名稱 | 水壓抗性 | 容許差 | 適應速度 | 可適應上限 | 其他 | Cpx | Met | Arc |
|---|---|---|---|---|---|---|---|---|---|
| （基準） | — | 0 | 1 | × 1 | 3 | — | — | — | — |
| `DepthRim_PressureFragile` | 不耐壓 pressure-fragile | 0 | 0.5 | × 0.5 | 2 | 水壓傷害 × 1.5 | 1 | +2 | 0 |
| `DepthRim_PressureTolerant` | 耐壓 pressure-tolerant | +1 | 1.25 | × 1.25 | 3 | — | 1 | −1 | 0 |
| `DepthRim_PressureAdapted` | 深海適應 deep-adapted | +2 | 1.5 | × 1.5 | 3 | 水壓傷害 × 0.75 | 2 | −2 | 0 |
| `DepthRim_PressureImmune` | 淵體 abyssal body | +3 | 3（永不壓力不適） | — | 3 | 水壓傷害 × 0 | 3 | −1 | 1 |

說明：

- **水壓抗性**直接寫 `statOffsets`，和潛水服的 +2 相加，所以：深海適應的人不穿潛水服也能在深海泡水（但仍要呼吸，見 §2），穿了可以下到海床而不用先適應。
- **容許差**取代 `PressureAdaptationMath.Tolerance` 常數：`Mismatch(adapted, ambient, tolerance)`。容許差 3 等於任何壓力都不會不適，加壓艙對他沒有意義；水密門的 `DoorAllows` 跟著用同一個值，所以淵體的人可以自由進出加壓艙。
- **適應速度**乘在 `DaysPerLevel` 的結果上（`DaysPerLevel(filtration) ÷ speed`），仍然限制在 0.25～4 天（下限、上限各放寬一倍，讓基因的效果不被原本的 0.5～2 天夾掉）。
- **可適應上限**：不耐壓的人適應值最多到 2（深海），所以海床的乾艙對他來說永遠差 1 級；配合容許差 0.5，等於在海床時永遠輕度不適。這是「不耐壓」的核心懲罰：能下潛，但不能長住海床。
- **水壓傷害倍率**乘在 `ApplyPressure` 的 `BarotraumaPerUpdatePerTier × shortfall` 上。
- 介面：`Hediff_PressureAdaptation` 的說明加一行「基因：容許 ±X 級、適應速度 ×Y、最多適應到 Z」；`Alert_PressureSickness` 與 `BoardingReadiness` 的剩餘時間改用同一組參數。
- 單元測試：`Mismatch` 與 `Step` 的新參數、上限夾值、`DaysPerLevel` 的新邊界。

---

## 5. 畏光（exclusionTag `DepthRim_LightSensitivity`）

原版 `UVSensitivity` 只看日光；DepthRim 的強光多半是人造的（燈、頭燈、聲納室、破冰上浮後的極地日光），所以另做一個看「所在格光照」的版本。

| defName | 名稱 | 效果 | Cpx | Met |
|---|---|---|---|---|
| `DepthRim_Photophobia` | 畏光 photophobic | 所在格光照 ≥ 0.7：ShootingAccuracyPawn × 0.85、WorkSpeedGlobal × 0.9、心情 −4（「刺眼」）；在黑暗中（< 0.3）心情 +2 | 1 | +2 |
| `DepthRim_PhotophobiaIntense` | 強烈畏光 light-blind | 光照 ≥ 0.5：Sight −0.3、ShootingAccuracyPawn × 0.7、WorkSpeedGlobal × 0.8、心情 −8；直射日光下額外 Moving −0.1 | 1 | +4 |

- 判定：新的 `ConditionalStatAffecter_BrightLight`（門檻寫在 XML），光照用 `map.glowGrid.GroundGlowAt(pawn.Position)`；強烈畏光的視力減益用 hediff `DepthRim_Dazzled`（嚴重度 = 光照，每 250 ticks 設定，比照 `DepthRim_Wading` 的做法）。
- 想法：`ThoughtDef DepthRim_BrightLight` / `DepthRim_ComfortingDark`，`ThoughtWorker` 檢查基因與光照；有 `DarkVision` 不抵銷畏光（兩者常一起出現，見 §7）。
- **頭燈問題**：潛水服頭燈會讓穿的人腳下光照接近 1，畏光者一出艙就刺眼。建議：`CompSuitLamp` 對畏光者預設改成「暗紅光」（半徑 2.5、亮度降到 0.4 以下），並加一個切換按鈕（白光／暗紅／關）。見 §9 Q3。
- 不另做「暗視」基因：直接用原版 `DarkVision`（`ignoreDarkness`），種族裡搭配。

---

## 6. 其他候選基因

依實作成本分三批：§6.0 幾乎不用寫程式；§6.1 和現有機制直接相關、程式量小；§6.2 等相關系統做出來再說。

### 6.0 低成本批（純 XML，或共用三個小條件類別）

#### A. 純 XML（0 行 C#）

原版 `GeneDef` 已經有的欄位（Biotech 的基因都在用）：`statOffsets`／`statFactors`、`disablesNeeds`、`dontMindRawFood`、`foodPoisoningChanceFactor`、`damageFactors`、`hediffGiversCannotGive`、`skinColorOverride`，加上 ThoughtDef 的 `nullifyingGenes`（用 PatchOperation 加）。

| defName | 名稱 | 效果 | 用到的欄位 | Cpx | Met |
|---|---|---|---|---|---|
| `DepthRim_ConfinedAdapted` | 幽閉適應 confined-adapted | 不需要房間空間（潛艇艙室狹小也不扣心情） | `disablesNeeds` `RoomSize` | 1 | −1 |
| `DepthRim_RawSeafood` | 海食 seafood digester | 吃生食沒有心情懲罰，食物中毒機率 × 0.5 | `dontMindRawFood`、`foodPoisoningChanceFactor` | 1 | −1 |
| `DepthRim_Waterproof` | 防水皮膚 waterproof skin | 沒有「濕透」的心情懲罰 | PatchOperation 在原版 `SoakingWet` 的 `nullifyingGenes` 加上這個基因 | 1 | 0 |
| `DepthRim_ConcussionResistant` | 抗震 concussion-resistant | 爆炸傷害 × 0.6（魚雷命中艙室時的爆炸） | `damageFactors` `Bomb` | 1 | −1 |
| `DepthRim_ScaledSkin` | 鱗皮 scaled skin | ArmorRating_Sharp +0.1、ArmorRating_Blunt +0.05；Beauty −1 | `statOffsets` | 1 | −1 |
| `DepthRim_ColdWaterBody` | 冷水體質 cold-water body | 不會失溫、ComfyTemperatureMin −10、ComfyTemperatureMax −5 | `hediffGiversCannotGive` `Hypothermia`、`statOffsets` | 2 | −2 |
| `DepthRim_Skin_AbyssGrey`／`_Pearl`／`_Coral` | 膚色：深淵灰藍、珍珠白、珊瑚紅 | 外觀 | `skinColorOverride`，不用新貼圖 | 0 | 0 |

- 冷水體質和 §6.1 的鯨脂重疊：二選一。建議留冷水體質（純 XML），鯨脂取消。
- 種族也可以直接用原版基因：`CaveDweller`（不需要戶外，潛艇生活很合適）、`DarkVision`、耐寒系列。

#### B. 寫三個小條件類別，換來一整組 XML 基因

`ConditionalStatAffecter` 子類別一個大約 15 行（只覆寫 `Label` 和 `Applies(StatRequest)`），寫一次後所有基因都能在 `conditionalStatAffecters` 裡用。Odyssey 的 `MoveSpeed_Space`（太空中加速、地面上減速）就是這種寫法。

| 類別 | 條件 | 判定 |
|---|---|---|
| `ConditionalStatAffecter_Submerged` | 沒入水中 | 有 `DepthRim_Wading` 且嚴重度 ≥ 0.75 |
| `ConditionalStatAffecter_UnderwaterMap`／`_NotUnderwaterMap` | 在水下地圖上（含乾艙）／不在 | 地圖是 `IDepthTierProvider` 或海洋層 |
| `ConditionalStatAffecter_OnSeaSurface` | 在海面地圖的水面層、平台層 | `SeaLevels`（§3.2 的海腳已經要用到） |
| `ConditionalStatAffecter_Dark`／`_BrightLight` | 所在格光照 < 0.3／≥ 門檻 | `GlowGrid`（§5 的畏光已經要用到） |

有了這些類別，下面的基因都只要寫 XML：

| defName | 名稱 | 效果 | Cpx | Met |
|---|---|---|---|---|
| `DepthRim_StrongSwimmer` | 善泳 strong swimmer | 沒入水中時 MoveSpeed × 1.4（取代 §6.1 原本用 HediffComp 的做法） | 1 | −1 |
| `DepthRim_UnderwaterWorker` | 水中作業手 underwater worker | 沒入水中時 WorkSpeedGlobal +0.2（抵掉潛水服 −0.25 的大部分） | 1 | −1 |
| `DepthRim_DeepCalm` | 深海安定 deep calm | 水下地圖上 MentalBreakThreshold −0.05、GlobalLearningFactor +0.1 | 1 | −1 |
| `DepthRim_LandSick` | 陸地不適 land-sick | 不在水下地圖時 MoveSpeed −0.15、WorkSpeedGlobal −0.1、MentalBreakThreshold +0.05 | 1 | +2 |
| `DepthRim_DarkWorker` | 暗處工作者 dark worker | 黑暗中 WorkSpeedGlobal +0.1、ShootingAccuracyPawn +0.05（配 `DarkVision` 才有意義） | 1 | −1 |

- 陸地不適可以取代 §6.2 的「離水脫水」，也可以拿來給淵民當主要代價，比 §2 的鰓乾燥簡單得多（見 §9 Q7）。
- 程式量：三個條件類別合計約 60 行，加上翻譯 key。之後新增基因都不用再碰 C#。

### 6.1 第一批

| defName | 名稱 | 效果 | 接點 | Cpx | Met | exclusionTag |
|---|---|---|---|---|---|---|
| `DepthRim_Bioluminescent` | 生物發光 bioluminescent | 身上發出半徑 2 的藍綠微光（水中、黑暗中才開）；美觀 +1 | 重用 `CompSuitLamp` 的 `RegisterCustomGlower` 邏輯，抽成 `PawnGlower` 給基因用（`Gene` 子類別 `Gene_Bioluminescent` 每 250 ticks 更新） | 1 | −1 | — |
| `DepthRim_DecompressionResistant` | 抗減壓病 decompression-resistant | `DepthRim_Barotrauma` 自然消退 × 2；壓力不適的痛覺 × 0.5 | `HediffComp` 或補丁讀基因 | 1 | −1 | — |

### 6.2 第二批（等系統）

| 構想 | 名稱 | 等什麼 |
|---|---|---|
| 不受深海壓抑影響 | 深淵定居者 abyss dweller | 設計稿 §5「越深越壓抑」的心情還沒做；做了之後比照原版 `CaveDweller` 用 `nullifyingGenes` |
| 黑暗水中標出附近生物與敵人 | 回聲定位 echolocation（能力基因） | 聲納接觸系統開放給角色能力 |
| 低溫代謝、低耗氧 | 低代謝 slow metabolism | 艙內空氣／二氧化碳機制（目前沒有） |
| 抗聲爆、抗震 | 鯨耳 whale ears | 魚雷命中的震波傷害（目前沒有） |

---

## 7. 種族（XenotypeDef）

| defName | 名稱 | 基因 | 定位 |
|---|---|---|---|
| `DepthRim_Mariner` | 水手 mariner | 海腳、屏息、耐壓、冷水體質、防水皮膚 | 海面與淺海的船員；非 archite，海盜與商船人員可用 |
| `DepthRim_Deepdiver` | 深潛者 deepdiver | 鰓、深海適應、善泳、水中作業手、幽閉適應、`DarkVision`、畏光、易暈船、深淵灰藍膚 | 住在潛艇與海底站的人；在水下強、在海面暈船、怕燈 |
| `DepthRim_Abyssal` | 淵民 abyssal | 鰓、淵體、生物發光、善泳、深海安定、`DarkVision`、強烈畏光、海食、陸地不適、易暈船、珍珠白膚 | 海床遺跡裡的改造人；含 archite，只從遺跡、事件取得 |

- 外觀：深潛者、淵民用原版的膚色基因（灰藍、蒼白），之後再做專屬的鰓／發光紋路頭部貼圖（列入 [待補內容清單.md](待補內容清單.md) §1）。
- 派系：海盜潛艇（`1.7/Defs/Enemy/Pirates.xml`）的 `xenotypeSet` 混入水手 30%、深潛者 10%（只在 Biotech 啟用時，用條件資料夾的 PatchOperation 加）。
- 取得：
  - 基因包：GeneDef 預設會進原版基因包生成；淵體、強烈畏光以外的基因額外加進沉船與聲納地標的戰利品（`Landmark` 的 loot 表加基因包，偏重海洋基因）。
  - 淵民：海床遺跡地標的「冬眠艙」事件（之後做），救出後可招募。

---

## 8. 實作順序

1. 條件資料夾、`GeneCategoryDef`、`GeneExtension_DepthRim`、`DepthRimGenes` 快取與補丁。
2. 耐壓四級（純 XML + `PressureAdaptationMath` 參數化），單元測試。
3. 水下呼吸（屏息、鰓、鰓乾燥），驗收測試：鰓角色在淹水艙不溺水、離水後累積乾燥。
4. 暈船基準機制 + 兩個基因，單元測試 `SeasicknessMath`，驗收測試：`OverboardHazard.debugSeaFactor` 固定暴風雨，基準角色到「暈船」、海腳不暈、易暈船到「嚴重」。
5. 畏光兩級、想法、頭燈暗紅模式。
6. §6.0 低成本批：A 組純 XML 可以在第 1 步之後隨時做；B 組先寫條件類別（`OnSeaSurface`、`BrightLight` 在第 4、5 步就會寫好），再補 XML。
6a. §6.1 第一批候選基因。
7. 三個種族、海盜派系混入、戰利品。
8. 圖示（暫用原版：鰓借 `Gene_Breathless`、耐壓借 `Gene_VacuumResistance`、畏光借 `Gene_MildUVSensitivity`；暈船、善泳、生物發光的暫代圖示實作時再挑），正式圖示列入 [待補內容清單.md](待補內容清單.md) §1。

測試：單元測試 `DepthRim.Tests`、驗收測試 `DepthRim.Acceptance`；驗收需要 Biotech 啟用。

---

## 9. 待決定

| # | 問題 | 建議 |
|---|---|---|
| Q1 | 暈船要不要對**沒有基因的人**也生效？（不生效 = 純基因系統，暈船只是負面基因） | 生效，但基準只在暴風雨才到「暈船」階段；這樣海腳才有價值，沒有 Biotech 的玩家也有海況的感覺 |
| Q2 | Odyssey 的 breathless（`VacuumResistance_Total`）要不要讓人在水裡少溺水？ | 算作屏息（溺水 × 0.35），不算鰓 |
| Q3 | 畏光者的潛水服頭燈怎麼處理？ | 預設暗紅低亮度，加切換按鈕 |
| Q4 | 動物會不會暈船？ | 先不做 |
| Q5 | 淵體（耐壓第四級）要不要 archite？ | 要：抗性 +3 加上永不壓力不適，等於完全跳過加壓艙玩法 |
| Q6 | 不耐壓的「最多適應到深海」會讓海床基地永遠不適，是否太重？ | 保留；它是 Met +2 的負面基因，玩家可用加壓艙之外的方式（不派他下海床）處理 |
| Q7 | 鰓的乾燥是否對潛艇生活太煩？ | 3 天到 0.5、泡一下就重設；若太煩，鰓本身不加代價，改成在種族裡搭配 §6.0 的陸地不適 |
| Q8 | 冷水體質（失溫免疫）在極地設定會不會太強？ | Cpx 2、Met −2；若太強改成只給 ComfyTemperatureMin −15，不免疫失溫 |
