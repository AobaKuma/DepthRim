# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()
start = s.index('| # | 系統 | 怎麼接 | 要注意 |')
end = s.index('\n\n---\n\n## 11. Def 清單')
new = u'''| # | 系統 | 怎麼接 | 要注意 |
|---|---|---|---|
| 1 | 場地與前往 | 灣內：原版海岸地圖；外海：船開到海面地圖（§4.9）。除錯動作「Create sea-surface map」仍可用。 | 船的系統（§4.9 M1～M4）未實作；灣內版不需要船，先做先玩。 |
| 2 | 灣內水域 | 原版 `WaterOceanShallow`／`WaterOceanDeep` 加 `DepthRim_HullBase` 承載力（§4.9 M0）；船體平台可以鋪在灣裡；箱網放在水裡，靠岸邊／平台／橋。 | 不要讓箱網蓋在船體平台上（那是甲板）；`PlaceWorker_NetCage` 只收水域。 |
| 3 | 水深與水溫 | 灣內：表面水溫＋氣溫距平（§5.1）；外海：`OceanWorldView.TileBelow`＋`OceanTemperature.AnnualMean`。 | 灣內沒有海洋層格，不要對陸地呼叫 `AnnualMean`；灣內水深用 10／50 m 的設計值。 |
| 4 | 海況傷害 | 外海用 `MapComponent_SeaWeather.BatterBuildings`；灣內不跑這個 component，只有原版天氣壓力。 | `MapComponent_SeaWeather` 有 `SeaLevels.IsSeaLevel` 守衛，不要為灣內繞過它；灣內不要誤吃海況傷害。 |
| 5 | 落海 | 外海橋上的 Pawn 是「艙外」，`MapComponent_RoughSeaOverboard` 會擲骰；灣內海岸地圖的 `HazardOf` 不是 `RoughSea`。 | 灣內箱網不用安全繩；外海農場要在錨點 9 格內。 |
| 6 | 海面兩層／快照 | 平台層的 `SectionLayer_LowerLevel` 會畫水面層的箱網；灣內是普通地圖，沒有快照。 | 快照延遲 2 秒；灣內沒有 `_Hull` 水線遮罩，箱網靠自己的貼圖與浪花，不要假設有海面著色器。 |
| 7 | 魚的品種 | 放養時把 `AquacultureSpeciesDef.variant` 記在 comp；逃逸生成 Pawn 後 `CompFishCatch.SetVariant(variant)`。 | 收成不生成 Pawn，直接 `GenSpawn` 魚獲。 |
| 8 | 魚獲與食物 | 用 Odyssey 的 `Fish_*`；不新增魚獲物品。 | 魚獲會腐壞（2 天），收成後要立刻入庫或冷藏。 |
| 9 | 研究與海底知識 | 一般研究接 `DepthRim_ResearchTab`；深水接 `DepthRim_Seabed`；海岸開局用潛水鐘採樣（§4.9）。 | 沒有船與鐘就採不到海底研究；深水箱網會被卡住。 |
| 10 | 船的角色 | 船運飼料／魚苗／魚獲、把玩家送到外海；箱網的互動格必須是靜態結構。 | 不要讓箱網只靠船當連通；船一離港，箱網不能變成無人可達。 |
| 11 | 沒有水面戰鬥 | 偷魚移到 M3 候選；第一版只有自然風險。 | 不要為了箱網引入接舷戰或海盜船。 |
| 12 | 模組設定 | `DepthRimModSettings.enableNetCages`（預設 true）；關掉時隱藏箱網的藍圖與事件，已存在的箱網停止運作。 | 關掉不影響舊存檔；箱網留在原地，只是不再產出。 |'''
s = s[:start] + new + s[end:]
io.open(p, 'w', encoding='utf-8').write(s)
print('replaced', len(new))
