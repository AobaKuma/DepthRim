using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;
using UnityEngine;

// 中層水域（地形下方經過的生物）：純函式與內容檢查。
static partial class Test
{
    static void MidwaterMathTests()
    {
        Check(!MidwaterMath.ShowsMidwater(DepthTier.Shallow), "shallows show the seabed, not the midwater");
        Check(MidwaterMath.ShowsMidwater(DepthTier.Thermocline), "thermocline shows the midwater");
        Check(MidwaterMath.ShowsMidwater(DepthTier.Deep), "deep shows the midwater");
        Check(!MidwaterMath.ShowsMidwater(DepthTier.Seabed), "seabed (anchorable) does not show the midwater");

        // 穿越路線：起點與終點都在地圖外（含邊距），而且路線真的穿過地圖
        const int W = 250, H = 200;
        const float margin = 30f;
        for (int i = 0; i < 16; i++)
        {
            float angle = i * Mathf.PI / 8f + 0.1f;
            foreach (float lateral in new[] { -1f, 0f, 1f })
            {
                MidwaterMath.CrossingPath(W, H, angle, lateral, margin, out Vector2 from, out Vector2 to);
                string tag = $"angle {angle:0.00} lateral {lateral}";
                Check(OutsideBy(from, W, H, margin - 0.01f), "start is outside the map plus margin, " + tag);
                Check(OutsideBy(to, W, H, margin - 0.01f), "end is outside the map plus margin, " + tag);
                Vector2 dir = (to - from).normalized;
                Check(Vector2.Dot(dir, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle))) > 0.999f, "travels along the requested heading, " + tag);
                Vector2 mid = (from + to) * 0.5f;
                Check(mid.x > 0 && mid.x < W && mid.y > 0 && mid.y < H, $"the middle of the path is on the map ({mid}), " + tag);
            }
        }

        Near(MidwaterMath.Envelope(0f), 0f, "invisible at the start");
        Near(MidwaterMath.Envelope(1f), 0f, "invisible at the end");
        Near(MidwaterMath.Envelope(0.5f), 1f, "fully visible mid-way");
        Check(MidwaterMath.Envelope(0.05f) > 0f && MidwaterMath.Envelope(0.05f) < 1f, "fades in");
        Check(MidwaterMath.Envelope(0.03f) < MidwaterMath.Envelope(0.08f), "fade-in rises");
        Near(MidwaterMath.Envelope(0.3f), MidwaterMath.Envelope(0.7f), "fade is symmetric");

        Check(MidwaterMath.AmbientTarget(0, 900, 5) == 0, "no abyss, no ambient creatures");
        Check(MidwaterMath.AmbientTarget(899, 900, 5) == 0, "a small hole stays empty");
        Check(MidwaterMath.AmbientTarget(2000, 900, 5) == 2, "one creature per 900 abyss cells");
        Check(MidwaterMath.AmbientTarget(100000, 900, 5) == 5, "capped");

        Check(MidwaterMath.TravelTicks(120f, 2f) == 3600, "120 cells at 2 cells/s is 60 s = 3600 ticks");
        Check(MidwaterMath.TravelTicks(10f, 0f) == 1, "zero speed never divides by zero");

        Vector2 cam = new Vector2(30, 20);
        Vector2 q = MidwaterMath.ProjectDepth(new Vector2(30, 20), cam, 0.4f);
        Near(q.x, 30f, "a creature right under the camera stays there");
        Vector2 r = MidwaterMath.ProjectDepth(new Vector2(50, 20), cam, 0.25f);
        Near(r.x, 45f, "deeper creatures pull towards the camera (perspective)");
        Near(MidwaterMath.ProjectDepth(new Vector2(50, 20), cam, 0f).x, 50f, "depth 0 lies on the floor");
        Near(MidwaterMath.DepthScale(0.25f), 0.75f, "size shrinks with depth");
        Check(MidwaterMath.DepthScale(5f) > 0f, "depth is clamped so nothing shrinks to nothing");
        // 鏡頭在地圖角落時，經過鏡頭下方的生物也要在鏡頭下方（舊的「依地圖中心平移」會把牠推走）
        Vector2 corner = new Vector2(20, 15);
        Check(Vector2.Distance(MidwaterMath.ProjectDepth(corner, corner, 0.35f), corner) < 0.01f, "camera in a corner still sees what passes beneath it");
    }

    static bool OutsideBy(Vector2 p, int w, int h, float margin)
        => p.x <= -margin || p.y <= -margin || p.x >= w + margin || p.y >= h + margin;

    static void ContentMidwater()
    {
        string defsPath = Path.Combine(ModRoot, "1.7", "Defs", "Midwater", "Midwater.xml");
        XDocument doc = XDocument.Load(defsPath);
        var creatures = doc.Root.Elements("DepthRim.MidwaterCreatureDef").ToList();
        Check(creatures.Count >= 4, $"midwater creatures defined ({creatures.Count})");
        foreach (XElement c in creatures)
        {
            string name = (string)c.Element("defName");
            string tex = (string)c.Element("texPath");
            Check(File.Exists(Path.Combine(ModRoot, "Textures", tex.Replace('/', Path.DirectorySeparatorChar) + ".png")), $"{name} texture exists ({tex})");
            var tiers = c.Element("tiers")?.Elements("li").Select(e => (DepthTier)Enum.Parse(typeof(DepthTier), e.Value)).ToList();
            Check(tiers != null && tiers.Count > 0 && tiers.All(MidwaterMath.ShowsMidwater), $"{name} only lives where the midwater is drawn");
            bool giant = (string)c.Element("giant") == "true";
            float weight = c.Element("ambientWeight") != null ? float.Parse((string)c.Element("ambientWeight")) : 0f;
            Check(giant != (weight > 0f), $"{name} is either a giant or an ambient creature");
            if (giant)
            {
                Check(!string.IsNullOrEmpty((string)c.Element("passMessage")), $"{name} (a giant) has a pass message");
            }
        }
        foreach (DepthTier tier in new[] { DepthTier.Thermocline, DepthTier.Deep })
        {
            bool hasGiant = creatures.Any(c => (string)c.Element("giant") == "true"
                && c.Element("tiers").Elements("li").Any(e => e.Value == tier.ToString()));
            Check(hasGiant, $"{tier} has a giant for the event");
            bool hasAmbient = creatures.Any(c => c.Element("ambientWeight") != null
                && c.Element("tiers").Elements("li").Any(e => e.Value == tier.ToString()));
            Check(hasAmbient, $"{tier} has ambient creatures");
        }

        // 著色器：自訂頂點著色器把世界座標傳給片段著色器（location 2），片段著色器用它查遮罩
        string shaders = Path.Combine(ModRoot, "1.7", "Shaders");
        string vert = File.ReadAllText(Path.Combine(shaders, "DepthRim_Midwater.vert.glsl"));
        string frag = File.ReadAllText(Path.Combine(shaders, "DepthRim_Midwater.frag.glsl"));
        Check(vert.Contains("layout(location = 2) out vec2 vWorld"), "vertex shader outputs world XZ");
        Check(frag.Contains("layout(location = 2) in vec2 vWorld"), "fragment shader reads world XZ");
        Check(frag.Contains("_MaskTex") && frag.Contains("discard"), "fragment shader clips to the abyss mask");
        XElement shaderDef = doc.Root.Elements("ShaderDef").Single(e => (string)e.Element("defName") == "DepthRim_Midwater");
        int queue = int.Parse((string)shaderDef.Element("shaderProgram").Element("renderQueue"));
        Check(queue > 3177 && queue < 3180, $"drawn over the outer water (3177) and under the darkness overlay (3180) ({queue})");

        // 事件只給海洋地圖：海洋地點帶自己的標籤，事件也只認這個標籤
        XDocument site = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "World", "Ocean_Site.xml"));
        XElement oceanSite = site.Root.Elements("WorldObjectDef").Single(e => (string)e.Element("defName") == "DepthRim_OceanSite");
        Check(oceanSite.Element("IncidentTargetTags")?.Elements("li").Any(e => e.Value == "DepthRim_OceanMap") == true, "ocean site carries the ocean-map incident tag");
        XElement incident = doc.Root.Elements("IncidentDef").Single(e => (string)e.Element("defName") == "DepthRim_GiantPasses");
        Check(incident.Element("targetTags").Elements("li").Select(e => e.Value).SequenceEqual(new[] { "DepthRim_OceanMap" }), "the giant event only targets ocean maps");
        Check((string)incident.Element("baseChance") == "0", "the vanilla storyteller never rolls it (the map component does)");
    }
}
