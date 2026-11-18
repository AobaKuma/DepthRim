using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DepthRim
{
    public enum OceanLandmarkKind
    {
        /// <summary>不生成地圖：抵達時跳出對話，看乘員技能決定收益。</summary>
        Encounter,

        /// <summary>生成一張水下地圖，乘員下艇實地探索。</summary>
        Site
    }

    /// <summary>
    /// 聲納掃到的海洋地標（[聲納地標與穿梭艇探索_機制設計.md]）。主動與被動聲納都找得到，只差範圍。
    /// 遭遇型：<see cref="description"/> 是對話開場白，<see cref="options"/> 是選項（「離開」自動附上）。
    /// 地點型：<see cref="worldObjectDef"/> 要是 <see cref="WorldObject_LandmarkSite"/>，地圖生成器寫在那個 WorldObjectDef 上。
    /// </summary>
    public class OceanLandmarkDef : Def
    {
        public OceanLandmarkKind kind = OceanLandmarkKind.Encounter;

        public WorldObjectDef worldObjectDef;

        /// <summary>出現在哪些水深層級（依那一格的海床）。</summary>
        public List<DepthTier> depthTiers = new List<DepthTier>();

        public float commonality = 1f;

        /// <summary>沒人處理時，存在幾天後消失。</summary>
        public IntRange lifetimeDays = new IntRange(8, 15);

        public ResearchProjectDef requiredResearch;

        /// <summary>地點型：地圖大小。</summary>
        public IntVec3 siteMapSize = new IntVec3(100, 1, 100);

        public List<LandmarkOption> options = new List<LandmarkOption>();

        public bool Available => requiredResearch == null || requiredResearch.IsFinished;

        public bool AllowsTier(DepthTier tier) => depthTiers.NullOrEmpty() || depthTiers.Contains(tier);

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (worldObjectDef == null)
            {
                yield return "no worldObjectDef";
            }
            if (kind == OceanLandmarkKind.Encounter && options.NullOrEmpty())
            {
                yield return "an encounter landmark needs options";
            }
            if (kind == OceanLandmarkKind.Site && worldObjectDef != null && !typeof(WorldObject_LandmarkSite).IsAssignableFrom(worldObjectDef.worldObjectClass))
            {
                yield return "a site landmark's worldObjectDef must be a WorldObject_LandmarkSite";
            }
            if (kind == OceanLandmarkKind.Encounter && worldObjectDef != null && !typeof(WorldObject_OceanLandmark).IsAssignableFrom(worldObjectDef.worldObjectClass))
            {
                yield return "an encounter landmark's worldObjectDef must be a WorldObject_OceanLandmark";
            }
            foreach (LandmarkOption o in options)
            {
                if (o.skill == null)
                {
                    yield return $"option '{o.label}' has no skill";
                }
            }
        }

        public static IEnumerable<OceanLandmarkDef> AllAvailable => DefDatabase<OceanLandmarkDef>.AllDefs.Where(d => d.Available);
    }

    /// <summary>遭遇型地標的一個選項：一項技能判定，三種結果各自的文字與收益。</summary>
    public class LandmarkOption
    {
        [MustTranslate]
        public string label;

        public SkillDef skill;

        /// <summary>技能等級等於難度時的基本成功率。</summary>
        public int difficulty = 6;

        public float baseChance = LandmarkMath.DefaultBaseChance;

        /// <summary>擔任主角的人得到的經驗值（失敗時只拿一半）。</summary>
        public float xp = 600f;

        [MustTranslate]
        public string criticalText;

        [MustTranslate]
        public string successText;

        [MustTranslate]
        public string failureText;

        /// <summary>大成功的收益；沒寫就用成功的。</summary>
        public List<LandmarkOutcome> critical = new List<LandmarkOutcome>();

        public List<LandmarkOutcome> success = new List<LandmarkOutcome>();

        public List<LandmarkOutcome> failure = new List<LandmarkOutcome>();

        public List<LandmarkOutcome> OutcomesFor(LandmarkResult result)
        {
            switch (result)
            {
                case LandmarkResult.Critical:
                    return critical.NullOrEmpty() ? success : critical;
                case LandmarkResult.Success:
                    return success;
                default:
                    return failure;
            }
        }

        public string TextFor(LandmarkResult result)
        {
            switch (result)
            {
                case LandmarkResult.Critical:
                    return criticalText.NullOrEmpty() ? successText : criticalText;
                case LandmarkResult.Success:
                    return successText;
                default:
                    return failureText;
            }
        }
    }
}
