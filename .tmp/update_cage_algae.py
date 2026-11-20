# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()
start = s.index('### 6.3 藻華與缺氧')
end = s.index('### 6.4 病害')
new = u'''### 6.3 藻華與缺氧

| 項目 | 內容 |
|---|---|
| 形式 | `GameConditionDef DepthRim_AlgalBloom`，地圖級，3～7 天。 |
| 觸發（灣內） | 海岸地圖、至少 2 座有魚的箱網、戶外溫度 > 18 °C 連續 5 天、前 3 天有餵食；平均間隔 20 天（岸上逕流讓灣內更容易）。 |
| 觸發（外海） | 海面地圖、至少 2 座有魚的箱網、戶外溫度 > 18 °C 連續 5 天、風速 < 0.7；平均間隔 25 天。 |
| 效果 | 所有箱網溶氧 −0.18；水面出現綠色薄層（用既有的水花 effecter 或簡單的綠色 overlay，不另做著色器）；訊息提示「藻華：停止餵食、開曝氣機」。 |
| 對策 | 把 `feedRateSetting` 調到 0～50%、開曝氣機、緊急收成。 |
| 結束 | 天氣轉涼、或時間到。 |
| 連鎖 | 藻華期間若又停電，溶氧會掉到 0.15 以下，觸發缺氧死亡。 |

'''
s = s[:start] + new + s[end:]
io.open(p, 'w', encoding='utf-8').write(s)
print('replaced', len(new))
