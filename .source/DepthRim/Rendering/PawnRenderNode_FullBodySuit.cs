using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 連頭盔的全身衣物（潛水服）節點：<c>texPath</c> 的北／東／南三張貼圖加上同名 <c>m</c> 遮罩
    /// （<c>DiveSuit_southm</c> 等），所有體型共用。
    ///
    /// <para>遮罩紅色區域 = 主色 = 衣物的 <see cref="Thing.DrawColor"/>（染色、意識形態配色），
    /// 黑色區域 = 第二色 = 白色，維持原圖（面窗、管線、氣瓶）。原版的基本節點只用 props.color、
    /// 也不換遮罩著色器，所以另寫這個類別。雕像沿用原版做法：單色 Cutout。</para>
    /// </summary>
    public class PawnRenderNode_FullBodySuit : PawnRenderNode
    {
        public PawnRenderNode_FullBodySuit(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
        }

        public override Graphic GraphicFor(Pawn pawn)
        {
            string path = TexPathFor(pawn);
            if (path.NullOrEmpty())
            {
                return null;
            }
            Color? statue = pawn.Drawer.renderer.StatueColor;
            if (statue.HasValue)
            {
                return GraphicDatabase.Get<Graphic_Multi>(path, ShaderDatabase.Cutout, Vector2.one, statue.Value);
            }
            Shader shader = props.shaderTypeDef?.Shader ?? ShaderDatabase.CutoutComplex;
            return GraphicDatabase.Get<Graphic_Multi>(path, shader, Vector2.one, ColorFor(pawn), Color.white);
        }

        public override Color ColorFor(Pawn pawn)
        {
            Color color = apparel?.DrawColor ?? props.color ?? Color.white;
            if (props.useRottenColor && pawn.Drawer.renderer.CurRotDrawMode == RotDrawMode.Rotting)
            {
                color = PawnRenderUtility.GetRottenColor(color);
            }
            return color;
        }
    }
}
