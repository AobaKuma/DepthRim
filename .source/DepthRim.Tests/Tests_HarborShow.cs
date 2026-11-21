using System.Collections.Generic;
using DepthRim;
using UnityEngine;

// 進出港演出（海岸造船 M4）的船體姿態。
static partial class Test
{
    static void HarborShowMathTests()
    {
        Check(Mathf.Abs(HarborShowMath.Compass(Vector2.up)) < 0.01f && Mathf.Abs(HarborShowMath.Compass(Vector2.right) - 90f) < 0.01f
              && Mathf.Abs(HarborShowMath.Compass(Vector2.down) - 180f) < 0.01f, "compass: north 0, east 90, south 180");
        Check(Mathf.Abs(HarborShowMath.Wrap(270f) + 90f) < 0.01f && Mathf.Abs(HarborShowMath.Wrap(-200f) - 160f) < 0.01f, "turning takes the short way round");

        Check(HarborShowMath.FinalHeading(3, 9, new Vector2(10, 0), new Vector2(10, 20)) == 0f, "a tall ship coming in northwards ends bow north");
        Check(HarborShowMath.FinalHeading(3, 9, new Vector2(10, 30), new Vector2(10, 20)) == 180f, "coming in southwards ends bow south");
        Check(HarborShowMath.FinalHeading(9, 3, new Vector2(0, 5), new Vector2(20, 5)) == 90f, "a wide ship coming in eastwards ends bow east");

        // 從南邊地圖邊往北開，到 (20,20) 迴轉，往東側推進泊位 (25,20)
        List<Vector2> path = new List<Vector2> { new Vector2(20, 0), new Vector2(20, 20) };
        Vector2 berth = new Vector2(25, 20);
        float final = 90f; // 停好時船頭朝東（例如 9×3）
        HarborShowMath.Pose start = HarborShowMath.At(0f, path, berth, final, true, false);
        Check((start.pos - path[0]).magnitude < 0.01f && Mathf.Abs(start.angle - (-90f)) < 0.01f, $"it starts at the entry, bow north (angle {start.angle})");
        HarborShowMath.Pose mid = HarborShowMath.At(0.25f, path, berth, final, true, false);
        Check(mid.pos.y > 0f && mid.pos.y < 20f && Mathf.Abs(mid.pos.x - 20f) < 0.01f, "half way through the approach it is on the route");
        HarborShowMath.Pose atTurn = HarborShowMath.At(HarborShowMath.TravelEnd, path, berth, final, true, false);
        HarborShowMath.Pose afterTurnStart = HarborShowMath.At(HarborShowMath.TravelEnd + 1e-4f, path, berth, final, true, false);
        Check((atTurn.pos - afterTurnStart.pos).magnitude < 0.05f && Mathf.Abs(atTurn.angle - afterTurnStart.angle) < 0.5f, "no jump when the turn begins");
        HarborShowMath.Pose turned = HarborShowMath.At(HarborShowMath.TurnEnd, path, berth, final, true, false);
        Check((turned.pos - path[1]).magnitude < 0.01f && Mathf.Abs(turned.angle) < 0.01f, "after the turn it faces its final heading at the turning point");
        HarborShowMath.Pose bump = HarborShowMath.At(HarborShowMath.TurnEnd + (HarborShowMath.SlideEnd - HarborShowMath.TurnEnd) * 0.8f, path, berth, final, true, false);
        Check(bump.pos.x > berth.x && bump.pos.x - berth.x < HarborShowMath.BumpCells + 0.01f, $"a side berth squeezes the fenders past the berth ({bump.pos.x - berth.x:0.000} cells)");
        HarborShowMath.Pose anchored = HarborShowMath.At(HarborShowMath.TurnEnd + (HarborShowMath.SlideEnd - HarborShowMath.TurnEnd) * 0.8f, path, berth, final, false, false);
        Check(anchored.pos.x <= berth.x + 1e-4f, "anchoring does not overshoot");
        HarborShowMath.Pose end = HarborShowMath.At(1f, path, berth, final, true, false);
        Check((end.pos - berth).magnitude < 1e-4f && Mathf.Abs(end.angle) < 1e-4f, "it ends exactly on the berth with no rotation (no jump on the last frame)");
        HarborShowMath.Pose shortStart = HarborShowMath.At(0f, path, berth, final, true, true);
        Check(shortStart.pos.y > 10f, $"the short version starts near the end of the route ({shortStart.pos})");
        Check(HarborShowMath.Bob(1f, 3f) == 0f && HarborShowMath.Bob(0.5f, 3f) == 0f, "no bob while moving or on the last frame");

        List<Vector2> bent = new List<Vector2> { new Vector2(0, 0), new Vector2(0, 10), new Vector2(10, 10) };
        Vector2 p = HarborShowMath.AlongPath(bent, 0.75f, out Vector2 d);
        Check((p - new Vector2(5, 10)).magnitude < 0.01f && (d - Vector2.right).magnitude < 0.01f, "positions along a bent route go by arc length");

        // 實際秒數：開船速度固定，路線長短只改變開的那段多久；出港倒著播用同一條時間軸，速度一樣
        HarborShowMath.Timeline near = HarborShowMath.TimelineFor(40f, false);
        HarborShowMath.Timeline far = HarborShowMath.TimelineFor(120f, false);
        Check(Mathf.Abs(40f / near.travel - 120f / far.travel) < 0.01f, $"the same cruising speed on short and long routes ({40f / near.travel:0.0} vs {120f / far.travel:0.0} cells/s)");
        Check(near.turn == far.turn && near.slide == far.slide && near.settle == far.settle, "turning, sliding and settling take the same time on any route");
        Check(HarborShowMath.TimelineFor(1f, false).travel >= HarborShowMath.MinTravelSeconds - 1e-4f
              && HarborShowMath.TimelineFor(10000f, false).travel <= HarborShowMath.MaxTravelSeconds + 1e-4f, "travel time is clamped");
        HarborShowMath.Timeline shortTl = HarborShowMath.TimelineFor(120f, true);
        Check(shortTl.Total < far.Total, $"the short version is shorter ({shortTl.Total:0.0}s vs {far.Total:0.0}s)");
        Check(HarborShowMath.ProgressAt(0f, far) == 0f && HarborShowMath.ProgressAt(far.Total, far) == 1f
              && Mathf.Abs(HarborShowMath.ProgressAt(far.travel, far) - HarborShowMath.TravelEnd) < 1e-4f
              && Mathf.Abs(HarborShowMath.ProgressAt(far.travel + far.turn, far) - HarborShowMath.TurnEnd) < 1e-4f
              && Mathf.Abs(HarborShowMath.ProgressAt(far.travel + far.turn + far.slide, far) - HarborShowMath.SlideEnd) < 1e-4f, "seconds map onto the phase boundaries");
    }
}
