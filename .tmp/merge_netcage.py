# -*- coding: utf-8 -*-
import io
p = 'Docs/箱網養殖_內容設計.md'
s = io.open(p, encoding='utf-8').read()

s = s.replace('| `netDepth` | `NetCageExtension`（ThingDef） | 10／150／500 m。 |',
              '| `netDepth` | `CompProperties_NetCage`（ThingDef 的 comp） | 10／150／500 m。 |')
s = s.replace('海況傷害用 `NetCageExtension.stormDamageFactor = 1.5` 放大',
              '海況傷害用 `CompProperties_NetCage.stormDamageFactor = 1.5` 放大')

s = s.replace('`NetCageExtension{netDepth=10,minWaterDepth=10}`、HP 300、灣內＋外海',
              '`CompProperties_NetCage{netDepth=10,minWaterDepth=10,stormGrowthFactor=0.5,stormDamageFactor=1.5,capacityKg=300,feedCapacity=240}`、HP 300、灣內＋外海')
s = s.replace('| `DepthRim_NetCage_Midwater` | Building | 同上，`netDepth=150,minWaterDepth=180`、HP 360、只外海 |',
              '| `DepthRim_NetCage_Midwater` | Building | 同上，`netDepth=150,minWaterDepth=180,stormGrowthFactor=0.8,stormDamageFactor=1.5`、HP 360、只外海 |')
s = s.replace('| `DepthRim_NetCage_Deep` | Building | 同上，`netDepth=500,minWaterDepth=550`、HP 420、只外海 |',
              '| `DepthRim_NetCage_Deep` | Building | 同上，`netDepth=500,minWaterDepth=550,stormGrowthFactor=1.0,stormDamageFactor=1.5`、HP 420、只外海 |')

s = s.replace('| `DepthRim.NetCageExtension` | 掛在三個箱網 ThingDef | `netDepth`、`minWaterDepth`、`stormGrowthFactor`、`stormDamageFactor`（9×9 用 1.5） |\n| `DepthRim.CompProperties_NetCage` | 箱網 comp | `capacityKg=300`、`feedCapacity=240` |',
              '| `DepthRim.CompProperties_NetCage` | 箱網 comp（原 `NetCageExtension` 已合併） | `netDepth`、`minWaterDepth`、`stormGrowthFactor`、`stormDamageFactor`（9×9 用 1.5）、`capacityKg=300`、`feedCapacity=240` |')

s = s.replace('  NetCageExtension.cs             網層資料\n', '')
s = s.replace('  CompNetCage.cs                  魚群資料、每 250 tick、gizmos、inspect、存檔',
              '  CompNetCage.cs                  魚群資料、CompProperties（網層／容量／風暴係數）、每 250 tick、gizmos、inspect、存檔')

s = s.replace('| 4 | 海況傷害 | 外海用 `MapComponent_SeaWeather.BatterBuildings`；灣內不跑這個 component，只有原版天氣壓力。9×9 的箱網在 `BatterBuildings` 乘上 `stormDamageFactor = 1.5`。 |',
              '| 4 | 海況傷害 | 外海用 `MapComponent_SeaWeather.BatterBuildings`；灣內不跑這個 component，只有原版天氣壓力。9×9 的箱網在 `BatterBuildings` 從 `CompProperties_NetCage` 讀 `stormDamageFactor = 1.5`。 |')

s = s.replace('六個 WorkGiver 的 `workType` 只能是 `Handling`／`Warden`。',
              '六個 WorkGiver 的 `workType` 只能是 `Handling`／`Warden`；三個箱網 Def 都有 `CompProperties_NetCage`，且 `netDepth`／`minWaterDepth` 合法。')

io.open(p, 'w', encoding='utf-8').write(s)
print('ok')
