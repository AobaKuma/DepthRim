using Verse;

namespace DepthRim
{
    /// <summary>
    /// 連頭盔的全身衣物（潛水服）用的渲染節點：掛在 <c>apparel.renderNodeProperties</c>，
    /// 貼圖只有北／東／南三張（<see cref="Graphic_Multi"/>），不分體型，所有體型共用。
    ///
    /// <para>原版的身體衣物節點會在 wornGraphicPath 後面接 <c>_體型</c>；把 wornGraphicPath 留空，
    /// 原版就不建那個節點，只建這裡的自訂節點。</para>
    ///
    /// <para>顯示條件同原版身體衣物（<see cref="PawnRenderNodeWorker_Apparel_Body"/>）：
    /// 躺床時跟身體一起藏起來，沒有 <see cref="PawnRenderFlags.Clothes"/> 時不畫。
    /// 不直接用那個 worker，是因為它會把節點轉型成 <see cref="PawnRenderNode_Apparel"/>。</para>
    /// </summary>
    public class PawnRenderNodeWorker_FullBodySuit : PawnRenderNodeWorker_Body
    {
        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            return base.CanDrawNow(node, parms) && parms.flags.FlagSet(PawnRenderFlags.Clothes);
        }
    }
}
