# DepthRim 音效來源與授權

音檔來自兩個來源，檔案都只改了檔名與資料夾，內容未修改（音高、音量、濾波在 SoundDef 裡調整）：

- **OpenGameArt.org**（下載日期 2026-11-01）：授權為 **CC0（公眾領域）**，可自由使用、修改、再散布，不需要署名；這裡仍列出作者以示感謝。
- **模組作者自製**（2026-11-16 加入）：授權標為 **自製**，著作權屬於 DepthRim 模組作者，隨模組散布。
- **Pixabay**（下載日期 2026-11-15）：授權為 **Pixabay Content License**，可免費用於作品（含遊戲模組）、不需要署名；不得把音檔單獨拿出來販售或散布。

| 模組內的檔案 | 原始檔名 | 作者 | 來源頁面 | 授權 |
|---|---|---|---|---|
| `DepthRim/Ambient/UnderwaterPad.ogg` | Underwater-Ambient-Pad-isaiah658.ogg | isaiah658 | https://opengameart.org/content/underwater-ambient-pad | CC0 |
| `DepthRim/Bubbles/Bubble1.wav` | bubbles-single1.wav | Brian MacIntosh | https://opengameart.org/content/bubble-sound-effects | CC0 |
| `DepthRim/Bubbles/Bubble2.wav` | bubbles-single2.wav | Brian MacIntosh | https://opengameart.org/content/bubble-sound-effects | CC0 |
| `DepthRim/Bubbles/Bubble3.wav` | bubbles-single3.wav | Brian MacIntosh | https://opengameart.org/content/bubble-sound-effects | CC0 |
| `DepthRim/BubbleLoop/BubbleLoop.wav` | bubbles-loop1-amp.wav | Brian MacIntosh | https://opengameart.org/content/bubble-sound-effects | CC0 |
| `DepthRim/HullCreak/Creak1.ogg` | metal_sheet_01.ogg（100-CC0-wood-metal-SFX.zip） | rubberduck | https://opengameart.org/content/100-cc0-metal-and-wood-sfx | CC0 |
| `DepthRim/HullCreak/Creak2.ogg` | metal_sheet_02.ogg（同上） | rubberduck | https://opengameart.org/content/100-cc0-metal-and-wood-sfx | CC0 |
| `DepthRim/HullCreak/Creak3.ogg` | metal_sheet_03.ogg（同上） | rubberduck | https://opengameart.org/content/100-cc0-metal-and-wood-sfx | CC0 |
| `DepthRim/HullCreak/Creak4.ogg` | metal_sheet_04.ogg（同上） | rubberduck | https://opengameart.org/content/100-cc0-metal-and-wood-sfx | CC0 |
| `DepthRim/FloodLoop/FloodLoop.ogg` | loop_water_01.ogg（water-splash-slime-sfx.zip） | rubberduck | https://opengameart.org/content/40-cc0-water-splash-slime-sfx | CC0 |
| `DepthRim/PumpLoop/PumpLoop.ogg` | loop_water_02.ogg（同上） | rubberduck | https://opengameart.org/content/40-cc0-water-splash-slime-sfx | CC0 |
| `DepthRim/Breach/Breach1.ogg` | splash_12.ogg（同上） | rubberduck | https://opengameart.org/content/40-cc0-water-splash-slime-sfx | CC0 |
| `DepthRim/Breach/Breach2.ogg` | splash_13.ogg（同上） | rubberduck | https://opengameart.org/content/40-cc0-water-splash-slime-sfx | CC0 |
| `DepthRim/Breach/Breach3.ogg` | Transform_Water.wav（2026-11-16 轉成 ogg） | DepthRim 模組作者 | 原創（模組作者自製） | 自製 |
| `DepthRim/Breach/Breach4.ogg` | Transform_Water_2 01.wav（2026-11-16 轉成 ogg） | DepthRim 模組作者 | 原創（模組作者自製） | 自製 |
| `DepthRim/Breach/Breach5.ogg` | Transform_Water_2 02.wav（2026-11-16 轉成 ogg） | DepthRim 模組作者 | 原創（模組作者自製） | 自製 |
| `DepthRim/HullCreak/Creak5.ogg` | Scale_Metal 01.wav（2026-11-16 轉成 ogg） | DepthRim 模組作者 | 原創（模組作者自製） | 自製 |
| `DepthRim/HullCreak/Creak6.ogg` | Scale_Metal 02.wav（2026-11-16 轉成 ogg） | DepthRim 模組作者 | 原創（模組作者自製） | 自製 |
| `DepthRim/PressureHiss/AirHiss.ogg` | Merge_Air.wav（2026-11-16 轉成 ogg） | DepthRim 模組作者 | 原創（模組作者自製） | 自製 |
| `DepthRim/EngineRoom/EngineRoom.mp3` | freesound_community-g45-24-submarine-interior-engine-room-59300.mp3 | freesound_community | https://pixabay.com/sound-effects/g45-24-submarine-interior-engine-room-59300/ | Pixabay Content License |
| `DepthRim/HullGroan/HullGroan.mp3` | freesound_community-027558_stereotypical-sinking-submarine-sound-no-reverbaif-74671.mp3 | freesound_community | https://pixabay.com/sound-effects/027558-stereotypical-sinking-submarine-sound-no-reverbaif-74671/ | Pixabay Content License |
| `DepthRim/ShipHold/ShipHold.mp3` | soundreality-ship-hold-305930.mp3 | SoundReality | https://pixabay.com/sound-effects/ship-hold-305930/ | Pixabay Content License |

**規則：** 沒有列在這張表裡的音檔不得放進 `Sounds/`。授權只接受 CC0、Pixabay Content License 或自製。單元測試 `ContentSounds` 會檢查兩者一致與授權欄。
