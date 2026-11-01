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
