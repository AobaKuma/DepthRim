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
| 灣內內容 | 1～2 隻海獺（`DepthRim_SeaOtter`，新 Pawn）從**岸上**出現，走到岸邊，攻擊 6 格內、貼岸或貼平台的箱網，咬網（30～60 傷害），打完就離開。海獺**不游泳**，所以不需要新的水中尋路；代價是「貼岸的箱網一定在牠的範圍內」。 |
| 外海內容 | 1 隻睡鯊（重用 `DepthRim_Fish_Large`，掛 `FishVariant_SleeperShark` 與 `CompNetRaider`）從地圖邊緣的水裡出現，游向箱網，咬網（30～60 傷害），打完就離開。外海沒有岸，海獺不出現。 |
| 對策 | 獵殺、在岸邊／橋上佈防、蓋更貴的深層網（外海）；灣內可以把箱網往離岸遠一點的地方挪（但連通仍然要橋或平台，不能只靠船）。 |
| 生物從簡 | 只做一種新 Pawn（海獺，岸上）；睡鯊用既有的 `DepthRim_Fish_Large` 加 variant 與 `CompNetRaider`，不做完整的鯊魚 AI。 |

> 掠食者是「玩家看得到、可以打」的風險，和風暴（只能防）與病害（只能治）區隔。灣內的海獺是岸上來的，不是水裡來的——這也是為什麼灣內箱網仍然要留一條巡邏路線。

'''
s = s[:start] + new + s[end:]
io.open(p, 'w', encoding='utf-8').write(s)
print('replaced', len(new))
