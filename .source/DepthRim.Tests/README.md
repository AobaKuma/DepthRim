# DepthRim 單元測試（RimWorld 1.7）

做法比照 DMSE 的 `DMSE.BVRTests`：用真實的 DepthRim 組件和 1.7 遊戲參考組件跑，**不啟動遊戲**。
需要遊戲迴圈的部分（地圖、房間、Pawn、渲染、存讀檔）在 `.source/DepthRim.Acceptance`。

```sh
dotnet run --project .source/DepthRim.Tests/DepthRim.Tests.csproj -c Release -p:OutputPath=<暫存資料夾>/
```

一定要加 `-p:OutputPath`：這個參數也會套用到被參考的 DepthRim 專案，測試用的 DLL 才不會混進 `1.7/Assemblies`。
全部通過時最後一行是 `PASS: N DepthRim checks passed, 0 failed`，結束碼為 0。

| 區段 | 檔案 | 內容 |
|---|---|---|
| FloodingLeak / DoorFlow / Volume / PumpDrainTime | `Tests_Flooding.cs` | `FloodingMath` 的滲水、門流量、水量換算、10×10 艙室抽乾時間 |
| Pressure | `Tests_Flooding.cs` | 水壓倍率與水壓抗性需求 |
| WaterRenderCut | `Tests_Flooding.cs` | Pawn 水線階數：身體／頭部範圍、躺下、單調性 |
| DivingAir | `Tests_Flooding.cs` | 氧氣扣除、回充、上下限、預設容量 |
| WaterCorners | `Tests_Flooding.cs` | 艙室層頂點：只平均同一片水、岸邊程度、不滲進乾艙室 |
| OuterCoverage | `Tests_Flooding.cs` | 外海層涵蓋規則：迷霧格一律涵蓋（不洩漏地形）、裁掉已探索的艙室與艙室牆 |
| OpenFillAndLeakStages | `Tests_Flooding.cs` | 露天房間進水速度（邊緣 vs 屋頂破洞、水壓、上限）、漏水階段與效果／進水聲參數的單調性、建築依 fillPercent 淹沒 |
| FloodingVisual | `Tests_Flooding.cs` | 水體視覺：波紋週期、光照換算（`LightOf`／`AverageLight`）、混濁度 `WaterAlpha`（單調、上下限、不除零）、己方提示 `HintAlpha`（門檻以下為 0、單調、上限）與提示顏色 |
| ContentXml / DefFields / DefOf / Types / Keyed | `Tests_Content.cs` | XML 能解析、Def 欄位存在（含模組自己的 Def 型別）、DefOf 都有 Def、XML 引用的類別存在、翻譯鍵英文與繁中一致 |
| ContentShaders | `Tests_Content.cs` | ShaderDef 引用的 GLSL 檔存在（模組或原版）、模組檔名有 `DepthRim_` 前綴、`#version 450`、括號成對、`MaterialUniforms` 每個欄位都有 `@default`（全域值除外）、區塊 ≤ 512 bytes |
| ContentWaterTiers | `Tests_Content.cs` | `WaterVisualTiers.xml`：四個層級各一個、`defName` 不重複、C.1 的欄位都在、強度值 0–1、顏色三分量 0–1、C.5 的初始值抽查 |
| AudioMath | `Tests_Audio.cs` | 水中聲音（P2-11）：悶音取聲源與聽者較濕的一方、截止頻率與音量的端點與單調性、深海／海床 −20%、船殼承壓聲機率與 30% 上限 |
| WatertightDoor | `Tests_Audio.cs` | 水密門通行規則的真值表、門檻與溺水門檻一致、受損滲水倍率介於 0 與 1 |
| OceanBiomes | `Tests_Ocean.cs` | 海洋生態區分配規則（極地優先、海岸珊瑚／海藻、熱液與海溝不在海岸、遠洋深珊瑚／平原、沒有地表資料的退路） |
| ContentOcean | `Tests_Ocean.cs` | 七種生態區各一個 BiomeDef（WaterMapExtension、水深層級、forceRockTypes）、升降井可達與不可達的都有、三張牆體貼圖存在且被 Def 使用 |
| ContentSounds | `Tests_Audio.cs` | SoundDef 引用的音檔與資料夾都在 `Sounds/`、每個音檔都列在 `Sounds/CREDITS.md` 且為 CC0、四個層級各一個環境音並掛在冰洋生態域、DefOf 的 SoundDef 都有定義 |
| ContentDefInjected | `Tests_Content.cs` | DefInjected 每個鍵的 Def 與欄位都存在；資料夾名稱能被遊戲的 `GenTypes` 解析（模組自己的型別要寫完整名稱，例如 `DepthRim.WaterVisualTierDef`） |
| HarmonyTargets | `Tests_Content.cs` | 每個 `[HarmonyPatch]` 的目標方法、`___欄位`、參數名稱在目前的 1.7 組件裡都存在 |

新增區段：在 `Tests_*.cs` 寫一個 `static void`，再到 `Program.cs` 用 `Run("名稱", 方法)` 加進去。
地圖與房間相關的邏輯先抽成純函式（例如 `FloodingMath`），才能在這裡測。
