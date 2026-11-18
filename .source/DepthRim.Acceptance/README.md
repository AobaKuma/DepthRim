# DepthRim 遊戲內驗收測試（RimWorld 1.7）

在真正的遊戲迴圈裡跑 DepthRim 的流程：冰洋圖層、升降井與地圖生成、房間水位、電網、Pawn、渲染材質、Scribe。
公式層級的檢查在 `.source/DepthRim.Tests`。驅動程式整份沿用 DMSE 的 `DMSE.BVRAcceptance`。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .source/DepthRim.Acceptance/run.ps1 -OutDir <dir> [-TimeoutMinutes 20] [-Only F2,P1] [-SaveDataFolder <dir>]
```

- 先關閉 RimWorld（它會鎖住 `1.7/Assemblies`），並先建置 `.source/DepthRim`：測試跑的是 `1.7/Assemblies/DepthRim.dll`。
- `run.ps1` 只在執行期間把 `DepthRim.Acceptance.dll` 放進 `1.7/Assemblies`，結束後移除（當掉或逾時也會）。**不要發佈這個 DLL。**
- **模組清單：** 預設在 `<dir>/savedata` 建立暫存的存檔資料夾，複製你的 `Config`，並在 DMSE 後面加上 DepthRim，不會改到你自己的模組清單。
- 遊戲以 `-quicktest` 開新的墜毀殖民地，跑完後自動關閉。
- `-Only` 依套件順序只跑指定的測試；E1 之後的測試若單獨執行，會自己建立升降井與冰洋地圖。

## 輸出（`<dir>`）

| 檔案 | 內容 |
|---|---|
| `depthrim_acceptance_results.tsv` | 每個測試一列：id、PASS/FAIL、標題、細節 |
| `depthrim_acceptance_log.txt` | 每一項檢查（`ok:` / `FAIL:`）、備註與 tick |
| `DepthRim_*.png` | 關鍵畫面截圖（井底、排乾的艙室、水中的 Pawn 近景／遠景、燈下的水、潛水服頭燈、四個層級的近景／全景、四個層級的半淹艙室） |
| `Player.log` | 這次執行的遊戲紀錄 |

## 測試

| Id | 檔案 | 內容 |
|---|---|---|
| L1 | `Tests_World.cs` | 海洋圖層存在、格子已生成且都是海洋生態區（至少五種）、冰原／海冰下方是極地冰蓋、地表放大切到海洋、沒有逆重船連線、DMSE 轉移飛行尚未啟用 |
| E1 | `Tests_World.cs` | 升降井在冰洋層生成玩家地圖、井底出口連回地表、出口周圍岩體已清除、重複進入沿用同一張地圖 |
| F1 | `Tests_Flooding.cs` | 開放水域固定 100%；地表沒有水 |
| F2 | `Tests_Flooding.cs` | 新圍的密閉艙室繼承滿水、通電排水泵把它抽乾、整間水位一致 |
| F3 | `Tests_Flooding.cs` | 受損牆滲水、速度不超過公式、修好後停止 |
| F4 | `Tests_Flooding.cs` | 關閉的門不漏水、打開的門讓艙室進水 |
| F5 | `Tests_Flooding.cs` | 打穿外牆（外側沒屋頂）→ 艙室和外海相通、海水逐漸灌入（破口前緣是進水點）、灌滿才算開放水域 |
| S1 | `Tests_Flooding.cs` | `MapComponent_Flooding` 以 Scribe 存讀後每一格水位不變 |
| P1 | `Tests_Pawns.cs` | 沒裝備的殖民者在開放水域溺水、溺水加重、回到地表後消退 |
| P2 | `Tests_Pawns.cs` | 潛水服供氧、水壓抗性 +1、氣瓶空了開始溺水、地表不回充、冰洋乾燥艙室回充 |
| P3 | `Tests_Pawns.cs` | 涉水嚴重度 = 水位、游泳階段減速、淺海沒有水壓傷害 |
| H3 | `Tests_SeaHull.cs` | 海面地圖上的船體平台：能鋪在開放海面（不能鋪在海上平台上、不需要支柱）、側面只露出乾舷而海上平台照舊兩格、海面遮罩記成浮在水面（b）、水線參數、水密隔牆與水密門能蓋、拆除露出海面；近景與全景截圖 |
| H5 | `Tests_HullPlatform.cs` | 抗壓水密牆的斜角：海床平台上蓋一圈帶兩段斜接的牆，四個缺口格各一張 ShipHull 風格的斜角圖（`Textures/Thing/ShipHullAngled`），室內直角內角不補，往南接直牆的一側用 `_Linked` 變體；截圖 |
| P5 | `Tests_Pressure.cs` | 地表加壓艙的三個條件（耐壓甲板、密閉、水密門）、加壓到 2 級、殖民者進艙後不適並在 0.5～2 天內適應、出艙不會不適、適應值歸零後移除、海床上來的人在艙外嚴重不適而在艙內沒事、其他陣營視為已適應、斷電洩壓、破艙立刻失壓；水密門擋住未適應的人進艙與適應中的人出艙（徵召不擋、可達性一致）、開門洩壓、壓力不適警示、一般牆與水密門超過耐壓時受損 |
| P6 | `Tests_Pressure.cs` | 水下地圖的艙壓：乾艙在任何深度都維持自己的壓力、和外海同壓時牆不受力、換到更深處艙壓不變而牆再度超載、非耐壓甲板地板慢慢洩壓、艙裡的殖民者照艙壓適應、潛艇搬動時艙壓跟著格子走 |
| S6 | `Tests_SubmarinePrefab.cs` | 預製潛艇（FFF 結構 `DepthRim_Prefab_PatrolSubmarine`，`DepthRimFixture.SpawnPrefabSubmarine`）：船塢上生成、四個乾的密閉艙室、全部通電、DMSE 認得整塊船體、兩具推進器與電池電量、船殼耐壓變溫層、FFF 生成後占地外原本看得見的格子仍然看得見 |
| S7 | `Tests_SubmarineStart.cs` | 潛艇開局（場景 `DepthRim_SubmarineStart`、`ScenPart_SubmarineStart`）：開局格換到附近的淺海海洋格、地圖生成器換成 `DepthRim_OceanStart`；開局地圖中央有深淵船塢與遊戲內匯出的潛艇 `DR_Submarine_Test`（看得見、有屋頂、艙室密閉且乾、進家園區）；殖民者與開局物品放在船艙 |
| S8 | `Tests_Submarine.cs` | 航行中落海（`VoyageOverboard`）：同 S2 起飛後，在密閉艙室、沒屋頂的甲板、船外深淵各放一名殖民者；不在航行（視為已抵達）時沒人失蹤；回到航行後艙外兩人失蹤（世界 pawn、沒死）並收到一封信，艙室裡的人留在船上 |
| A1 | `Tests_Submarine.cs`、`Tests_RopeAnchor.cs` | 航行中的安全繩錨點：同 S2 起飛後在甲板裝錨點、沒屋頂的甲板放三名殖民者；最近的兩人扣上（有「掛在安全繩上」）、第三人失蹤；遮罩在掃描之間快取、蓋牆後重算且牆後拉不到；走回艙室解開；停船全部解開、回到航行再扣上；拆掉錨點後剩下的人下一個 tick 失蹤；記錄每次掃描的平均時間；截圖 |
| A2 | `Tests_RopeAnchor.cs` | 水面惡劣海況（`MapComponent_RoughSeaOverboard`）：固定天氣倍率；平靜時不扣繩；倍率 2.5 時錨點旁的人扣上、船另一頭的人上警報；擲骰必中時扣繩的人被浪打倒但留下、沒扣的人被捲走並收到信；倍率降到 1.5 時解開、警報消失；地表基地沒有風險；截圖 |
| A3 | `Tests_SeaWeather.cs` | 海面的惡劣海況天氣：海面地圖的天氣表有兩種惡劣海況；平靜、惡劣海況、暴風雨各一張截圖（加一張暴風雨全景）；擺動倍率下限、兇度、著色器 `_Rough` 與浪花參數、落海風險；平台層的天氣跟著水面層；戶外靠海邊的物品被捲走、屋頂下的不會；地圖元件自己會掃；海邊的牆受損但停在最大耐久 20%；天氣轉晴後不再捲走或打壞 |
| U1 | `Tests_Surfacing.cs` | 常規上浮（`SubmarineSurfacing`）：預製潛艇分離成潛艇、移到開放海面下的淺海；不在淺海時停用；上浮後地表那一格有海面地點、船體浮在開放海面上、船員跟著上來、水位系統不作用；從艦橋下潛回到正下方的海洋格（淺海、停著）、船體完整、空的海面地點移除 |
| I1 | `Tests_Surfacing.cs` | 破冰上浮：正上方改成海冰、冰厚用 `IceConditions.debugThickness` 固定；沒有破冰結構與冰太厚時停用；懸停一小時後部分開裂（結構受損、六小時冷卻）、一天內再頂出水（破冰點、船體在冰上、一圈開放水、碎冰、裂紋、開始結冰）；結冰半天還能下潛、四天後被凍住、撞開凍冰後下潛 |
| U3 | `Tests_Surfacing.cs` | 海面的畫法照原版深海：材質拿到原版深海的光照色階（`WaterOceanDeep`）與漣漪貼圖；海面地圖左半鋪原版深海、右半是 DepthRim 的海，在交界截圖（離線比較兩邊的平均色與質感） |
| U2 | `Tests_Surfacing.cs` | 開放海面的出水演出（`SubmarineSurfacingCutscene`）：上浮後演出先播、船體等演出結束才搬上海面；1.2、2.6、3.6、5.5 秒各截一張 |
| I2 | `Tests_Surfacing.cs` | 破冰的出水演出（猛烈出水）：破裂前放射裂紋已經長出、沒有開放水；破裂後出現開放水、上百塊碎冰四射；演出結束後船體在冰上、四周一圈開放水；0.5、2.5、3.6、6、9 秒各截一張 |
| G1 | `Tests_SubmarineStart.cs` | **只在 `run.ps1 -Scenario DepthRim_SubmarineStart -Only G1` 時跑**（`ScenarioOverride` 讓 -quicktest 改用這個場景開新遊戲，原版新遊戲流程只少了選地點的頁面）：開局地圖在海洋層、是玩家據點、潛艇直接坐在中央的海床上、三名殖民者與開局物品在船艙、起始研究完成、鏡頭在潛艇上；跑兩天後殖民者沒事、沒有溺水、屋頂沒塌、艙室乾；存檔再讀檔後一切還在；全程沒有錯誤 |
| G2 | `Tests_StartSubmarineSurfacing.cs` | **只在 `run.ps1 -Scenario DepthRim_SubmarineStart -Only G2` 時跑**：開局的潛艇（`DR_Submarine_Test`，996 格）整艘出港、船員在船上；移到開放海面下上浮（演出照播、截圖）、下潛；移到海冰下、裝破冰結構、破冰上浮（演出照播、截圖）；船體與船員都在冰面上 |
| G3 | `Tests_StartVoyagePlan.cs` | **只在 `run.ps1 -Scenario DepthRim_SubmarineStart -Only G3` 時跑**：開局潛艇用艦橋的航行指令（玩家實際的流程）：一開局（遊戲還沒開始跑）推進器與艦橋就有電、航程內的海洋格能選、選了之後有可按的「前往此處」、航行計畫視窗打開且沒有錯誤、啟航沒被擋 |
| V1 | `Tests_Visual.cs` | 兩個水體著色器都編譯成功、開放水域只由外海層畫（繪製高度在迷霧之上）、每個開放水域區塊都有網格；近景與全景截圖 |
| V2 | `Tests_Visual.cs` | 艙室層的水位、角落岸邊、牆外的水不滲進艙室、抽乾後沒有網格；外海層裁掉艙室與牆、牆外往艙室淡出；漏水中的截圖 |
| V3 | `Tests_Visual.cs` | 迷霧格（岩體或洞穴）只帶迷霧權重、迷霧邊緣柔和過渡、揭開迷霧後重建；迷霧邊緣截圖 |
| V4 | `Tests_Visual.cs` | 光照寫入頂點（燈下 ≥ 0.6、遠處 ≤ 0.05）、跨區塊自動重建（立燈放在離邊界 3 格處，量鄰區第 3 格）、迷霧格光照恆為 0、潛水服頭燈跟隨與移除、單一區塊重建 ≤ 2 ms、10 名頭燈掃描成本 |
| V5 | `Tests_Visual.cs` | 暗處己方提示的 alpha 與著色器公式一致、敵方沒有提示、開燈後消失、截圖 |
| V6 | `Tests_Visual.cs` | 淺海用淺海材質、除錯切換到深海後整張地圖標記重建且抽樣區塊都換材質、切回自動；截圖 |
| V7 | `Tests_Visual.cs` | 四個層級各拍近景與全景、記錄每個層級的 FPS 並與淺海比較（幀率有雜訊，見指南 §D D5）；截圖 |
| V8 | `Tests_Visual.cs` | 半淹艙室在四個層級下換材質與水色；艙室著色器沒有生物光／霧氣，海洋雪由 `compartmentEffects` 決定（深海／海床為 0 以上）；截圖 |
| V9 | `Tests_Visual.cs` | 深淵地板（P2-10）：著色器惰性解析後不是 `Cutout`、挖滿 11×11、`dontRender`、背景網格、崖邊權重 255 vs 中央 0、水位 100%、Pawn 走不進去、崖邊放燈後「崖邊亮／中央暗」、印出螢幕座標供像素量測；兩張截圖 |
| V10 | `Tests_Visual.cs` | 效能壓力測試（P2-8）：20 盞燈的區塊重建、30 名 Pawn 的提示計算、深淵背景單區塊重建 |
| V11 | `Tests_Phase2.cs` | 深淵挖洞分支（P2-10）：淺海不挖、深海會挖、格數一致、屋頂格不挖；Odyssey 崖壁貼圖、TerrainEdges 層有頂點；深淵滿水且不可走；截圖後還原地形 |
| V12 | `Tests_Phase2.cs` | 玩家設定（P2-5）：`WriteSettings()` 重建材質、粒子歸零、混濁減半、整張地圖標記重建、頭燈覆寫、提示關閉；設定視窗渲染並截圖；還原設定 |
| V13 | `Tests_Audio.cs` | 水中聲音（P2-11）：乾艙室不悶、開放水域與迷霧格完全悶、半淹介於中間、聽者在水中時一律悶、悶音強度 0 不悶；排水泵聲隨抽乾變清楚、關泵停止；層級環境音參數；進水聲、承壓聲、破艙聲只觸發一次 |
| V14 | `Tests_Phase3.cs` | 日光沒有烘進外海層網格（正午與午夜重建結果相同）、`_SkyLight` 跟著日光、頂點 g 只有人造光；艙室材質的最低覆蓋與水線泡沫；北牆正面的水帶（高度跟著水位、頂邊水線、南牆沒有）；有燈的半淹艙室截圖 |
| V15 | `Tests_Phase3.cs` | 水密門：乾處沒潛水服的人打不開、原版可達性走不出去、徵召／潛水員／已在水中的人可通過、檢視字串；受損水密門的進水點會噴水花；截圖 |
| V16 | `Tests_Ocean.cs` | 七種海洋生態區各生成一張地圖：水深層級、水位系統、岩石種類（珊瑚只在珊瑚礁）、海藻、熱液泉、深淵比例；揭開迷霧截圖 |
| V17 | `Tests_Ocean.cs` | 抗壓水密牆圍出密閉艙室、受損滲水約一般牆的 1/10；升降井只能蓋在淺海／變溫層生態區上方 |
| V18 | `Tests_Phase4.cs` | 艙室水面延伸進東西牆的透明邊；室內建築依 fillPercent 淹沒（排水泵有覆蓋、發電機沒有）；屋頂破洞慢慢進水、灌滿才算開放水域；牆體耐久 80/60/30/10% 對應漏水階段 0–3 與進水聲階段 |
| V19 | `Tests_Phase4.cs` | 左下角水位行的文字（開放水域／艙室／地表沒有）與 transpiler 已掛上；海洋地圖的天氣是「海底」 |
| R1 | `Tests_Pawns.cs` | 水線材質在主執行緒建立、使用 CutoutComplex、保留原貼圖、有遮罩、會快取；近景與遠景截圖 |
| B1～B4 | `Tests_Boarding.cs`、`Tests_Boarding2.cs` | 跳幫突擊艇（敵我共用）：B1 敵方艇切入密閉艙室不破密閉、穿潛水服的突擊隊員、撤退回艇脫離；B2 俘虜與戰利品被帶走、被擊落沉沒、艙外破牆、玩家艇在月池上登艇出發降落；B3 海面地圖的月池從正下方的海洋格出發；B4 出發後（艇在世界物件裡）存檔再讀檔，艇上的人與貨都還在 |
| H2 | `Tests_SeaSurface.cs` | 海面的兩層：新的海面地圖只有海；支柱蓋在開放海面上，建立平台層（同尺寸、同格子的口袋地圖）與 5×5 平台、支柱頂；第二根支柱共用平台層；溫度與日照一致；放置規則；兩層的海面遮罩（平台影子、浮船、支柱入水處）；水面層往上伸的柱身、平台層往下畫的柱身；往下看的快照（水面層變了才重建）；搭升降梯上去；拆支柱後平台崩塌、上面的人掉到水面層；拆平台露出高空；兩層一起移除；兩張截圖 |

## 測試環境

- 關閉說書人事件，開啟 `DebugSettings.unlimitedPower`；建築仍要接上電網（排水泵旁放電線）。
- 每個 F／V 測試在井底出口右上方清出一塊區域、在牆圈內蓋人造屋頂、自己圍牆，測試結束時清掉生成的東西與屋頂（屋頂留著會因失去支撐而坍塌）。
- 截圖不是馬上寫檔：`ScreenshotTaker.TakeNonSteamShot` 走 Unity 的 `ScreenCapture.CaptureScreenshot`，
  它是排隊的，實測圖檔會晚好幾幀才輸出（圖裡甚至已經有 vanilla 那句「截圖已儲存」訊息）。因此：
  - `AcceptanceDriver.Screenshot()` 會把 `screenshotCooldown` 設成 3，測試幫浦在這 3 幀內不推進，
    所以「截圖之後立刻換相機」不會被拍進上一張圖。
  - 測試跑完時幫浦會**再多留 20 幀**才結算（`pendingEnd`），因為 `EndTest` → `CleanupAfterTest`
    會把當前地圖切回地表，太早結算的話「每個測試的最後一張」就會拍到地表
    （V4 的 `suit_lamp`、V7 的 `Seabed_wide` 都踩過這個坑）。
  - **新增測試時不要在截圖後立刻換地圖或換相機，並假設它已存檔。**
- 新增測試：寫一個接收 `TestContext`、回傳 `IEnumerable<Wait>` 的方法，再加進 `AcceptanceSuite.Build()`。
