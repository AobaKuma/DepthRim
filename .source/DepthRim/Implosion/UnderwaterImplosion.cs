using HarmonyLib;
using RimWorld;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 掛在 DamageDef 上，調整水中內爆的行為（實作計畫 §2.2）：
    /// <list type="bullet">
    /// <item><c>exempt</c>：在水中照原版爆炸（EMP、煙霧、眩暈、消防泡沫，見 Patches/Implosion_DamageDefs.xml）。</item>
    /// <item><c>keepDamageType</c>：火焰類在水中也不改成燙傷。</item>
    /// <item><c>scald</c>：這個 DamageDef 就是燙傷（只掛在 DepthRim_Scald），視覺用燙傷版。</item>
    /// </list>
    /// </summary>
    public class UnderwaterImplosionExtension : DefModExtension
    {
        public bool exempt;

        public bool keepDamageType;

        public bool scald;
    }

    /// <summary>
    /// 水中爆炸改成內爆（實作計畫 Docs/水中內爆_實作計畫.md）：判定、接手。
    /// 接手時關掉原版的視覺與音效（<see cref="Explosion.doVisualEffects"/>、<see cref="Explosion.doSoundEffects"/>）、
    /// 不起火、火焰類改成燙傷；傷害量、半徑、熱量、附帶氣體與破艙進水都照原版。
    /// </summary>
    public static class UnderwaterImplosion
    {
        /// <summary>這張地圖這一格的爆炸要不要接手。</summary>
        public static bool ShouldImplode(Map map, IntVec3 center, DamageDef damType, bool callerWantsVisuals)
        {
            if (map == null || damType == null || !center.InBounds(map))
            {
                return false;
            }
            MapComponent_Flooding flooding = map.Flooding();
            if (flooding == null)
            {
                return false;
            }
            bool exempt = damType.GetModExtension<UnderwaterImplosionExtension>()?.exempt ?? false;
            bool hasWater = flooding.HasWater;
            return ImplosionMath.ShouldImplode(exempt, callerWantsVisuals, hasWater, hasWater && flooding.IsOpenAt(center), hasWater ? flooding.LevelAt(center) : 0f);
        }

        public static bool IsScald(DamageDef def)
        {
            return def?.GetModExtension<UnderwaterImplosionExtension>()?.scald ?? false;
        }

        /// <summary>火焰類爆炸在水中要不要改成燙傷（§3.4）。</summary>
        public static bool ConvertsToScald(DamageDef def)
        {
            if (def == null)
            {
                return false;
            }
            UnderwaterImplosionExtension ext = def.GetModExtension<UnderwaterImplosionExtension>();
            bool flameWorker = def.workerClass != null && typeof(DamageWorker_Flame).IsAssignableFrom(def.workerClass);
            return ImplosionMath.ConvertsToScald(def == DamageDefOf.Flame, flameWorker, ext?.keepDamageType ?? false, ext?.scald ?? false);
        }

        /// <summary>
        /// 接手一個剛要開始的爆炸：改欄位並回傳記錄（還沒開始播）。不接手時回傳 null。
        /// 必須在 <c>StartExplosion</c> 之前呼叫：之後它馬上用 <c>damType</c> 算受影響格。
        /// </summary>
        public static ImplosionInstance Claim(Explosion explosion, SoundDef explosionSound)
        {
            if (explosion == null || !explosion.Spawned || !ShouldImplode(explosion.Map, explosion.Position, explosion.damType, explosion.doVisualEffects))
            {
                return null;
            }
            bool wantedSound = explosion.doSoundEffects;
            explosion.doVisualEffects = false;
            explosion.doSoundEffects = false;
            explosion.chanceToStartFire = 0f;
            if (ConvertsToScald(explosion.damType))
            {
                explosion.damType = DepthRimDefOf.DepthRim_Scald;
            }
            MapComponent_Flooding flooding = explosion.Map.Flooding();
            return new ImplosionInstance
            {
                explosionId = explosion.thingIDNumber,
                center = explosion.Position,
                radius = explosion.radius,
                energy = ImplosionMath.Energy(explosion.damAmount, explosion.radius),
                // 視覺層級：平常等於實際層級；除錯選單「Override water visual tier」可以逐層預覽（只影響畫面）
                tier = flooding.RenderTier,
                scald = IsScald(explosion.damType),
                screenShakeFactor = explosion.screenShakeFactor,
                // 暫代音效（步驟 6 換成內爆聲）：呼叫端指定的聲音，否則傷害類型的爆炸聲；經過 UnderwaterAudio 悶音
                placeholderSound = wantedSound ? (explosionSound.NullOrUndefined() ? explosion.damType.soundExplosion : explosionSound) : null,
            };
        }
    }

    /// <summary>
    /// 攔截點（§3.1）：掛在 <see cref="Explosion.StartExplosion"/>，所有爆炸（含 <c>GenExplosion.DoExplosion</c> 與 DMSE 魚雷）都會經過。
    /// Prefix 改欄位，不跳過原方法：傷害、熱量、Notify_Explosion 都照原版跑。
    /// </summary>
    [HarmonyPatch(typeof(Explosion), nameof(Explosion.StartExplosion))]
    public static class Patch_Explosion_StartExplosion
    {
        [HarmonyPrefix]
        public static void Prefix(Explosion __instance, SoundDef explosionSound, out ImplosionInstance __state)
        {
            __state = UnderwaterImplosion.Claim(__instance, explosionSound);
        }

        [HarmonyPostfix]
        public static void Postfix(Explosion __instance, ImplosionInstance __state)
        {
            if (__state != null && __instance.Spawned)
            {
                __instance.Map.GetComponent<MapComponent_Implosions>()?.Begin(__state);
            }
        }
    }

    /// <summary>
    /// 每格效果（§3.3）：原版每格的火光被 <c>doVisualEffects = false</c> 關掉了，改丟往中心吸的小氣泡。
    /// 掛在基底類別；Flame、Vaporize 的覆寫都會先呼叫基底，燙傷用的 AddInjury 沒有覆寫。
    /// </summary>
    [HarmonyPatch(typeof(DamageWorker), nameof(DamageWorker.ExplosionAffectCell))]
    public static class Patch_DamageWorker_ExplosionAffectCell
    {
        [HarmonyPostfix]
        public static void Postfix(Explosion explosion, IntVec3 c, bool canThrowMotes)
        {
            if (!canThrowMotes || explosion == null || explosion.doVisualEffects || !explosion.Spawned)
            {
                return;
            }
            explosion.Map.GetComponent<MapComponent_Implosions>()?.AffectCell(explosion, c);
        }
    }
}
