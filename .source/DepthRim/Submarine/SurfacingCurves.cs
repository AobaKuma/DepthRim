using Verse;

namespace DepthRim
{
    /// <summary>
    /// 出水演出的時間曲線（2026-11-15），全部用原版的 <see cref="SimpleCurve"/>（跟原版 skyfaller 的
    /// speedCurve、zPositionCurve 一樣：幾個關鍵點、中間線性內插、兩端夾住）。橫軸都是「破水後幾秒」。
    /// 船體不再用 SmoothStep 慢慢淡入：破水的一瞬間被水花、白浪蓋住，水往兩邊分開時船已經在了。
    /// </summary>
    public static class SurfacingCurves
    {
        /// <summary>清楚船體的不透明度。前 0.25 秒白浪完全蓋住船，所以可以很快出現。</summary>
        public static readonly SimpleCurve ShipAlpha = new SimpleCurve
        {
            new CurvePoint(0f, 0f),
            new CurvePoint(0.25f, 0.45f),
            new CurvePoint(0.8f, 0.9f),
            new CurvePoint(1.3f, 1f),
        };

        /// <summary>船體顏色從深藍到原色（0 = 深藍）。海水從甲板淌掉，顏色才回來。</summary>
        public static readonly SimpleCurve ShipTint = new SimpleCurve
        {
            new CurvePoint(0f, 0.15f),
            new CurvePoint(0.6f, 0.6f),
            new CurvePoint(1.6f, 0.9f),
            new CurvePoint(2.5f, 1f),
        };

        /// <summary>船體縮放：從水下（0.96）衝出水面時稍微衝過頭（1.02），再落回原尺寸。</summary>
        public static readonly SimpleCurve ShipScale = new SimpleCurve
        {
            new CurvePoint(0f, 0.96f),
            new CurvePoint(0.7f, 1.02f),
            new CurvePoint(1.4f, 0.995f),
            new CurvePoint(2.1f, 1f),
        };

        /// <summary>
        /// 船體往北的偏移（格；原版把高度畫成往北偏）：船頭衝出水面時往上一彈，落回去、再小彈一下。
        /// 形狀照原版穿梭機降落 zPositionCurve（高處停一下、最後一段很快落下）倒過來。
        /// </summary>
        public static readonly SimpleCurve ShipLift = new SimpleCurve
        {
            new CurvePoint(0f, -0.2f),
            new CurvePoint(0.45f, 0.28f),
            new CurvePoint(0.75f, 0.22f),
            new CurvePoint(1.15f, -0.06f),
            new CurvePoint(1.7f, 0.03f),
            new CurvePoint(2.3f, 0f),
        };

        /// <summary>破水後船邊小出水的密度（倍率）：破水瞬間最多，兩秒半內收掉。</summary>
        public static readonly SimpleCurve SplashRate = new SimpleCurve
        {
            new CurvePoint(0f, 1f),
            new CurvePoint(0.5f, 0.75f),
            new CurvePoint(1.6f, 0.25f),
            new CurvePoint(2.5f, 0f),
        };

        /// <summary>從船頭劈到船尾要幾秒（船越長越久，但不拖；太快就看不出先後）。</summary>
        public static float SweepSeconds(int length)
        {
            float s = length * 0.06f;
            return s < 0.6f ? 0.6f : (s > 1.4f ? 1.4f : s);
        }

        /// <summary>同一段從中線到舷邊晚幾秒（水從龍骨往兩舷翻）；船邊外一格再晚一點。</summary>
        public const float SideDelay = 0.12f;

        /// <summary>每一段船體在那段被劈開後幾秒才開始浮現（水柱先衝起來蓋住，船才從水柱底下出來）。</summary>
        public const float StripRevealDelay = 0.12f;
    }
}
