# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()

s = s.replace('| 風險 | 1 個事件＋1 個地圖狀態（＋M3 候選） | 掠食者、藻華；偷魚是 M3 候選 |',
              '| 風險 | 多個事件＋地圖狀態 | 掠食者、藻華、`HeatWave`／`ColdSnap`／`VolcanicWinter`／`ToxicFallout`、冷暖洋流；偷魚是 M3 候選 |')

# §11.3
s = s.replace('| IncidentDef | `DepthRim_CagePredator`（M2）；`DepthRim_CagePoaching`（M3 候選，未定） |',
              '| IncidentDef | `DepthRim_CagePredator`（M2）；`DepthRim_WarmCurrentIncident`、`DepthRim_ColdCurrentIncident`（M2）；`DepthRim_CagePoaching`（M3 候選，未定） |')
s = s.replace('| GameConditionDef | `DepthRim_AlgalBloom`（M2） |',
              '| GameConditionDef | `DepthRim_AlgalBloom`（M2）；`DepthRim_WarmCurrent`、`DepthRim_ColdCurrent`（M2） |')

# §11.4 texture row
s = s.replace('| 魚苗、飼料 | 原版小麥／飼料袋染色 | 專用圖示 |', '| 魚苗 | 原版小麥染色 | 專用圖示 |')

# §12 code files
s = s.replace('  GameCondition_AlgalBloom.cs\n  Alert_*.cs',
              '  GameCondition_AlgalBloom.cs\n  GameCondition_OceanCurrent.cs     冷暖洋流：水溫偏移、溶氧、藻華、eventFactor\n  IncidentWorker_OceanCurrent.cs\n  ClimateMath.cs                    純函式：原版氣候事件與洋流的修正\n  Alert_*.cs')

# §10 add rows after 13
s = s.replace('| 13 | 灣內／外海逃逸魚的存活 | 需要 `FishHabitat.ApplyNoWater` 認得水面地形（`DepthRim_OpenSea`、原版 `WaterOceanShallow`／`WaterOceanDeep`）；否則逃逸魚一生成就窒息。海獺是岸上 Pawn，不游泳。 | 這是一個小補丁，但會影響所有魚；要加單元測試與既有魚的行為回歸（水下地圖照舊看水位）。 |',
              '| 13 | 灣內／外海逃逸魚的存活 | 需要 `FishHabitat.ApplyNoWater` 認得水面地形（`DepthRim_OpenSea`、原版 `WaterOceanShallow`／`WaterOceanDeep`）；否則逃逸魚一生成就窒息。海獺是岸上 Pawn，不游泳。 | 這是一個小補丁，但會影響所有魚；要加單元測試與既有魚的行為回歸（水下地圖照舊看水位）。 |\n| 14 | 原版氣候事件 | 讀 `map.gameConditionManager` 的 `HeatWave`／`ColdSnap`／`VolcanicWinter`／`ToxicFallout`；套用 §5.5 的額外修正。 | 熱浪／寒流的水溫已由 `OutdoorTemp` 反映，不要重複加 offset。 |\n| 15 | 冷暖洋流 | 新增 `DepthRim_WarmCurrent`／`DepthRim_ColdCurrent` GameCondition；箱網讀水溫偏移，`OceanCurrentMath` 讀 `eventFactor`。 | 只在有箱網的地圖發信；灣內偏移 ×0.6；不要直接傷害箱網。 |\n| 16 | 飼料 | 直接用原版 `Kibble`；解鎖 `DepthRim_MakeFishKibble` 後可用魚肉做 55 Kibble。 | 原版 `Make_Kibble` 本來就吃魚肉；不要鎖掉原版配方；槽只收 Kibble。 |')

io.open(p, 'w', encoding='utf-8').write(s)
print('ok')
