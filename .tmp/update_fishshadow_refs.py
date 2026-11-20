# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()

s = s.replace('| 17 | 工作類型 | 六個 WorkGiver 指到 `WorkTypeDefOf.Handling`／`Warden`；修網用建造技能、治療用醫療技能。 | 不要新增 WorkTypeDef；玩家的優先度表不變。 |',
              '| 17 | 工作類型 | 六個 WorkGiver 指到 `WorkTypeDefOf.Handling`／`Warden`；修網用建造技能、治療用醫療技能。 | 不要新增 WorkTypeDef；玩家的優先度表不變。 |\n| 18 | 水中魚影 | 重用 Odyssey `FleckDefOf.FishShadow`／`FishShadowReverse`；`MapComponent_Aquaculture` 依 `fishDensity` 在箱網附近生成 fleck。 | 不要生成 Pawn；不要和 `FishShadowComponent` 重複；`OdysseyActive` guard。 |')

s = s.replace('  ClimateMath.cs                    純函式：原版氣候事件與洋流的修正',
              '  ClimateMath.cs                    純函式：原版氣候事件與洋流的修正\n  FishShadowField.cs                魚群密度、fleck 生成、總量上限')

s = s.replace('| `DiseaseSpread` | 12 格內鄰居數、crowding、每 3 天的感染率、隔離自癒、治療後 severity；純函式。 |',
              '| `DiseaseSpread` | 12 格內鄰居數、crowding、每 3 天的感染率、隔離自癒、治療後 severity；純函式。 |\n| `FishShadowField` | `fishDensity` 的加總與上限、候選格（水＋8 鄰水）、每 450 tick 的 fleck 數量。 |')

s = s.replace('| Q10 | 冷暖洋流 |',
              '| Q11 | 水中魚影 | 3 座有魚箱網、餵食、藻華；觀察 450 tick；另開一張沒有箱網的地圖。 | `FishShadow`／`FishShadowReverse` 出現在箱網 12 格內；密度隨魚數增加、沒有 Pawn、沒有 ERROR；沒有箱網的地圖不生成。 |\n| Q10 | 冷暖洋流 |')

s = s.replace('| **M2 風險** | 溶氧、曝氣機、藻華、病害、逃逸、警報、原版氣候事件、冷暖洋流、Q3／Q7／Q9／Q10 |',
              '| **M2 風險** | 溶氧、曝氣機、藻華、病害、逃逸、警報、原版氣候事件、冷暖洋流、水中魚影、Q3／Q7／Q9／Q10／Q11 |')

s = s.replace('| 水中魚影 | Odyssey `FishShadow`／`FishShadowReverse` | 不用新貼圖 |\n', '')
s = s.replace('| 魚苗 | 原版小麥染色 | 專用圖示 |',
              '| 魚苗 | 原版小麥染色 | 專用圖示 |\n| 水中魚影 | Odyssey `FishShadow`／`FishShadowReverse` | 不用新貼圖 |')

s = s.replace('21. **工作類型共用馴獸／監管**：本稿不新增 WorkTypeDef；修網掛馴獸（建造技能）、治療掛監管（醫療技能）。如果覺得優先度表太擠，再考慮獨立「養殖」工作類型。',
              '21. **工作類型共用馴獸／監管**：本稿不新增 WorkTypeDef；修網掛馴獸（建造技能）、治療掛監管（醫療技能）。如果覺得優先度表太擠，再考慮獨立「養殖」工作類型。\n22. **魚影要不要影響掠食者**：本稿讓 `fishDensity` 給掠食者權重 ×(1+0.5×density)；如果只想純視覺，把這條拿掉。')

io.open(p, 'w', encoding='utf-8').write(s)
print('ok')
