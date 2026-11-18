using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 需要定時結算的海洋基因（基因設計稿 §2、§5、§6.1）：每 <see cref="Interval"/> ticks 看一次
    /// 鰓乾燥（<see cref="GeneExtension_DepthRim.gills"/>）、強烈畏光的眩光（photophobia 2）、抗減壓病（barotraumaRecoveryFactor）。
    /// 基因本身只是開關，數值都在 <see cref="MarineGeneMath"/>。
    /// </summary>
    public class Gene_DepthRim : Gene
    {
        public const int Interval = 250;

        private int pending;

        public GeneExtension_DepthRim Ext => def.GetModExtension<GeneExtension_DepthRim>();

        public override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            pending += delta;
            if (pending < Interval)
            {
                return;
            }
            int ticks = pending;
            pending = 0;
            if (pawn == null || pawn.Dead || !Active)
            {
                return;
            }
            GeneExtension_DepthRim ext = Ext;
            if (ext == null)
            {
                return;
            }
            if (ext.gills)
            {
                UpdateGills(ticks);
            }
            if (ext.photophobia >= 2)
            {
                UpdateDazzle();
            }
            if (ext.barotraumaRecoveryFactor > 1f)
            {
                UpdateBarotrauma(ext.barotraumaRecoveryFactor, ticks);
            }
        }

        private void UpdateGills(int ticks)
        {
            Hediff drying = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_GillDrying);
            float before = drying?.Severity ?? 0f;
            float after = MarineGeneMath.GillDrying(before, GillsWet(pawn), pawn.AmbientTemperature, ticks);
            if (after <= 0f)
            {
                if (drying != null)
                {
                    pawn.health.RemoveHediff(drying);
                }
                return;
            }
            if (drying == null)
            {
                drying = pawn.health.AddHediff(DepthRimDefOf.DepthRim_GillDrying);
            }
            drying.Severity = after;
        }

        /// <summary>鰓濕著：泡在及腰以上的水裡，或站在露天的雨中。</summary>
        public static bool GillsWet(Pawn pawn)
        {
            Hediff wading = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Wading);
            if (wading != null && wading.Severity >= MarineGeneMath.GillWetLevel)
            {
                return true;
            }
            Map map = pawn.MapHeld;
            if (map == null || !pawn.Spawned)
            {
                return false;
            }
            return map.weatherManager.RainRate > 0.1f && !map.roofGrid.Roofed(pawn.Position);
        }

        private void UpdateDazzle()
        {
            Hediff dazzled = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Dazzled);
            bool glaring = pawn.Spawned && MarineGeneMath.Glaring(2, pawn.Map.glowGrid.GroundGlowAt(pawn.Position));
            if (glaring && dazzled == null)
            {
                pawn.health.AddHediff(DepthRimDefOf.DepthRim_Dazzled);
            }
            else if (!glaring && dazzled != null)
            {
                pawn.health.RemoveHediff(dazzled);
            }
        }

        /// <summary>水壓傷害 HediffDef 本身每天消退 1.2；抗減壓病再多消退 (倍率 − 1) 倍。</summary>
        public const float BarotraumaBaseRecoveryPerDay = 1.2f;

        private void UpdateBarotrauma(float factor, int ticks)
        {
            Hediff baro = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Barotrauma);
            if (baro == null)
            {
                return;
            }
            baro.Severity -= BarotraumaBaseRecoveryPerDay * (factor - 1f) * ticks / 60000f;
        }

        public override void PostRemove()
        {
            base.PostRemove();
            DepthRimGenes.Invalidate(pawn);
            if (pawn?.health == null)
            {
                return;
            }
            GeneExtension_DepthRim ext = Ext;
            if (ext != null && ext.gills)
            {
                Hediff drying = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_GillDrying);
                if (drying != null)
                {
                    pawn.health.RemoveHediff(drying);
                }
            }
            if (ext != null && ext.photophobia >= 2)
            {
                Hediff dazzled = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Dazzled);
                if (dazzled != null)
                {
                    pawn.health.RemoveHediff(dazzled);
                }
            }
        }

        public override void PostAdd()
        {
            base.PostAdd();
            DepthRimGenes.Invalidate(pawn);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref pending, "depthRimPending", 0);
        }
    }

    /// <summary>
    /// 生物發光（基因設計稿 §6.1）：在水下地圖上、或夜裡，腳下註冊一個半徑 2 的藍綠微光（和潛水服頭燈同一套原版自訂光源）。
    /// 光源是執行期狀態，不存檔；角色離開地圖時由 <see cref="Patch_PawnDeSpawnGlow"/> 解除。
    /// </summary>
    public class Gene_Bioluminescent : Gene_DepthRim
    {
        public const float GlowRadius = 2f;

        public static readonly Color GlowColor = new Color(70f / 255f, 200f / 255f, 190f / 255f);

        /// <summary>夜裡（天光低於這個值）才發光；水下地圖一律發光。</summary>
        public const float NightSkyGlow = 0.3f;

        private const int GlowInterval = 30;

        private bool lit;

        private int handle;

        private Map glowMap;

        private IntVec3 glowCell;

        private int glowPending;

        public bool Lit => lit;

        public override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            glowPending += delta;
            if (glowPending >= GlowInterval)
            {
                glowPending = 0;
                UpdateGlow();
            }
        }

        public void UpdateGlow()
        {
            if (!WantGlow())
            {
                TurnOff();
                return;
            }
            if (lit && glowMap == pawn.Map && glowCell == pawn.Position)
            {
                return;
            }
            TurnOff();
            glowMap = pawn.Map;
            glowCell = pawn.Position;
            handle = glowMap.glowGrid.RegisterCustomGlower(glowCell, GlowRadius, new ColorInt(GlowColor), 0f);
            lit = true;
        }

        private bool WantGlow()
        {
            if (pawn == null || pawn.Dead || !pawn.Spawned || !Active)
            {
                return false;
            }
            Map map = pawn.Map;
            if (FloodingUtility.IsWaterMap(map) || VoyageView.IsVoyageMap(map))
            {
                return true;
            }
            return map.skyManager.CurSkyGlow < NightSkyGlow;
        }

        public void TurnOff()
        {
            if (lit && glowMap != null && !glowMap.Disposed)
            {
                glowMap.glowGrid.DeregisterCustomGlower(handle);
            }
            lit = false;
            glowMap = null;
            handle = 0;
        }

        public override void PostRemove()
        {
            TurnOff();
            base.PostRemove();
        }

        public override void Notify_PawnDied(DamageInfo? dinfo, Hediff culprit = null)
        {
            TurnOff();
            base.Notify_PawnDied(dinfo, culprit);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                lit = false;
                glowMap = null;
                handle = 0;
            }
        }
    }

    /// <summary>角色離開地圖（上運輸艇、進商隊、地圖卸除）時關掉生物發光，不留下幽靈光源。</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    public static class Patch_PawnDeSpawnGlow
    {
        public static void Prefix(Pawn __instance)
        {
            if (__instance.genes == null)
            {
                return;
            }
            var genes = __instance.genes.GenesListForReading;
            for (int i = 0; i < genes.Count; i++)
            {
                if (genes[i] is Gene_Bioluminescent glow)
                {
                    glow.TurnOff();
                }
            }
        }
    }
}
