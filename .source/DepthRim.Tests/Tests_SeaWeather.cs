using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;
using Verse;

// 海面的惡劣海況（2026-11-17）：天氣轉換時的海況內插、兩種天氣的 XML、原版 Ocean 生態區的天氣表補丁、著色器參數。
static partial class Test
{
    static void SeaWeather()
    {
        // ── 內插 ──
        SeaStateValues calm = SeaStateValues.Calm;
        SeaStateValues storm = new SeaStateValues { swayFloor = 2.5f, roughness = 1f, itemSweepMtbHours = 3f, buildingHitMtbHours = 3f, buildingDamage = new FloatRange(6f, 15f) };
        SeaStateValues half = SeaStateValues.Lerp(calm, storm, 0.5f);
        Near(half.swayFloor, 1.75f, "half way: sway floor in between");
        Near(half.roughness, 0.5f, "half way: half as rough");
        Near(half.itemSweepMtbHours, 6f, "half way from never: items swept half as often (rate interpolated)");
        Near(half.buildingDamage.max, 7.5f, "half way: damage in between");
        Near(SeaStateValues.Lerp(calm, storm, 0f).itemSweepMtbHours, 0f, "fully calm: never");
        Near(SeaStateValues.Lerp(calm, storm, 1f).itemSweepMtbHours, 3f, "fully stormy: the storm's interval");
        Near(SeaStateValues.Lerp(calm, storm, 2f).roughness, 1f, "t is clamped");
        Near(SeaStateValues.LerpMtb(10f, 3f, 0.5f), 1f / ((0.1f + 1f / 3f) / 2f), "between two intervals the rate is averaged");
        Near(SeaStateValues.From(null).swayFloor, 1f, "a weather without the extension is calm");

        // ── 著色器參數 ──
        Near(MapComponent_SeaSurface.RoughParams(0f).x, 0f, "calm: no whitecaps");
        Check(MapComponent_SeaSurface.RoughParams(1f).x >= 0.99f && MapComponent_SeaSurface.RoughParams(1f).y > 0.3f, "storm: whitecaps and a darker sea");
        string shader = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Shaders", "DepthRim_SeaSurface.frag.glsl"));
        Check(shader.Contains("vec4 _Rough;") && shader.Contains("u._Rough.x") && shader.Contains("u._Rough.y") && shader.Contains("u._Rough.z"), "the sea shader declares and uses _Rough");

        // ── 天氣 XML ──
        XElement[] weathers = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements("WeatherDef")).ToArray();
        XElement rough = weathers.FirstOrDefault(w => w.Element("defName")?.Value == "DepthRim_RoughSea");
        XElement stormDef = weathers.FirstOrDefault(w => w.Element("defName")?.Value == "DepthRim_RoughSeaStorm");
        Check(rough != null && stormDef != null, "both rough-sea weathers are defined");
        if (rough != null && stormDef != null)
        {
            float F(XElement w, string field) => float.Parse(w.Element("modExtensions").Elements("li").First(li => (string)li.Attribute("Class") == "DepthRim.SeaStateExtension").Element(field).Value, CultureInfo.InvariantCulture);
            Check(F(rough, "swayFloor") >= RopeTetherMath.RoughSeaFactor && F(stormDef, "swayFloor") >= RopeTetherMath.RoughSeaFactor, "both are rough enough to count as an overboard hazard");
            Check(F(stormDef, "swayFloor") > F(rough, "swayFloor") && F(stormDef, "roughness") > F(rough, "roughness"), "the storm is rougher");
            Check(F(stormDef, "itemSweepMtbHours") < F(rough, "itemSweepMtbHours") && F(stormDef, "buildingHitMtbHours") < F(rough, "buildingHitMtbHours"), "the storm sweeps and batters more often");
            Check(stormDef.Element("rainRate") != null && stormDef.Element("eventMakers") != null && rough.Element("rainRate") == null, "only the storm has rain and lightning");
        }

        // ── 原版 Ocean 生態區的天氣表 ──
        string patch = File.ReadAllText(Path.Combine(ModRoot, "1.7", "Patches", "SeaSurface_Weather.xml"));
        Check(patch.Contains("BiomeDef[defName=\"Ocean\"]") && patch.Contains("<DepthRim_RoughSea>") && patch.Contains("<DepthRim_RoughSeaStorm>") && patch.Contains("<Clear>"), "the Ocean biome gets a weather table with both rough-sea weathers");
    }
}
