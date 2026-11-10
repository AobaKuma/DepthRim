using UnityEngine;

namespace DepthRim
{
    /// <summary>
    /// 中層水域（無法下錨的變溫層與深海）的純函式：哪些層級畫得出地形下方的生物、穿越地圖的路線、淡入淡出與視差。
    /// 不碰遊戲物件，可單元測試。
    /// </summary>
    public static class MidwaterMath
    {
        /// <summary>穿越路線的起點與終點離地圖邊緣多遠（格）：在畫面外出現、在畫面外消失。</summary>
        public const float PathMargin = 30f;

        /// <summary>路線中心離地圖中心最多偏多少（佔短邊一半的比例）。</summary>
        public const float MaxLateralOffset = 0.7f;

        /// <summary>前後各多少比例的路程用來淡入淡出。</summary>
        public const float FadeFraction = 0.12f;

        /// <summary>
        /// 這個層級會不會畫地形下方的中層水域：只有無法下錨的變溫層與深海。
        /// 淺海與海床的「下方」就是海床，看不到東西從下面經過。
        /// </summary>
        public static bool ShowsMidwater(DepthTier tier)
        {
            return tier == DepthTier.Thermocline || tier == DepthTier.Deep;
        }

        /// <summary>
        /// 一條直線穿過地圖的路線。<paramref name="angle"/> 是前進方向（弧度），
        /// <paramref name="lateral"/>（-1..1）讓路線偏離中心。起點與終點都落在地圖外 <paramref name="margin"/> 格以外，
        /// 所以生物從畫面外游進來、再游出去，不會憑空出現在畫面中央。
        /// </summary>
        public static void CrossingPath(int width, int height, float angle, float lateral, float margin, out Vector2 from, out Vector2 to)
        {
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 perp = new Vector2(-dir.y, dir.x);
            float halfShort = Mathf.Min(width, height) * 0.5f;
            Vector2 center = new Vector2(width * 0.5f, height * 0.5f) + perp * (Mathf.Clamp(lateral, -1f, 1f) * MaxLateralOffset * halfShort);
            // 沿著 dir 走到「地圖外擴 margin」的矩形邊界，兩個方向各走一次
            float forward = DistanceToRectEdge(center, dir, -margin, -margin, width + margin, height + margin);
            float backward = DistanceToRectEdge(center, -dir, -margin, -margin, width + margin, height + margin);
            from = center - dir * backward;
            to = center + dir * forward;
        }

        /// <summary>從矩形內的一點沿方向走到矩形邊界的距離。</summary>
        public static float DistanceToRectEdge(Vector2 p, Vector2 dir, float minX, float minZ, float maxX, float maxZ)
        {
            float best = float.MaxValue;
            if (dir.x > 1e-6f) { best = Mathf.Min(best, (maxX - p.x) / dir.x); }
            if (dir.x < -1e-6f) { best = Mathf.Min(best, (minX - p.x) / dir.x); }
            if (dir.y > 1e-6f) { best = Mathf.Min(best, (maxZ - p.y) / dir.y); }
            if (dir.y < -1e-6f) { best = Mathf.Min(best, (minZ - p.y) / dir.y); }
            return best == float.MaxValue ? 0f : Mathf.Max(0f, best);
        }

        /// <summary>路程比例 0..1 的不透明度：前後 <see cref="FadeFraction"/> 淡入淡出，中段 1。</summary>
        public static float Envelope(float progress)
        {
            if (progress <= 0f || progress >= 1f)
            {
                return 0f;
            }
            float fadeIn = Mathf.Clamp01(progress / FadeFraction);
            float fadeOut = Mathf.Clamp01((1f - progress) / FadeFraction);
            return Smooth(Mathf.Min(fadeIn, fadeOut));
        }

        private static float Smooth(float t)
        {
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// 深度投影：越深的東西越往鏡頭正下方縮（透視），所以鏡頭平移時牠們移動得比地板慢，看起來在地板更下面。
        /// <paramref name="depth"/> 0 = 貼著地板，1 = 無限深（縮成鏡頭下的一點）。回傳畫面上的位置；大小也要乘上 <see cref="DepthScale"/>。
        /// 不能用「鏡頭離地圖中心多遠就整體平移」（原版軌道碎片的做法）：那樣鏡頭在地圖角落時，生物會被推到幾十格外，
        /// 永遠不會出現在玩家看的那塊深淵上。
        /// </summary>
        public static Vector2 ProjectDepth(Vector2 position, Vector2 camera, float depth)
        {
            return camera + (position - camera) * DepthScale(depth);
        }

        public static float DepthScale(float depth)
        {
            return 1f - Mathf.Clamp(depth, 0f, 0.9f);
        }

        /// <summary>
        /// 一張地圖上同時要有幾隻背景生物：深淵格越多越熱鬧，每 <paramref name="cellsPerCreature"/> 格一隻，上限 <paramref name="max"/>。
        /// 沒有深淵（看不到下面）就是 0。
        /// </summary>
        public static int AmbientTarget(int abyssCells, int cellsPerCreature, int max)
        {
            if (abyssCells <= 0 || cellsPerCreature <= 0)
            {
                return 0;
            }
            return Mathf.Clamp(abyssCells / cellsPerCreature, 0, max);
        }

        /// <summary>以每秒格數的速度走完 <paramref name="distance"/> 格要幾 tick（至少 1）。</summary>
        public static int TravelTicks(float distance, float cellsPerSecond)
        {
            if (cellsPerSecond <= 0f)
            {
                return 1;
            }
            return Mathf.Max(1, Mathf.RoundToInt(distance / cellsPerSecond * 60f));
        }
    }
}
