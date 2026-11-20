# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()
start = s.index('## 6. 風險')
new = u'''### 5.5 原版氣候事件與洋流事件

箱網的水溫主要來自 §5.1；原版 `GameCondition` 只要改 `OutdoorTemp`，水溫就會跟著動。除此之外，事件再加下列修正（每 250 tick 套用一次）：

| 原版事件 | 水溫 | 溶氧 | 成長 | 藻華 MTB | 備註 |
|---|---|---|---|---|---|
| `HeatWave` | 已由 `OutdoorTemp` 反映（表層最明顯） | −0.10 | 依適溫曲線 | ×0.6 | 冷水魚會熱緊迫 |
| `ColdSnap` | 已由 `OutdoorTemp` 反映 | +0.05 | 依適溫曲線 | ×1.4 | 熱帶魚可能低溫死亡 |
| `VolcanicWinter` | 已由 `OutdoorTemp` 反映；日照下降 | 0 | ×0.85 | ×1.2 | 浮游植物少，魚長得慢 |
| `ToxicFallout` | 0 | −0.05 | ×0.85 | ×0.8 | 魚獲不帶毒（避免食物中毒系統） |

- 熱浪／寒流對水溫的影響已經在 `OutdoorTemp` 裡，**不要**再加水溫 offset；上表只列「額外」的修正。
- 原版天氣（雨、雷雨、霧、雪）：
  - 灣內：直接套用 §6.1 的 `windSpeedFactor ≥ 1.2`／`rainRate ≥ 0.5` 效果。
  - 外海：`DepthRim_RoughSea`／`DepthRim_RoughSeaStorm` 由海況處理，不再重複套用原版天氣效果；其他原版天氣（雨、霧、雪）套用灣內效果。
- 其他原版事件（日蝕、極光、心靈 drone 等）不特別處理；有改 `OutdoorTemp` 的，水溫會自動跟著。

**洋流（冷暖）事件**：repo 目前沒有洋流事件的 Def；本稿新增兩個 `GameConditionDef`，由 `IncidentDef` 啟動：

| 事件 | 水溫偏移（表層／150 m／500 m） | 溶氧 | 成長 | 藻華 MTB | 洋流發電機 |
|---|---|---|---|---|---|
| `DepthRim_WarmCurrent` | +3.0／+1.4／+0.5 °C | −0.05 | 暖水魚 ×1.1 | ×0.8 | 強度 ×1.15 |
| `DepthRim_ColdCurrent` | −3.0／−1.4／−0.5 °C | +0.05 | 冷水魚 ×1.1 | ×1.2 | 強度 ×1.15 |

- 灣內：水溫偏移 ×0.6（遮蔽水域，交換較慢）。
- 持續 5～15 天，平均間隔 20 天；只在有箱網的地圖發生（沒有箱網時不發信）。
- 暖水魚／冷水魚的判定用 `AquacultureSpeciesDef` 的適溫中點：中點 ≥ 18 °C 算暖水，≤ 8 °C 算冷水；其他不受額外加乘，仍吃 §5.1 的適溫曲線。
- `OceanCurrentMath.Strength` 加一個 `eventFactor`（1.15），洋流發電機和箱網讀同一個事件；事件結束回 1.0。
- 事件对箱網的影響是「水溫偏移＋溶氧＋藻華」，不是新的傷害來源；不會直接破網。

'''
s = s[:start] + new + s[start:]
io.open(p, 'w', encoding='utf-8').write(s)
print('added')
