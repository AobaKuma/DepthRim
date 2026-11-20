# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()
start = s.index('### 5.1 水溫')
end = s.index('### 5.2 溶氧')
new = u'''### 5.1 水溫（灣內用表面模型；外海用 `OceanTemperature` 深度模型）

**灣內（原版海岸地圖）**：沒有海洋層格可讀，用海面水溫＋氣溫距平。淺水灣的熱容量比外海小，但仍比空氣穩：

```
airAnnual = 地表格的年均氣溫（map.Tile.Tile.temperature）
annual    = OceanTemperature.SurfaceWater(airAnnual)
offset    = map.mapTemperature.OutdoorTemp - airAnnual
waterTemp = clamp(annual + offset * 0.55, -1.8, 29.5)
```

例子（年均氣溫 10 °C）：年均水溫 12.0 °C；夏天 20 °C → 17.5 °C；冬天 0 °C → 6.5 °C。所以灣內藍魚（10～32 °C）只在夏天能養，鱈魚／鮭魚／比目魚全年可以。

**外海（海面地圖）**：正下方的海洋格有水溫模型：`OceanWorldView.TileBelow(map.Tile)` 取得海洋格，`OceanTemperature.AnnualMean(depth, airAnnual, kind)` 算該深度的年均水溫。箱網的水溫是「年均水溫 ＋ 氣溫距平 × 阻尼」：

```
airAnnual   = 地表格的年均氣溫（map.Tile.Tile.temperature）
annual      = OceanTemperature.AnnualMean(netDepth, airAnnual, kind)
offset      = map.mapTemperature.OutdoorTemp - airAnnual
damping     = 0.45 * exp(-netDepth / 80) + 0.15 * exp(-netDepth / 15)
waterTemp   = annual + offset * damping
```

`kind` 用 `OceanTemperature.KindOf(海洋格生態區)`：一般海域／冰下／熱液噴口。熱液噴口區的 `AnnualMean` 會加約 6 °C，所以「冰海上的熱液區」可以養溫水魚——這是地圖給的稀有機會。

外海溫帶海域（年均氣溫 10 °C）的例子：

| 網層 | 網袋中點 | 年均水溫 | 氣溫 ±10 °C 時的擺幅 | 適合 |
|---|---|---|---|---|
| 表層 | 10 m | 12.0 °C | ±4.7 °C | 鱈魚、鮭魚、藍魚、比目魚 |
| 斜溫層 | 150 m | 9.4 °C | ±0.7 °C | 鮭魚、鮪魚、無鬚鱈 |
| 深層 | 500 m | 4.6 °C | ±0.01 °C | 無鬚鱈、霜魚 |

熱帶（年均氣溫 28 °C）與極地（年均氣溫 −10 °C）的同一深度：

| 網層 | 熱帶年均 | 極地年均 | 備註 |
|---|---|---|---|
| 表層 10 m | 26.4 °C | −1.8 °C | 極地表層會結冰，通常沒有開放海面可蓋 |
| 斜溫層 150 m | 20.5 °C | −1.1 °C | 熱帶斜溫層仍暖，可養鮪魚 |
| 深層 500 m | 9.2 °C | 0.2 °C | 熱帶深層對霜魚太暖（上限 10 °C），對鱈魚可以 |

水溫的影響：

| 水溫狀況 | 效果 |
|---|---|
| 在適溫內 | 成長 ×1.0 |
| 超出適溫 0～6 °C | 每 1 °C 成長 −15%，最多 −90% |
| 超出適溫 > 6 °C | 成長 0，每日死亡率 +1% |
| 日夜／季節變化 | 灣內明顯；外海表層明顯，斜溫層幾乎沒有，深層完全沒有 |

'''
s = s[:start] + new + s[end:]
io.open(p, 'w', encoding='utf-8').write(s)
print('replaced', len(new))
