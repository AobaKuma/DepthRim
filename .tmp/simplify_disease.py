# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()

s = s.replace('    float diseaseSeverity;           // 0～1；0 = 健康\n    CageDisease disease;             // None / Lice / FinRot\n',
              '    float diseaseSeverity;           // 0～1；0 = 健康，只有一種病害\n    int lastSpreadCheck;             // 上次擴散判定的 tick\n')
s = s.replace('diseaseFactor = 1 - 0.35 * diseaseSeverity', 'diseaseFactor = 1 - 0.25 * diseaseSeverity')
s = s.replace('               + 0.01 * diseaseSeverity', '               + 0.0015 * diseaseSeverity')

start = s.index('### 6.4 病害')
end = s.index('### 6.5 偷魚')
new = u'''### 6.4 病害（簡化：一種病害，重點是擴散）

不再分海蝨／鰭腐。每座箱網只有一個 `diseaseSeverity`（0～1），病害的壞處不是「慢慢死光」，而是**會擴散到鄰近的箱網**；養得越密，傳得越快。

| 項目 | 內容 |
|---|---|
| 發生 | 每日檢查，平均 40 天；水溫 > 18 °C、O2 < 0.5、密度 > 0.9 時，間隔 ×0.7（可疊加）。 |
| 潛伏 | 發生後 severity 從 0.3 開始，每天 +0.03，最高 1.0。 |
| 直接影響 | 成長 ×(1 − 0.25×severity)；死亡率 +0.0015×severity／日（severity 1 時 +0.15%/日）。**不是主要死因**。 |
| 擴散 | 每 3 天檢查一次。對每個 **12 格內**（中心距離）的其他箱網：`chance = 0.15 × severity × crowding`。 |
| crowding | `min(2.0, 1 + 0.25 × (鄰居數 − 1))`；鄰居數是 12 格內的其他箱網數。沒有鄰居就不會擴散。 |
| 密集養殖 | 4 座相鄰的箱網，crowding = 1.75；加上 severity 上升，感染力會快速放大。 |
| 治療 | 600 工作＋5 藥品，severity −0.4；草藥效果 ×0.5。治療不會立刻停止擴散，只是降低 severity 與擴散率。 |
| 清空 | 收成或清空箱網直接移除病害；這是最乾脆的「撲殺」。 |
| 自癒 | 12 格內沒有其他箱網時，10 天後 severity 每日 −0.02，歸零自癒。 |
| 訊息 | 同一張地圖每天最多一則「病害擴散到 N 座箱網」；不逐座發信。 |

- 病害是箱網的狀態，不是 Pawn 的 Hediff；只存 `diseaseSeverity` 與 `lastSpreadCheck`。
- 治療工作由「養殖」工作類型處理；沒有藥品時可以用草藥（效果 ×0.5）。
- **設計重點**：一座孤立的箱網感染後會自己好；一排密集的箱網會連環中。玩家要嘛拉開距離，要嘛及早治療／撲殺，不能只蓋一排。
- 病害不影響魚獲的食用安全（避免食物中毒系統）；它影響的是成長、死亡與隔壁的箱網。

'''
s = s[:start] + new + s[end:]

s = s.replace('| `DepthRim_CageDisease` | 黃色 | 任一座箱網病害 severity > 0.3 |',
              '| `DepthRim_CageDisease` | 黃色 | 任一座箱網病害 severity > 0.2 |')
s = s.replace('| 病害 | 海蝨 30 天、鰭腐 20 天 | 治療 2 次 |',
              '| 病害 | 發生 MTB 40 天；擴散每 3 天、12 格內、每鄰居 15%×severity×crowding | 治療 600 工作＋5 藥品（−0.4） |')
s = s.replace('| `NetCageMath.Growth` | 適溫內 18 天到 100%；超出 3 °C 的減速；沒飼料 ×0；病害 ×0.65；灣內雨天 ×0.8。 |',
              '| `NetCageMath.Growth` | 適溫內 18 天到 100%；超出 3 °C 的減速；沒飼料 ×0；病害 severity 1 時 ×0.75；灣內雨天 ×0.8。 |')
s = s.replace('| `ClimateMath.OceanCurrent` | 暖／冷洋流在各深度的水溫偏移、灣內 ×0.6、`eventFactor` 1.15、適溫中點判定。 |',
              '| `ClimateMath.OceanCurrent` | 暖／冷洋流在各深度的水溫偏移、灣內 ×0.6、`eventFactor` 1.15、適溫中點判定。 |\n| `DiseaseSpread` | 12 格內鄰居數、crowding、每 3 天的感染率、隔離自癒、治療後 severity；純函式。 |')
s = s.replace('| Q7 | 病害與治療（M2） | 強制海蝨；不治療 10 天；治療兩次。 | 成長下降、死亡上升；治療後 severity 下降；兩次治癒。 |',
              '| Q7 | 病害與擴散（M2） | 感染 3 座相鄰箱網的中間那座；觀察 15 天；治療；另放一座 15 格外、同條件箱網。 | 相鄰箱網陸續感染；隔離那座不感染且會自癒；治療後 severity 下降；清空箱網移除病害；沒有 ERROR。 |')

io.open(p, 'w', encoding='utf-8').write(s)
print('ok')
