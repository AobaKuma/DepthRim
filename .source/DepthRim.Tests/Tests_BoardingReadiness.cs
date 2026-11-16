using DepthRim;

// 玩家突擊艇出航前檢查（BoardingReadinessMath）。
static partial class Test
{
    static void BoardingReadiness()
    {
        const BoardingReadinessIssue none = BoardingReadinessIssue.None;

        // 非血肉（機械體）一律沒問題
        Check(BoardingReadinessMath.Assess(false, false, 0f, 0f, 0f, DepthTier.Seabed) == none, "mechanoid: no issues even at the seabed without a suit");

        // 淺海：只看潛水服
        Check(BoardingReadinessMath.Assess(true, true, 1f, 0f, 0f, DepthTier.Shallow) == none, "suited colonist at shallow sea: ready");
        Check(BoardingReadinessMath.Assess(true, false, 0f, 0f, 0f, DepthTier.Shallow) == BoardingReadinessIssue.NoSuit, "no suit at shallow sea: NoSuit only");
        Check(BoardingReadinessMath.Assess(true, true, 0.3f, 1f, 0f, DepthTier.Shallow) == BoardingReadinessIssue.LowAir, "air at 30%: LowAir");
        Check(BoardingReadinessMath.Assess(true, true, BoardingReadinessMath.LowAirPercent, 1f, 0f, DepthTier.Shallow) == none, "air exactly at the threshold: fine");

        // 變溫層：潛水服的 1 級抗性夠，常壓的身體差 1 級在容許範圍內
        Check(BoardingReadinessMath.Assess(true, true, 1f, 1f, 0f, DepthTier.Thermocline) == none, "suit (1) at thermocline from the surface: ready");
        Check(BoardingReadinessMath.Assess(true, false, 0f, 0f, 0f, DepthTier.Thermocline)
            == (BoardingReadinessIssue.NoSuit | BoardingReadinessIssue.PressureShortfall), "no suit at thermocline: drowning and pressure damage");

        // 深海：從常壓出發，潛水服撐不住，也會不適
        BoardingReadinessIssue deep = BoardingReadinessMath.Assess(true, true, 1f, 1f, 0f, DepthTier.Deep);
        Check(deep == (BoardingReadinessIssue.PressureShortfall | BoardingReadinessIssue.Maladapted), $"surface crew to the deep: shortfall + maladapted ({deep})");
        // 適應到 1 級：抗性 1 + 適應 1 = 2 撐得住，差 1 級不會不適
        Check(BoardingReadinessMath.Assess(true, true, 1f, 1f, 1f, DepthTier.Deep) == none, "adapted to 1 with a suit: ready for the deep");

        // 海床：住在海床的人（適應 3）就算沒穿潛水服也不會受水壓傷，只會溺水
        Check(BoardingReadinessMath.Assess(true, false, 0f, 0f, 3f, DepthTier.Seabed) == BoardingReadinessIssue.NoSuit, "seabed-adapted without a suit: only NoSuit");
        // 反過來：適應海床的人去淺海會不適
        Check(BoardingReadinessMath.Assess(true, true, 1f, 1f, 3f, DepthTier.Shallow) == BoardingReadinessIssue.Maladapted, "seabed-adapted crew going to shallow sea: maladapted");
    }
}
