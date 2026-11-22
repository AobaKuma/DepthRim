# DMSE 對接

對象：`Mods/_Dead-Man-Switch-Expedition`（`Aoba.DeadManSwitch.Expedition`），依賴 Odyssey、DMS Core。DMSE 的 `1.6/` 已凍結，所以 **DepthRim 只支援 1.7**。DMSE 的設計文件在它的 `docs/`（`CelestialTransfer_轉移飛行重構.md`、`BVR_*.md`、`外部模組擴充點.md`；DMSE 的 `.gitignore` 排除 `docs/`，只在本機）。

2026-11-16 整理：原本的研究筆記與「DMSE 調整計畫」都已完成並刪除。現在 **DepthRim 不對 DMSE 打 Harmony 補丁，也不用反射讀 DMSE 的私有成員**，全部走 DMSE 為此加的擴充點。

## 1. 架構決定

- **海洋是 PlanetLayer，不是口袋地圖。** 潛艇才能在海洋層的格子間航行（比照軌道船在軌道層），襲擊池用 `PlanetLayerDef` 的白名單控制，BVR 的世界層偵測直接可用。升降井是連結地表與海洋層的 `MapPortal`。
- **潛艇 = 轉移飛行換皮，聲納／魚雷 = BVR 換皮。** 進水系統和 DMSE 無關：魚雷爆炸打壞牆，`MapComponent_Flooding` 就接手。

## 2. 用到的 DMSE 擴充點

| 系統 | DMSE 擴充點 | DepthRim 的實作 |
|---|---|---|
| 轉移飛行的環境 | `TransferEnvironmentExtension`（掛在海洋層）：`voidTerrain`、`shipMapGenerator`、`shipWorldObject`、`routeWorker`、`energyWorker`、`landingWorker` | 虛空 = 深淵、船體地圖 `DepthRim_SubmarineHull`、`OceanRouteWorker`（海路）、`SubmarineEnergyWorker`（只算推進器耗電，出航不扣燃料）、`SubmarineLandingWorker`（下錨深度規則） |
| 拆平台 | `PlatformRemovalUtility.Remove` 讀圖層的 `voidTerrain` | 海洋層拆完露出深淵；地表圖層沒有 voidTerrain，`Patch_SeaSurfaceVoid` 換回開放海面／高空（補丁打在原版 `TerrainGrid.SetTerrain`） |
| 船體搬移 | `ShipSeparationUtility.RelocateHull`、`CenteringOffset`、`HullPlaced` 事件 | 上浮／下潛跨圖層搬船體；`HullMoveHooks` 讓水位與艙壓跟著船走；觸底揚塵 |
| 船體連通 | `DMSE.GridTopology` | 海上平台的支撐連通判定 |
| 偵測修正 | `AirDefensePipeline`（全域修正鏈） | `SonarLayerModifier`：溫躍層遮蔽 |
| 交戰領域 | `CombatDomain`（Air／Underwater）：彈體、目標、火控、CIWS 各自宣告；`IDomainAwareEffector` | 魚雷、反魚雷魚雷、突擊艇、火控聲納是水下；近防砲實作 `IDomainAwareEffector` |
| 交戰判定 | `MissileBodyDef.engageWorker` | `TorpedoEngageWorker`：只走海路、射程用海路距離 |
| 飛彈陣地 | `CompProperties_MissileBase.partOptions`、`relocatedLetterLabelKey／TextKey`；`incomingWorldObjectDef` 型別不對時 ConfigError | 魚雷艇與海盜潛艇的隨機導引頭、「離開」信件；魚雷艇用自己的航跡 Def `DepthRim_WorldObject_EnemyTorpedo` |
| 抽象防空 | `CompProperties_WorldAirDefense.ignoreNetworkResearch` | 海盜潛艇不等玩家研究雷達網就建立節點 |
| 地圖層酬載 | `IBVRPayloadHolder`、`IInterceptResolver`、`IBVRContactLabel` | 突擊艇：DMSE 認得艇上的人、被攔下時沉沒、抵達前顯示「未知聲納接觸」 |
| 飛彈行為 | `MissileBehaviorSet.Get<T>()` | `TorpedoUtility.BehaviorOf` |
| 崖壁邊緣 | `TransferEnvironmentExtension.drawTerrainEdges` | 海洋層設 true：深淵的崖壁邊緣（P2-10）。原本是 DepthRim 的 `Patch_SectionLayer_TerrainEdges`，2026-11-22 移到 DMSE |
| 整備檢查 | `TransferReadiness.ExtraChecks` 事件 | `DiveBellTransferCheck`：潛水鐘沒收回不能出港。原本是對 `TransferReadiness.Check` 的 Postfix |
| 結構樁疊加層 | `CompProperties_StructuralPile.drawLinkLines` | 大／小龍骨設 false，選取時不畫連結線。原本是對 `PileDrawUtility.DrawNetworkOverlay` 的補丁 |

2026-11-22 起單元測試已移除（兩邊都是），改以建置與遊戲內實測驗證。

## 3. 相容性

- DepthRim 依賴有這些擴充點的 DMSE 版本；舊版 DMSE 沒有退回補丁的路徑。
- 新欄位都是 Def 欄位或介面，不進存檔；worker 不存狀態。
- DMSE 的預設行為不變，其他依賴 DMSE 的模組不受影響。

## 4. 注意事項

- DMSE 的遊戲內驗收本身有不少既有失敗與不穩定的測試（和 DepthRim 無關），要另外整理。載著 DepthRim 跑時，DMSE 的 W4 末端窗口會差約 20 tick（遊戲較慢）。
- DMSE 文件記載「未測：玩家移動船」；DepthRim 的核心情境就是這個，出問題時先懷疑這條路徑。
- 之後的海盜基地（M7 7.5）要沿用抽象防空與飛彈陣地；如果需要讓別的模組自己加 NPC 節點，可以請 DMSE 開放節點提供者註冊表。
