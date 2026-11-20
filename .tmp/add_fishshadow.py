# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()
start = s.index('## 6. 風險')
new = u'''### 5.6 魚群密度與水中魚影

海面上的魚群不要生成 Pawn；用 Odyssey 的 `FleckDefOf.FishShadow`／`FishShadowReverse` 畫「水中魚影」。這和 Odyssey 的 `Verse.FishShadowComponent` 用同一組 fleck，只是密度由箱網決定。

- `MapComponent_Aquaculture` 每 450 tick 算一次 `fishDensity`（0–1）：

  ```
  perCage = 0.15 + 0.25 × 有魚 + 0.10 × 有飼料 + 0.10 × 藻華
  fishDensity = min(1, Σ perCage)
  ```

- 每 450 tick 生成 `ceil(fishDensity × 4)` 個 fleck，位置在「隨機一座有魚箱網中心 12 格內」的水面格；候選格本身與 8 鄰都必須是水（`DepthRim_OpenSea` 或 `TerrainDef.IsWater`）。
- 生成方式照 `FishShadowComponent`：`FleckMaker.GetDataStatic(loc, map, 隨機 FishShadow 或 FishShadowReverse, 3f × Rand.Range(0.4f, 1.1f))`，`rotation` 隨機 0–180，`rotationRate` ±75×0.75–1.25，`map.flecks.CreateFleck`。
- 只有 `ModsConfig.OdysseyActive` 時才畫；Odyssey 是硬依賴，但 guard 還是要留。
- 灣內：原版 `FishShadowComponent` 可能已經在 `WaterBody` 上畫魚影；本系統只補「箱網附近因投餌增加的魚影」，不要重複畫同一格。
- 總量上限 40 個 fleck；這是純視覺，不生成 Pawn、不影響魚獲。
- `fishDensity` 可給掠食者事件權重 `×(1 + 0.5 × fishDensity)`，讓「魚多」有一點代價；如果不想，保持純視覺。

'''
s = s[:start] + new + s[start:]
io.open(p, 'w', encoding='utf-8').write(s)
print('added')
