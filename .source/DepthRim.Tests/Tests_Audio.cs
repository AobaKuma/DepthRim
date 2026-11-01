using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 水中聲音（二期 P2-11）：悶音曲線、承壓聲機率，以及音檔／SoundDef／CREDITS.md 的一致性。
static partial class Test
{
    static readonly string[] AudioExtensions = { ".ogg", ".wav", ".mp3" };

    static void AudioMath()
    {
        // 聽者規則：取聲源與聽者中較濕的一方
        Check(FloodingMath.AudioMuffle(0f, 0f) == 0f, "dry source, dry listener: no muffle");
        Check(FloodingMath.AudioMuffle(1f, 0f) == 1f, "source in water: full muffle");
        Check(FloodingMath.AudioMuffle(0f, 1f) == 1f, "listener in water hears a dry room muffled too");
        Near(FloodingMath.AudioMuffle(0.3f, 0.6f), 0.6f, "the wetter side wins");
        Check(FloodingMath.AudioMuffle(2f, -1f) == 1f, "muffle is clamped to 0–1");

        // 截止頻率端點
        Near(FloodingMath.MuffleCutoffHz(0f, DepthTier.Shallow), 22000f, "cutoff at 0 is 22 kHz (unfiltered)", 0.5f);
        Near(FloodingMath.MuffleCutoffHz(0.25f, DepthTier.Shallow), 3000f, "cutoff at 0.25 is 3 kHz", 0.5f);
        Near(FloodingMath.MuffleCutoffHz(1f, DepthTier.Shallow), 700f, "cutoff at 1 is 700 Hz", 0.5f);
        Near(FloodingMath.MuffleCutoffHz(1f, DepthTier.Deep), 560f, "deep sea lowers the cutoff by 20%", 0.5f);
        Near(FloodingMath.MuffleCutoffHz(1f, DepthTier.Seabed), 560f, "seabed lowers the cutoff by 20%", 0.5f);
        Near(FloodingMath.MuffleCutoffHz(1f, DepthTier.Thermocline), 700f, "thermocline keeps the shallow curve", 0.5f);
        Near(FloodingMath.MuffleCutoffHz(0f, DepthTier.Seabed), 22000f, "a dry sound is never filtered, even on the seabed", 0.5f);

        // 音量端點
        Near(FloodingMath.MuffleVolume(0f), 1f, "volume at 0 is 1");
        Near(FloodingMath.MuffleVolume(0.25f), 0.85f, "volume at 0.25 is 0.85");
        Near(FloodingMath.MuffleVolume(1f), 0.55f, "volume at 1 is 0.55 (water still carries sound, unlike vacuum)");

        // 單調：越濕越悶、越小聲
        bool cutoffMonotone = true, volumeMonotone = true;
        foreach (DepthTier tier in Enum.GetValues(typeof(DepthTier)))
        {
            float prevHz = float.MaxValue, prevVol = float.MaxValue;
            for (int i = 0; i <= 100; i++)
            {
                float m = i / 100f;
                float hz = FloodingMath.MuffleCutoffHz(m, tier);
                float vol = FloodingMath.MuffleVolume(m);
                cutoffMonotone &= hz <= prevHz + 1e-3f;
                volumeMonotone &= vol <= prevVol + 1e-6f;
                prevHz = hz;
                prevVol = vol;
            }
        }
        Check(cutoffMonotone, "cutoff never rises as the muffle grows (every tier)");
        Check(volumeMonotone, "volume never rises as the muffle grows");

        // 船殼承壓聲
        Check(FloodingMath.HullCreakChance(DepthTier.Shallow, 0) == 0f, "no hull creak in the shallows with an intact hull");
        Check(FloodingMath.HullCreakChance(DepthTier.Thermocline, 0) == 0f, "no hull creak in the thermocline with an intact hull");
        Near(FloodingMath.HullCreakChance(DepthTier.Deep, 0), 0.04f, "deep sea creaks 4% per update");
        Near(FloodingMath.HullCreakChance(DepthTier.Seabed, 0), 0.08f, "seabed creaks 8% per update");
        Near(FloodingMath.HullCreakChance(DepthTier.Shallow, 2), 0.04f, "each damaged wall adds 2%");
        Near(FloodingMath.HullCreakChance(DepthTier.Seabed, 3), 0.14f, "seabed plus three damaged walls");
        Near(FloodingMath.HullCreakChance(DepthTier.Seabed, 100), 0.3f, "creak chance is capped at 30%");
        Check(FloodingMath.HullCreakChance(DepthTier.Deep, -5) == 0.04f, "negative wall counts are ignored");
    }

