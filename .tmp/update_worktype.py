# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()

s = s.replace('| 工作 | 1 個工作類型＋6 個工作 | 放養、收成、補飼料、修網、治療、收魚苗 |',
              '| 工作 | 6 個工作（不新增工作類型） | 放養、收成、補飼料、修網、治療、收魚苗；掛在馴獸（Handling）與監管（Warden） |')

start = s.index('### 8.1 工作類型')
end = s.index('### 8.2 檢視字串')
new = u'''### 8.1 工作類型

**不新增 `WorkTypeDef`。** 六個 WorkGiver 掛在既有的 **馴獸（Handling）** 與 **監管（Warden）**：

| WorkGiver | 工作類型 | 技能 | 觸發 | 工作 | 時間 |
|---|---|---|---|---|---|
| `WorkGiver_StockCage` | 馴獸 Handling | Animals | 箱網已選品種、空箱、庫存有足夠魚苗 | 搬魚苗→放養 | 1200 |
| `WorkGiver_HarvestCage` | 馴獸 Handling | Animals | 可收成，或玩家標記緊急收成 | 收成 | 1500 |
| `WorkGiver_RefillCageFeed` | 馴獸 Handling | Animals | 飼料槽 < 40%、庫存有 Kibble | 搬 Kibble→補槽 | 600 |
| `WorkGiver_RepairCageNet` | 馴獸 Handling | Construction | HP < 50%、有布與鋼 | 修網 | 800 |
| `WorkGiver_TreatCage` | 監管 Warden | Medicine | 有病害、有藥品 | 治療 | 1200 |
| `WorkGiver_CollectFry` | 馴獸 Handling | Animals | 魚苗網 ≥ 30 尾 | 收集魚苗 | 250 |

- 工作類型只決定**優先度**；實際速度看技能。修網掛在馴獸、但用建造技能；治療掛在監管、用醫療技能。
- 為什麼不新增工作類型：工作類型是全域優先度表；箱網的六件事都能歸到馴獸／監管，新增一個只會讓玩家的優先度表多一格。代價是修網會跟動物工作搶人、治療會跟監管工作搶人，必要時用允許區域或手動指令微調。

'''
s = s[:start] + new + s[end:]

s = s.replace('| WorkTypeDef | `DepthRim_Aquaculture` |\n', '')
s = s.replace('  WorkGiver_*.cs / JobDriver_*.cs 六個工作',
              '  WorkGiver_*.cs / JobDriver_*.cs 六個工作（Handling／Warden）')
s = s.replace('| `AquacultureDefTests` | 每個品種的 variant 存在、網層與溫區有交集、`stockCount × harvestWeight ≤ 300`、`yieldPerFish` 合法、`DepthRim_MakeFishKibble` 存在且 research 正確；`coastal = true` 的品種必須有表層網且適溫與灣內溫區有交集。 |',
              '| `AquacultureDefTests` | 每個品種的 variant 存在、網層與溫區有交集、`stockCount × harvestWeight ≤ 300`、`yieldPerFish` 合法、`DepthRim_MakeFishKibble` 存在且 research 正確；`coastal = true` 的品種必須有表層網且適溫與灣內溫區有交集；六個 WorkGiver 的 `workType` 只能是 `Handling`／`Warden`。 |')
s = s.replace('20. **病害只做擴散，夠不夠？** 本稿保留「成長 −25%、死亡率 +0.15%/日」作為壓力，但主要靠擴散；如果覺得太溫和，把 severity 的直接死亡率調高，不要把擴散再複雜化。',
              '20. **病害只做擴散，夠不夠？** 本稿保留「成長 −25%、死亡率 +0.15%/日」作為壓力，但主要靠擴散；如果覺得太溫和，把 severity 的直接死亡率調高，不要把擴散再複雜化。\n21. **工作類型共用馴獸／監管**：本稿不新增 WorkTypeDef；修網掛馴獸（建造技能）、治療掛監管（醫療技能）。如果覺得優先度表太擠，再考慮獨立「養殖」工作類型。')
io.open(p, 'w', encoding='utf-8').write(s)
print('ok')
