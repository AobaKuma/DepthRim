using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>一隻正在從深淵底下經過的生物：沿直線從 <see cref="from"/> 游到 <see cref="to"/>，走完就消失。</summary>
    public class MidwaterCreature : IExposable
    {
        public MidwaterCreatureDef def;

        public Vector2 from;

        public Vector2 to;

        public int durationTicks = 1;

        public int ageTicks;

        /// <summary>每隻的大小倍率（同種生物不會一模一樣大）。</summary>
        public float scale = 1f;

        /// <summary>著色器擺動的相位，讓同時出現的幾隻不會同步擺尾。</summary>
        public float phase;

        public float Progress => Mathf.Clamp01((float)ageTicks / durationTicks);

        public bool Finished => ageTicks >= durationTicks;

        public Vector2 Position => Vector2.Lerp(from, to, Progress);

        /// <summary>前進方向（弧度）。</summary>
        public float Heading => Mathf.Atan2(to.y - from.y, to.x - from.x);

        public float Alpha => MidwaterMath.Envelope(Progress);

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref from, "from");
            Scribe_Values.Look(ref to, "to");
            Scribe_Values.Look(ref durationTicks, "durationTicks", 1);
            Scribe_Values.Look(ref ageTicks, "ageTicks");
            Scribe_Values.Look(ref scale, "scale", 1f);
            Scribe_Values.Look(ref phase, "phase");
        }
    }
}
