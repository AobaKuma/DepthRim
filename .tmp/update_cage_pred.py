# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()
start = s.index('### 6.2 掠食者')
end = s.index('### 6.3 藻華與缺氧')
new = u'''### 6.2 掠食者

| 項目 | 內容 |
|---|---|
| 事件 | `DepthRim_CagePredator`（IncidentWorker）。只在有「有魚的箱網」的地圖發生；灣內與外海都適用。 |
| 觸發 | 權重：灣內 0.5、外海 0.6；最小間隔 5 天；需要地圖上有箱網且至少一座有魚。 |
| 灣內內容 | 1～2 隻海獺（`DepthRim_SeaOtter`，新 Pawn，淺海／海藻林）從岸邊或地圖邊緣的水裡出現，游向箱網，咬網（30～60 傷害），打完就離開。 |
| 外海內容 | 表層網：海獺；斜溫／深層網：1 隻睡鯊（重用 `DepthRim_Fish_Large`，掛 `FishVariant_SleeperShark` 與 `CompNetRaider`）。 |
| 對策 | 獵殺、在橋／岸邊佈防、蓋更貴的深層網（海獺只咬表層網）、把箱網蓋在離岸遠一點的地方（灣內事件權重 ×0.5）。 |
| 生物從簡 | 只做一種新 Pawn（海獺）；睡鯊用既有的 `DepthRim_Fish_Large` 加 variant 與 `CompNetRaider`，不做完整的鯊魚 AI。 |

> 掠食者是「玩家看得到、可以打」的風險，和風暴（只能防）與病害（只能治）區隔。灣內的海獺也讓「岸邊箱網」不是完全安全。

'''
s = s[:start] + new + s[end:]
io.open(p, 'w', encoding='utf-8').write(s)
print('replaced', len(new))