    static void ContentSounds()
    {
        string soundsRoot = Path.Combine(ModRoot, "Sounds");
        if (!Check(Directory.Exists(soundsRoot), "Sounds folder exists")) { return; }

        // 1. SoundDef 引用的音檔或資料夾都存在
        HashSet<string> modSoundDefs = new HashSet<string>();
        int clipRefs = 0;
        foreach (string file in DefFiles)
        {
            XDocument doc = XDocument.Load(file);
            foreach (XElement def in doc.Root.Elements("SoundDef"))
            {
                string defName = def.Element("defName")?.Value;
                Check(defName != null && modSoundDefs.Add(defName), $"SoundDef {defName} has a unique defName");
                Check(defName != null && defName.StartsWith("DepthRim_"), $"SoundDef {defName} has the DepthRim_ prefix");
                foreach (XElement clip in def.Descendants("clipPath"))
                {
                    clipRefs++;
                    string path = clip.Value.Trim();
                    Check(AudioExtensions.Any(ext => File.Exists(Path.Combine(soundsRoot, path + ext))), $"{defName}: clip {path} exists under Sounds/");
                }
                foreach (XElement folder in def.Descendants("clipFolderPath"))
                {
                    clipRefs++;
                    string dir = Path.Combine(soundsRoot, folder.Value.Trim());
                    Check(Directory.Exists(dir) && Directory.GetFiles(dir).Any(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())),
                        $"{defName}: clip folder {folder.Value.Trim()} exists and has audio");
                }
            }
        }
        Check(clipRefs >= 7, $"found SoundDef clip references ({clipRefs})");

        // 2. Sounds/ 底下每個音檔都列在 CREDITS.md，CREDITS.md 列的檔案都存在
        string credits = Path.Combine(soundsRoot, "CREDITS.md");
        if (!Check(File.Exists(credits), "Sounds/CREDITS.md exists")) { return; }
        string creditsText = File.ReadAllText(credits);
        List<string> audioFiles = Directory.GetFiles(soundsRoot, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetRelativePath(soundsRoot, f).Replace('\\', '/'))
            .ToList();
        Check(audioFiles.Count >= 10, $"found mod audio files ({audioFiles.Count})");
        foreach (string rel in audioFiles)
        {
            Check(creditsText.Contains("`" + rel + "`"), $"{rel} is listed in Sounds/CREDITS.md");
        }
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(creditsText, @"`(DepthRim/[^`]+)`"))
        {
            Check(File.Exists(Path.Combine(soundsRoot, m.Groups[1].Value)), $"CREDITS.md entry {m.Groups[1].Value} exists");
        }
        foreach (string line in creditsText.Split('\n').Where(l => l.StartsWith("| `DepthRim/")))
        {
            Check(line.TrimEnd().EndsWith("CC0 |"), $"credit line is CC0: {line.Trim()}");
        }

        // 3. 四個層級環境音：各有一個 DepthTier 開關，掛在冰洋生態域上
        XDocument sounds = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "Audio", "DepthRim_Sounds.xml"));
        HashSet<DepthTier> ambientTiers = new HashSet<DepthTier>();
        List<string> ambientNames = new List<string>();
        foreach (XElement def in sounds.Root.Elements("SoundDef").Where(d => d.Element("defName").Value.StartsWith("DepthRim_Ambient_")))
        {
            ambientNames.Add(def.Element("defName").Value);
            XElement tierParam = def.Descendants("inParam").FirstOrDefault(p => (string)p.Attribute("Class") == "DepthRim.SoundParamSource_DepthTier");
            if (!Check(tierParam != null, $"{def.Element("defName").Value} is gated by SoundParamSource_DepthTier")) { continue; }
            Check(Enum.TryParse(tierParam.Element("tier")?.Value, out DepthTier tier) && ambientTiers.Add(tier), $"{def.Element("defName").Value} gates a distinct tier");
            Check(def.Descendants("onCamera").Any(e => e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)), $"{def.Element("defName").Value} is an on-camera ambience");
        }
        Check(ambientTiers.Count == 4, $"every depth tier has an ambience ({ambientTiers.Count})");
        XDocument biome = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Biomes.xml"));
        List<string> biomeSounds = biome.Descendants("soundsAmbient").Elements("li").Select(e => e.Value.Trim()).ToList();
        Check(ambientNames.All(biomeSounds.Contains), "the ice ocean biome plays all four tier ambiences");
        Check(biomeSounds.All(modSoundDefs.Contains), "every biome ambience is a DepthRim SoundDef");

        // 4. DefOf 的 SoundDef 都有定義
        foreach (System.Reflection.FieldInfo f in typeof(DepthRimDefOf).GetFields().Where(f => f.FieldType == typeof(Verse.SoundDef)))
        {
            Check(modSoundDefs.Contains(f.Name), $"DepthRimDefOf.{f.Name} has a SoundDef");
        }
    }
}

// 水密門的通行規則（FloodingMath.WatertightDoorAllows）。
static partial class Test
{
    static void WatertightDoor()
    {
        float deep = FloodingMath.SubmergedLevelStanding;
        Check(FloodingMath.WatertightDoorAllows(false, false, false, deep - 0.01f), "shallow on both sides: anyone may pass");
        Check(!FloodingMath.WatertightDoorAllows(false, false, false, deep), "flooded side: a dry colonist without air is stopped");
        Check(!FloodingMath.WatertightDoorAllows(false, false, false, 1f), "open water side: a dry colonist without air is stopped");
        Check(FloodingMath.WatertightDoorAllows(true, false, false, 1f), "drafted colonists follow orders");
        Check(FloodingMath.WatertightDoorAllows(false, true, false, 1f), "a diver with air may pass");
        Check(FloodingMath.WatertightDoorAllows(false, false, true, 1f), "someone already in the water may come back in");
        Check(FloodingMath.SubmergedLevelStanding == FloodingPawnEffects.SubmergedLevelStanding, "door threshold matches the drowning threshold");
        Check(Building_WatertightDoor.LeakFactor > 0f && Building_WatertightDoor.LeakFactor < 1f, "a damaged watertight door leaks less than a wall");
    }
}
