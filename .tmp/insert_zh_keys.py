# -*- coding: utf-8 -*-
"""Insert the ocean-current-turbine Keyed entries into the Traditional Chinese Keyed file (CRLF preserved)."""
import io
import os

path = os.path.join("1.7", "Languages", "ChineseTraditional (繁體中文)", "Keyed", "DepthRim.xml")
block = """  <!-- 洋流發電機與水下風力發電限制（1.7/Defs/Buildings/OceanCurrentTurbine.xml、.source/DepthRim/Power） -->
  <!-- EN: Cannot generate power: this is not an underwater map. -->
  <DepthRim.CurrentTurbine.NotUnderwater>無法發電：這裡不是水下地圖。</DepthRim.CurrentTurbine.NotUnderwater>
  <!-- EN: Cannot generate power: the turbine is not completely under water. -->
  <DepthRim.CurrentTurbine.NotSubmerged>無法發電：整台發電機沒有完全浸在水裡。</DepthRim.CurrentTurbine.NotSubmerged>
  <!-- EN: Ocean current: {0} -->
  <DepthRim.CurrentTurbine.Current>洋流：{0}</DepthRim.CurrentTurbine.Current>
  <!-- EN: Current obstructed in {0} cells: {1}% less output. -->
  <DepthRim.CurrentTurbine.Blocked>水流被擋住 {0} 格：輸出減少 {1}%。</DepthRim.CurrentTurbine.Blocked>
  <!-- EN: This placement blocks the current in {0} cells: {1}% less output. -->
  <DepthRim.CurrentTurbine.GhostBlocked>這個位置會擋住 {0} 格水流：輸出減少 {1}%。</DepthRim.CurrentTurbine.GhostBlocked>
  <!-- EN: Must be built on the sea floor, under water. -->
  <DepthRim.CurrentTurbine.MustBeUnderwater>只能蓋在水下的海床上。</DepthRim.CurrentTurbine.MustBeUnderwater>
  <!-- EN: The whole turbine has to be under water. -->
  <DepthRim.CurrentTurbine.MustBeSubmerged>整台發電機都要在水面下。</DepthRim.CurrentTurbine.MustBeSubmerged>
  <!-- EN: Cannot be built under water: there is no wind down here. -->
  <DepthRim.WindTurbine.NoWindUnderwater>水下不能蓋：這裡沒有風。</DepthRim.WindTurbine.NoWindUnderwater>
  <!-- EN: No wind under water: this turbine cannot generate power here. -->
  <DepthRim.WindTurbine.NoWindUnderwaterInspect>水下沒有風：這台風力發電機在這裡無法發電。</DepthRim.WindTurbine.NoWindUnderwaterInspect>

"""

text = io.open(path, "r", encoding="utf-8-sig", newline="").read()
marker = "</LanguageData>"
assert text.count(marker) == 1, "expected exactly one closing tag"
assert "CurrentTurbine.NotUnderwater" not in text, "already inserted"
text = text.replace(marker, block.replace("\n", "\r\n") + marker)
io.open(path, "w", encoding="utf-8-sig", newline="").write(text)
print("updated", path)
