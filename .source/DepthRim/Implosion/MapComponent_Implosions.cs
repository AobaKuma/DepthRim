using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>某一處的水色與混濁度（<see cref="MapComponent_Implosions"/> 用來把內爆混進水裡）。</summary>
    public readonly struct MurkSample
    {
        public readonly Color color;

        public readonly float turbidity;

        public MurkSample(Color color, float turbidity)
        {
            this.color = color;
            this.turbidity = turbidity;
        }
    }

    /// <summary>一次水中內爆的播放記錄（不存檔：整段特效只有幾秒，讀檔時遺失可以接受）。</summary>
    public class ImplosionInstance
    {
        public int explosionId;

        public IntVec3 center;

        public float radius;

        public float energy;

        public DepthTier tier;

        public bool scald;

        public float screenShakeFactor = 1f;

        public SoundDef placeholderSound;

        public int startTick;

        /// <summary>超過 <see cref="ImplosionMath.MaxActive"/> 時只放簡化效果。</summary>
        public bool simplified;

        public bool collapsed;

        /// <summary>已丟出的往內吸粒子數（上限 <see cref="ImplosionMath.MaxParticles"/>）。</summary>
        public int inwardThrown;

        public int collapseTick = -1;

        public bool warpInSpawned;

        /// <summary>每個實例不同的相位與轉角（氣泡晃動、貼圖方向），弧度。</summary>
        public float seed;

        public float Period => ImplosionMath.Period(energy, tier);

        public int CollapseTicks => Mathf.Max(1, Mathf.RoundToInt(Period * GenTicks.TicksPerRealSecond));

        public int TotalTicks => Mathf.Max(CollapseTicks + 1, Mathf.RoundToInt(ImplosionMath.TotalBubbleSeconds(Period) * GenTicks.TicksPerRealSecond));

        public int ColumnTicks => Mathf.RoundToInt(ImplosionMath.ColumnSeconds(scald) * GenTicks.TicksPerRealSecond);

        /// <summary>實例結束的時間：主氣泡畫完、氣泡柱也冒完。</summary>
        public int EndTicks => Mathf.Max(TotalTicks, CollapseTicks + ColumnTicks);
    }

    /// <summary>
    /// 播放水中內爆（實作計畫 §3.2、§4）：記錄進行中的實例、推進時間軸。
    /// 開始時閃光；每格往中心吸的小氣泡（<see cref="AffectCell"/>）；主氣泡與震波環（<see cref="MapComponentUpdate"/>）；
    /// 崩塌時震動鏡頭、揚起混濁水團，之後冒一陣氣泡柱。粒子受模組設定「粒子」控制，迷霧中心不畫。
    /// 聲音暫時沿用原本的爆炸聲（經過水中悶音），步驟 6 換成內爆音效。
    /// </summary>
    public class MapComponent_Implosions : MapComponent
    {
        private readonly List<ImplosionInstance> active = new List<ImplosionInstance>();

        /// <summary>累計接手的內爆數（不存檔，給測試與除錯看）。</summary>
        public static int TotalStarted { get; private set; }

        public int ActiveCount => active.Count;

        public IReadOnlyList<ImplosionInstance> Active => active;

        public MapComponent_Implosions(Map map) : base(map)
        {
        }

        public bool IsImploding(Explosion explosion)
        {
            return InstanceOf(explosion) != null;
        }

        public ImplosionInstance InstanceOf(Explosion explosion)
        {
            if (explosion == null)
            {
                return null;
            }
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i].explosionId == explosion.thingIDNumber)
                {
                    return active[i];
                }
            }
            return null;
        }

        /// <summary>累計丟出的粒子數（不存檔，給測試看）。</summary>
        public static int TotalParticles { get; private set; }

        private static bool ParticlesOn => DepthRimModSettings.Current.particles;

        /// <summary>
        /// 中心那一格的水色與混濁度：和外海層用同一套定義（<see cref="WaterLight.At"/>、<see cref="FloodingMath.WaterAlpha"/>、
        /// 層級 Def 的 waterColor／deepColor），所以內爆看起來在同一片水裡，暗水裡也不會比水還亮。
        /// </summary>
        private MurkSample LightAt(ImplosionInstance inst)
        {
            MapComponent_Flooding flooding = map.Flooding();
            WaterVisualTierDef def = flooding != null ? WaterVisualTierDef.For(flooding.RenderTier) : null;
            float light = WaterLight.At(map, inst.center, def?.ambientReach ?? 1f);
            float turbidity = FloodingMath.WaterAlpha(light, def?.clearAlpha ?? 0.15f, def?.murkAlpha ?? 0.85f, WaterTierMaterials.LightMin, WaterTierMaterials.LightMax);
            Color deep = def?.deepColor ?? new Color(0.02f, 0.07f, 0.15f);
            Color water = def?.waterColor ?? new Color(0.06f, 0.24f, 0.40f);
            Color murk = Color.Lerp(deep, water, ImplosionMath.MurkLightness(light, WaterTierMaterials.LightMin, WaterTierMaterials.LightMax));
            return new MurkSample(murk, turbidity);
        }

        /// <summary>粒子的顏色：水深調色，再混進該處的水色與混濁度。</summary>
        private Color ParticleColor(ImplosionInstance inst, float alpha)
        {
            return ImplosionAssets.Lit(ImplosionAssets.Tint(inst.tier, inst.scald), LightAt(inst), alpha);
        }

        private bool Visible(ImplosionInstance inst)
        {
            return inst.center.InBounds(map) && !inst.center.Fogged(map);
        }

        /// <summary>
        /// 原版逐格處理爆炸時呼叫（<see cref="Patch_DamageWorker_ExplosionAffectCell"/>）：以機率丟一顆往中心吸的小氣泡，
        /// 速度算好在崩塌那一刻抵達。原版由內往外逐格延遲處理，所以粒子自然一圈圈出現、再一起被吸回去。
        /// </summary>
        public void AffectCell(Explosion explosion, IntVec3 c)
        {
            ImplosionInstance inst = InstanceOf(explosion);
            if (inst == null || !ParticlesOn || !Visible(inst) || inst.inwardThrown >= ImplosionMath.MaxParticles(inst.radius))
            {
                return;
            }
            float chance = ImplosionMath.CellParticleChance(inst.radius) * (inst.simplified ? 0.5f : 1f);
            if (!Rand.Chance(chance) || c.Fogged(map))
            {
                return;
            }
            Vector3 center = inst.center.ToVector3Shifted();
            Vector3 pos = c.ToVector3Shifted() + new Vector3(Rand.Range(-0.4f, 0.4f), 0f, Rand.Range(-0.4f, 0.4f));
            Vector3 toCenter = center - pos;
            toCenter.y = 0f;
            float age = (Find.TickManager.TicksGame - inst.startTick) / (float)GenTicks.TicksPerRealSecond;
            float travel = ImplosionMath.SecondsToCollapse(age, inst.Period);
            FleckCreationData data = FleckMaker.GetDataStatic(pos, map, DepthRimDefOf.DepthRim_ImplosionInward, Rand.Range(0.12f, 0.24f));
            data.velocity = toCenter.normalized * ImplosionMath.InwardSpeed(toCenter.magnitude, age, inst.Period);
            data.solidTimeOverride = Mathf.Max(0.02f, travel - 0.16f);
            data.rotationRate = Rand.Range(-90f, 90f);
            data.instanceColor = ParticleColor(inst, 0.5f);
            map.flecks.CreateFleck(data);
            inst.inwardThrown++;
            TotalParticles++;
        }

        public void Begin(ImplosionInstance instance)
        {
            instance.startTick = Find.TickManager.TicksGame;
            instance.seed = Rand.Range(0f, Mathf.PI * 2f);
            int full = 0;
            for (int i = 0; i < active.Count; i++)
            {
                if (!active[i].simplified)
                {
                    full++;
                }
            }
            instance.simplified = full >= ImplosionMath.MaxActive;
            active.Add(instance);
            TotalStarted++;

            if (Visible(instance))
            {
                // 0 秒的閃光：暖白的火球（參考影片 1:05）；燙傷型偏橘。閃光本身就是光源，不跟著環境光調暗
                Flash(instance, instance.radius * 2.5f, 0.75f);
                // 壓力波：原版靈能扭曲往外擴散，把水扭一下
                Warp(instance, DepthRimDefOf.DepthRim_ImplosionWarpOut, instance.radius * 0.8f);
            }
            instance.placeholderSound?.PlayOneShot(new TargetInfo(instance.center, map));
        }

        public override void MapComponentTick()
        {
            if (active.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                ImplosionInstance inst = active[i];
                int age = now - inst.startTick;
                if (!inst.warpInSpawned && age >= Mathf.RoundToInt(ImplosionMath.WarpInSeconds(inst.Period) * GenTicks.TicksPerRealSecond))
                {
                    // 氣泡塌回中心前：周圍的水被吸進去（原版靈能收縮環同一套扭曲）
                    inst.warpInSpawned = true;
                    Warp(inst, DepthRimDefOf.DepthRim_ImplosionWarpIn, ImplosionMath.MaxBubbleRadius(inst.radius, inst.tier) * 2.6f);
                }
                if (!inst.collapsed && age >= inst.CollapseTicks)
                {
                    inst.collapsed = true;
                    inst.collapseTick = now;
                    Collapse(inst);
                }
                if (inst.collapsed)
                {
                    TickColumn(inst, now);
                }
                if (age >= inst.EndTicks)
                {
                    active.RemoveAt(i);
                }
            }
        }

        private void Collapse(ImplosionInstance inst)
        {
            if (!Visible(inst))
            {
                return;
            }
            Flash(inst, inst.radius * 1.4f, 0.45f);
            if (ParticlesOn)
            {
                ThrowMurk(inst);
            }
            if (map != Find.CurrentMap || Find.CameraDriver == null)
            {
                return;
            }
            float distance = (inst.center.ToVector3Shifted() - Find.Camera.transform.position).magnitude;
            Find.CameraDriver.shaker.DoShake(ImplosionMath.Shake(inst.radius, inst.screenShakeFactor, distance));
        }

        private void Warp(ImplosionInstance inst, FleckDef def, float size)
        {
            if (!ParticlesOn || !Visible(inst) || inst.simplified)
            {
                return;
            }
            FleckCreationData data = FleckMaker.GetDataStatic(inst.center.ToVector3Shifted(), map, def, size);
            data.rotation = inst.seed * Mathf.Rad2Deg;
            map.flecks.CreateFleck(data);
        }

        private void Flash(ImplosionInstance inst, float size, float alpha)
        {
            FleckCreationData data = FleckMaker.GetDataStatic(inst.center.ToVector3Shifted(), map, DepthRimDefOf.DepthRim_ImplosionFlash, size);
            data.instanceColor = ImplosionAssets.FlashColor(inst.scald, alpha);
            map.flecks.CreateFleck(data);
        }

        /// <summary>崩塌時散出的泡沫雲：往外散、略往上漂；燙傷型是蒸氣色，海床帶泥沙。</summary>
        private void ThrowMurk(ImplosionInstance inst)
        {
            Vector3 center = inst.center.ToVector3Shifted();
            float spread = ImplosionMath.MaxBubbleRadius(inst.radius, inst.tier) * 0.7f;
            Color murk = ImplosionAssets.MurkTint(inst.tier, inst.scald);
            Color color = ImplosionAssets.Lit(murk, LightAt(inst), murk.a);
            int count = ImplosionMath.MurkCount(inst.radius);
            for (int i = 0; i < count; i++)
            {
                Vector3 offset = Gen.RandomHorizontalVector(spread);
                FleckCreationData data = FleckMaker.GetDataStatic(center + offset, map, DepthRimDefOf.DepthRim_ImplosionMurk, inst.radius * Rand.Range(0.8f, 1.3f));
                data.velocity = (offset.sqrMagnitude > 0.0001f ? offset.normalized : Vector3.forward) * Rand.Range(0.12f, 0.3f) + new Vector3(0f, 0f, Rand.Range(0.1f, 0.3f));
                data.rotationRate = Rand.Range(-15f, 15f);
                data.instanceColor = color;
                map.flecks.CreateFleck(data);
                TotalParticles++;
            }
        }

        /// <summary>崩塌後的氣泡柱：從中心附近冒出、往畫面上方漂並左右晃（§4.1）。</summary>
        private void TickColumn(ImplosionInstance inst, int now)
        {
            int since = now - inst.collapseTick;
            if (since >= inst.ColumnTicks || since % ImplosionMath.ColumnIntervalTicks(inst.scald, inst.simplified) != 0 || !ParticlesOn || !Visible(inst))
            {
                return;
            }
            float spread = ImplosionMath.MaxBubbleRadius(inst.radius, inst.tier) * 0.35f;
            Vector3 pos = inst.center.ToVector3Shifted() + Gen.RandomHorizontalVector(spread);
            FleckCreationData data = FleckMaker.GetDataStatic(pos, map, DepthRimDefOf.DepthRim_ImplosionBubble, Rand.Range(0.12f, 0.3f));
            data.velocity = new Vector3(Rand.Range(-0.3f, 0.3f), 0f, Rand.Range(0.8f, 1.6f));
            data.rotationRate = Rand.Range(-60f, 60f);
            data.instanceColor = ParticleColor(inst, 0.85f);
            map.flecks.CreateFleck(data);
            TotalParticles++;
        }

        /// <summary>
        /// 主氣泡與震波環（實作計畫 §4.2）：曲線全由 <see cref="ImplosionMath"/> 決定，這裡只換算成矩陣與顏色。
        /// 進度以遊戲 tick 計（暫停時凍結、快轉時加快），和崩塌時間點（<see cref="MapComponentTick"/>）一致。
        /// </summary>
        public override void MapComponentUpdate()
        {
            if (active.Count == 0 || map != Find.CurrentMap)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < active.Count; i++)
            {
                ImplosionInstance inst = active[i];
                if (inst.simplified || inst.center.Fogged(map))
                {
                    continue;
                }
                DrawInstance(inst, (now - inst.startTick) / (float)GenTicks.TicksPerRealSecond);
            }
        }

        private void DrawInstance(ImplosionInstance inst, float seconds)
        {
            float period = inst.Period;
            MurkSample light = LightAt(inst);
            Color tint = ImplosionAssets.Tint(inst.tier, inst.scald);
            Vector3 pos = inst.center.ToVector3Shifted();
            pos.y = AltitudeLayer.MoteOverhead.AltitudeFor();
            Quaternion turn = Quaternion.AngleAxis(inst.seed * Mathf.Rad2Deg, Vector3.up);

            // 壓力波：一圈淡淡的折射明暗線，不發光
            float ringRadius = ImplosionMath.RingRadius(seconds, period, inst.radius);
            float ringAlpha = ImplosionMath.RingAlpha(seconds, period);
            if (ringAlpha > 0.01f && ringRadius > 0.05f)
            {
                Draw(ImplosionAssets.RingMat, pos, turn, ringRadius, ringRadius, ImplosionAssets.Lit(tint, light, ringAlpha * 0.25f));
            }

            float scale = ImplosionMath.BubbleScale(seconds, period, ImplosionMath.ReboundRatio(inst.tier));
            float alpha = ImplosionMath.BubbleAlpha(seconds, period);
            if (scale > 0.01f && alpha > 0.01f)
            {
                // 崩塌瞬間反光變強一點（透明材質的顏色會被夾到 1，主要用不透明度表現）
                float rim = ImplosionMath.RimBrightness(seconds, period);
                Color c = ImplosionAssets.Lit(Color.Lerp(tint, Color.white, rim * 0.2f), light, Mathf.Clamp01(alpha * (1f + rim * 0.3f)));
                // 表面晃動：一軸拉長、另一軸壓扁；崩塌時最明顯
                float wobble = ImplosionMath.Wobble(seconds, period, inst.seed);
                // 煙霧貼圖只有中間一團是煙、本身又很淡：放大回氣泡應有的大小，疊兩層（轉不同角度）讓它夠濃
                float r = ImplosionMath.MaxBubbleRadius(inst.radius, inst.tier) * scale * ImplosionAssets.SmokeBubbleScale;
                pos.y += 0.01f;
                Draw(ImplosionAssets.BubbleMat, pos, turn, r * (1f + wobble), r * (1f - wobble), c);
                pos.y += 0.001f;
                Quaternion second = turn * Quaternion.AngleAxis(137f, Vector3.up);
                Draw(ImplosionAssets.BubbleMat, pos, second, r * 0.9f * (1f - wobble), r * 0.9f * (1f + wobble), c);
            }

            // 崩塌成泡沫團（參考影片 1:06–1:17）：貼圖的泡沫本體大約只佔貼圖半徑的 0.6，所以放大回來
            float column = ImplosionMath.ColumnSeconds(inst.scald);
            float foamScale = ImplosionMath.FoamScale(seconds, period, column);
            float foamAlpha = ImplosionMath.FoamAlpha(seconds, period, column);
            if (foamScale > 0.01f && foamAlpha > 0.01f)
            {
                float fr = ImplosionMath.MaxBubbleRadius(inst.radius, inst.tier) * foamScale / 0.6f;
                Vector3 foamPos = pos + new Vector3(0f, 0.01f, ImplosionMath.FoamRise(seconds, period));
                Color foam = ImplosionAssets.Lit(ImplosionAssets.FoamTint(inst.tier, inst.scald), light, foamAlpha);
                Draw(ImplosionAssets.FoamMat, foamPos, turn, fr, fr, foam);
            }
        }

        private static void Draw(Material mat, Vector3 pos, Quaternion rotation, float radiusX, float radiusZ, Color color)
        {
            ImplosionAssets.Block.SetColor(ShaderPropertyIDs.Color, color);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, rotation, new Vector3(radiusX * 2f, 1f, radiusZ * 2f)), mat, 0, null, 0, ImplosionAssets.Block);
        }

        public override void MapRemoved()
        {
            active.Clear();
        }
    }

    /// <summary>
    /// 內爆的材質與顏色（主執行緒載入）。貼圖是白色，顏色靠 <c>_Color</c>。
    /// renderQueue 3185：畫在兩種水層（艙室 2950、外海 3177）之上，顏色再依該處的水色與混濁度混過去（<see cref="InMurk"/>），
    /// 看起來是在水裡面。2026-11-18 試過直接排在水層下面：夜裡外海層幾乎不透明，整個內爆都看不見。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ImplosionAssets
    {
        public const int RenderQueue = 3185;
        public const float SmokeBubbleScale = 1.4f;

        public static readonly Material BubbleMat = Above(MaterialPool.MatFrom("Things/Mote/Smoke", ShaderDatabase.TransparentPostLight), "DepthRim_ImplosionBubble");

        /// <summary>壓力波：一般透明混合（不是加色），貼圖裡的暗線才看得出來。</summary>
        /// <summary>崩塌後的白色泡沫團（花椰菜狀的小泡堆）。</summary>
        public static readonly Material FoamMat = Above(MaterialPool.MatFrom("DepthRim/Implosion/Foam", ShaderDatabase.TransparentPostLight), "DepthRim_ImplosionFoam");

        public static readonly Material RingMat = Above(MaterialPool.MatFrom("DepthRim/Implosion/ShockRing", ShaderDatabase.TransparentPostLight), "DepthRim_ImplosionRing");

        private static Material Above(Material source, string name)
        {
            return new Material(source) { renderQueue = RenderQueue, name = name };
        }

        public static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();

        private static readonly Color ScaldCore = new Color(0.95f, 0.62f, 0.42f);

        /// <summary>
        /// 依水深層級調色（§4.5）：淺海偏白、溫躍層偏青、深海偏藍、海床帶泥沙；燙傷型偏橘紅。
        /// 飽和度刻意壓低：氣泡反射的是周圍的水色，不是自己有顏色。
        /// </summary>
        public static Color Tint(DepthTier tier, bool scald)
        {
            Color c;
            switch (tier)
            {
                case DepthTier.Thermocline: c = new Color(0.72f, 0.88f, 0.86f); break;
                case DepthTier.Deep: c = new Color(0.64f, 0.74f, 0.90f); break;
                case DepthTier.Seabed: c = new Color(0.80f, 0.75f, 0.64f); break;
                default: c = new Color(0.82f, 0.90f, 0.94f); break;
            }
            return scald ? Color.Lerp(c, ScaldCore, 0.45f) : c;
        }

        /// <summary>
        /// 放進水的混濁裡：顏色往該處的水色混（<see cref="ImplosionMath.MurkBlend"/>），不透明度也降一些。
        /// 不直接乘暗：白色泡沫在夜裡會變成灰色黏土，比周圍的水還髒。
        /// </summary>
        public static Color Lit(Color color, MurkSample murk, float alpha)
        {
            Color c = Color.Lerp(color, murk.color, ImplosionMath.MurkBlend(murk.turbidity));
            c.a = ImplosionMath.MurkAlpha(alpha, murk.turbidity);
            return c;
        }

        /// <summary>
        /// 崩塌時散出的泡沫雲：參考影片裡是白色的雲狀泡沫，不是暗色的混濁；海床帶泥沙、燙傷型是蒸氣。
        /// </summary>
        public static Color MurkTint(DepthTier tier, bool scald)
        {
            if (scald)
            {
                return new Color(0.93f, 0.94f, 0.95f, 0.4f);
            }
            switch (tier)
            {
                case DepthTier.Seabed: return new Color(0.72f, 0.66f, 0.54f, 0.45f);
                default: return new Color(0.84f, 0.89f, 0.9f, 0.35f);
            }
        }

        /// <summary>泡沫團：白色帶一點水色；海床混了泥沙；燙傷型是白色蒸氣。</summary>
        public static Color FoamTint(DepthTier tier, bool scald)
        {
            if (scald)
            {
                return new Color(0.96f, 0.95f, 0.93f);
            }
            return Color.Lerp(Tint(tier, false), Color.white, tier == DepthTier.Seabed ? 0.25f : 0.5f);
        }

        /// <summary>閃光：暖白（爆炸的火光），燙傷型偏橘。</summary>
        public static Color FlashColor(bool scald, float alpha)
        {
            Color c = scald ? new Color(1f, 0.72f, 0.45f) : new Color(1f, 0.93f, 0.8f);
            c.a = alpha;
            return c;
        }
    }
}
