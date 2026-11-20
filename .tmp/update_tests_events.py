# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()

s = s.replace('| `NetCageMath.Oxygen` | 六種情境（§5.2 的表）逐項比對；相鄰懲罰上限；曝氣上限；灣內天氣 −0.08。 |',
              '| `NetCageMath.Oxygen` | 六種情境（§5.2 的表）逐項比對；相鄰懲罰上限；曝氣上限；灣內天氣 −0.08；氣候／洋流修正。 |')
s = s.replace('| `AquacultureDefTests` |',
              '| `ClimateMath.VanillaEvents` | `HeatWave`／`ColdSnap`／`VolcanicWinter`／`ToxicFallout` 的 O2／成長／藻華修正；熱浪寒流不重複加水溫。 |\n| `ClimateMath.OceanCurrent` | 暖／冷洋流在各深度的水溫偏移、灣內 ×0.6、`eventFactor` 1.15、適溫中點判定。 |\n| `AquacultureDefTests` |')

s = s.replace('### 15.2 遊戲內驗收（`DepthRim.Acceptance`，編號 Q0～Q8）',
              '### 15.2 遊戲內驗收（`DepthRim.Acceptance`，編號 Q0～Q10）')
s = s.replace('| Q8 | 逃逸魚存活 | 灣內與外海各強制一次破網；觀察 1 天。 | 逃逸魚生成 ≤10、在水面存活、可以獵殺；沒有窒息 hediff；沒有 ERROR。 |',
              '| Q8 | 逃逸魚存活 | 灣內與外海各強制一次破網；觀察 1 天。 | 逃逸魚生成 ≤10、在水面存活、可以獵殺；沒有窒息 hediff；沒有 ERROR。 |\n| Q9 | 原版氣候事件 | 依序強制 `HeatWave`、`ColdSnap`、`VolcanicWinter`、`ToxicFallout`；觀察水溫、溶氧、成長、藻華。 | 水溫只由 `OutdoorTemp` 變；O2／成長／藻華符合 §5.5；沒有重複計算；沒有 ERROR。 |\n| Q10 | 冷暖洋流 | 強制 `DepthRim_WarmCurrent`／`DepthRim_ColdCurrent`；在表層、150 m、500 m 各放一座。 | 水溫偏移符合 §5.5；灣內 ×0.6；O2／藻華／成長有變；洋流發電機 `eventFactor` 1.15；沒有 ERROR。 |')

s = s.replace('| **M2 風險** | 溶氧、曝氣機、藻華、病害、逃逸、警報、Q3／Q7 |',
              '| **M2 風險** | 溶氧、曝氣機、藻華、病害、逃逸、警報、原版氣候事件、冷暖洋流、Q3／Q7／Q9／Q10 |')

io.open(p, 'w', encoding='utf-8').write(s)
print('ok')
