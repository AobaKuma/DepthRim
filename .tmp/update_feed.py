# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()

s = s.replace('| 建築 | 6 個 Def | 箱網 ×3（表層／斜溫／深層；表層兩邊通用）、魚苗網、飼料機、曝氣機 |',
              '| 建築 | 5 個 Def | 箱網 ×3（表層／斜溫／深層；表層兩邊通用）、魚苗網、曝氣機 |')
s = s.replace('| 物品 | 2 個 | 魚苗、魚飼料 |',
              '| 物品 | 1 個 | 魚苗（飼料直接用原版 `Kibble`，不新增物品） |')
s = s.replace('| 新貼圖 | 6～8 張（暫代可先染色） | 三種浮框、魚苗網、飼料機、曝氣機、魚苗、飼料 |',
              '| 新貼圖 | 5～7 張（暫代可先染色） | 三種浮框、魚苗網、曝氣機、魚苗 |')

s = s.replace('蓋飼料機、做飼料 ──► 補充飼料槽 ──► 魚成長（18～30 天）',
              '做原版 Kibble ──► 補充飼料槽 ──► 魚成長（18～30 天）')

# §3.3 remove feed mixer + fish feed rows
s = s.replace('| 建築 | `DepthRim_FeedMixer` | 2×1 | 鋼 40＋零件 2，400 工作 | 0（手搖） | 180 | 箱網養殖 | 飼料機：把乾草／魚雜做成魚飼料。蓋在陸地或平台上。 |\n', '')
s = s.replace('| 物品 | `DepthRim_FishFeed` | — | — | — | — | — | 魚飼料。堆疊 75、重 0.03、價 0.8；不是食物、不會腐壞。 |\n', '')

# replace recipe table block
old = u'''飼料配方（`DepthRim_FeedMixer` 的 bills）：

| 配方 | 材料 | 產出 | 工作 |
|---|---|---|---|
| 魚飼料（草食） | 乾草 25 | 飼料 30 | 300 |
| 魚飼料（魚雜） | 生魚／生肉 10 | 飼料 30 | 300 |

> 魚雜配方是給「收成後不要的雜魚、腐壞前的魚獲」一個去處；也讓「養魚」和「賣魚」之間有取捨。'''
new = u'''**飼料＝原版 `Kibble`**：箱網的飼料槽只收 Kibble，不新增飼料物品，也不新增飼料機。原版 `Make_Kibble` 在屠宰台（`TableButcher`）／屠宰點（`ButcherSpot`）本來就吃魚肉——Odyssey 的 `Fish` 分類父類是 `MeatRaw`。

解鎖 `DepthRim_Aquaculture` 後，額外解鎖 `DepthRim_MakeFishKibble`：

| 配方 | 材料 | 產出 | 工作 |
|---|---|---|---|
| 魚肉飼料（`DepthRim_MakeFishKibble`） | 1 營養魚肉＋1 營養植物（乾草或作物） | **Kibble 55** | 450 |

- 這是「只用魚肉」的便利 bill，让玩家把雜魚、過熟的魚獲消化掉；原版 `Make_Kibble` 仍然可用，沒有被鎖。
- 比原版 50 多 10%，是解鎖科技的小回報；如果覺得多餘，改成 50 也可以。
- 箱網槽裡的 Kibble 是食物，搬運工作從一般食材庫存取；不要讓殖民者直接吃掉槽裡的儲備（槽不是 stockpile）。'''
assert old in s
s = s.replace(old, new)

# §3.4 labels
s = s.replace('└──[船體平台／橋]──[飼料機]', '└──[船體平台／橋]──[Kibble 堆]')
s = s.replace('│  飼料機   曝氣機   │', '│  Kibble 堆 曝氣機  │')

# §7 product flow
s = s.replace('雜魚 → 飼料機的魚雜配方。', '雜魚 → 原版 Kibble 的魚肉配方。')

# §9 research unlock
s = s.replace('| 箱網養殖 | `DepthRim_Aquaculture` | 800（一般） | 水下（`DepthRim_ResearchTab`） | `Fishing`、`Electricity` | 灣內／外海表層網、魚苗網、飼料機、魚苗／飼料 |',
              '| 箱網養殖 | `DepthRim_Aquaculture` | 800（一般） | 水下（`DepthRim_ResearchTab`） | `Fishing`、`Electricity` | 灣內／外海表層網、魚苗網、魚苗、魚肉飼料 bill（`DepthRim_MakeFishKibble`） |')

# §11.1 remove feed mixer + fish feed
s = s.replace('| `DepthRim_FeedMixer` | Building | 2×1、`Building_WorkTable`、bills、陸地／平台 |\n', '')
s = s.replace('| `DepthRim_FishFeed` | Item | 堆疊 75、重 0.03、價 0.8、`thingCategories=ResourcesRaw` |\n', '')

# §11.3 add recipe
s = s.replace('| ResearchProjectDef | `DepthRim_Aquaculture`、`DepthRim_WaterQuality`、`DepthRim_DeepNetCages` |',
              '| ResearchProjectDef | `DepthRim_Aquaculture`、`DepthRim_WaterQuality`、`DepthRim_DeepNetCages` |\n| RecipeDef | `DepthRim_MakeFishKibble`（`researchPrerequisite=DepthRim_Aquaculture`；`TableButcher`／`ButcherSpot`；1 營養魚肉＋1 營養植物 → Kibble 55） |')

# §11.4 remove feed mixer texture
s = s.replace('| 飼料機 | 原版屠宰台染色 | 攪拌槽＋料斗 |\n', '')

# §12 remove feed mixer file
s = s.replace('  Building_FeedMixer.cs\n', '')

# §13 feed row
s = s.replace('| 飼料 | 30／批，乾草 25 或魚雜 10 | 300 工作 |',
              '| 飼料 | 原版 `Kibble`（0.05 營養／個） | 魚肉飼料 55／批（1 營養魚肉＋1 營養植物） |')

# §15.1 test
s = s.replace('`stockCount × harvestWeight ≤ 300`、`yieldPerFish` 合法、飼料配方存在；',
              '`stockCount × harvestWeight ≤ 300`、`yieldPerFish` 合法、`DepthRim_MakeFishKibble` 存在且 research 正確；')

# §16 M0
s = s.replace('表層網（灣內）、魚苗網、飼料機、魚苗／飼料、放養／成長／收成',
              '表層網（灣內）、魚苗網、魚苗／Kibble、放養／成長／收成')

# §18
s = s.replace('- 不做船上的箱網、甲板水槽、移動式養殖船。',
              '- 不做船上的箱網、甲板水槽、移動式養殖船。\n- 不自訂飼料物品；飼料就是原版 `Kibble`，魚肉配方只是解鎖後的便利 bill。')

io.open(p, 'w', encoding='utf-8').write(s)
print('ok')
