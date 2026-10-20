namespace AzerothCompanion.Core;

public static class RoamingSelfTests
{
    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Geometry(RoamingPlanner planner, double left, double top, double width, double height)
        {
            Require(planner.OccupiedAreaFraction <= .045 + 1e-10, "Roaming avatar footprint exceeded 4.5 percent.");
            foreach (var body in planner.Bodies)
            {
                Require(body.X >= left - 1e-8 && body.X + body.Width <= left + width + 1e-8, "Roaming escaped horizontal primary bounds.");
                Require(body.Y >= top && body.Y + body.Height == top + height, "Roaming left the primary bottom lane.");
            }
            for (var i = 1; i < planner.Bodies.Count; i++)
                Require(planner.Bodies[i - 1].X + planner.Bodies[i - 1].Width <= planner.Bodies[i].X, "Roaming avatars overlapped.");
        }
        foreach (var size in new[] { (1920, 1080), (3840, 2160), (1080, 1920), (5120, 1440), (800, 600) })
        foreach (var count in new[] { 1, 2 })
        foreach (var fraction in new[] { .015, .04, .045, 1, double.NaN })
        {
            var planner = new RoamingPlanner(731);
            planner.Configure(-size.Item1, -83, size.Item1, size.Item2 - 48, size.Item1, size.Item2, count, fraction);
            for (var i = 0; i < 1800; i++)
            {
                planner.Step(i % 17 == 0 ? 2 : 1d / 30);
                Geometry(planner, -size.Item1, -83, size.Item1, size.Item2 - 48);
            }
            planner.SetPosition(0, -1e9); planner.SetPosition(count - 1, 1e9);
            Geometry(planner, -size.Item1, -83, size.Item1, size.Item2 - 48);
            planner.SetPosition(0, 1e9); planner.SetPosition(count - 1, -1e9);
            Geometry(planner, -size.Item1, -83, size.Item1, size.Item2 - 48);
        }
        passed.Add("Roaming: 50 display/count/size configurations and 90,000 ticks remain bottom-anchored, nonoverlapping and within 4.5 percent.");

        var a = new RoamingPlanner(101); var b = new RoamingPlanner(101);
        a.Configure(0, 0, 2560, 1400, 2560, 1440); b.Configure(0, 0, 2560, 1400, 2560, 1440);
        var moved = new bool[2]; var pausedNaturally = new bool[2]; var independentlyMoving = false;
        var initial = a.Bodies.Select(body => body.X).ToArray();
        for (var i = 0; i < 3600; i++)
        {
            a.Step(1d / 30); b.Step(1d / 30);
            for (var body = 0; body < 2; body++)
            {
                Require(a.Bodies[body].X == b.Bodies[body].X && a.Bodies[body].Walking == b.Bodies[body].Walking, "Seeded roaming was not deterministic.");
                moved[body] |= Math.Abs(a.Bodies[body].X - initial[body]) > 30;
                pausedNaturally[body] |= i > 150 && !a.Bodies[body].Walking;
            }
            independentlyMoving |= a.Bodies[0].Walking != a.Bodies[1].Walking;
        }
        Require(moved.All(x => x) && pausedNaturally.All(x => x) && independentlyMoving, "Avatars did not independently walk and pause.");
        passed.Add("Roaming: deterministic independent movement and natural pauses.");

        a.PauseBody(0, true); var held = a.Bodies[0].X;
        for (var i = 0; i < 300; i++) a.Step(.1);
        Require(a.Bodies[0].X == held && !a.Bodies[0].Walking, "Busy avatar moved while paused.");
        a.PauseBody(0, false);
        a.Configure(320, 200, 900, 1100, 900, 1200);
        for (var i = 0; i < 300; i++) { a.Step(.1); Geometry(a, 320, 200, 900, 1100); }
        a.Configure(-700, -1200, 700, 650, 700, 700, 1, .045); Geometry(a, -700, -1200, 700, 650);
        passed.Add("Roaming: speech/interaction pause holds position, primary-display relocation and duo-to-solo resizing stay clamped.");

        var oldX = a.Bodies[0].X;
        a.Step(double.NaN); a.Step(-2); a.SetPosition(0, double.PositiveInfinity);
        Require(a.Bodies[0].X == oldX, "Invalid motion input changed placement.");
        passed.Add("Roaming: nonfinite positions/deltas are ignored and delayed ticks are bounded.");
        foreach(var size in new[]{(1920,1080),(3840,2160),(1080,1920),(800,600)})
        foreach(var fraction in new[]{.015,.04,.045})
        foreach(var count in new[]{1,2})
        {
            var floorArea=size.Item1*6d;var monitorArea=(double)size.Item1*size.Item2;
            a.Configure(0,0,size.Item1,size.Item2-54,size.Item1,size.Item2,count,fraction,floorArea);
            Require(a.OccupiedAreaFraction+floorArea/monitorArea<=fraction+1e-10,"Floor reservation exceeded the selected combined area cap.");
            Geometry(a,0,0,size.Item1,size.Item2-54);
        }
        passed.Add("Roaming: 24 display/count/footprint combinations reserve the floor strip inside the selected combined area cap.");

        // A floor or companion-count change releases footprint budget, but must
        // not change the user's chosen character size. Exercise reconfiguration
        // on one planner so this also covers the running overlay's transitions.
        foreach (var size in new[] { (1920, 1080), (3840, 2160), (1080, 1920), (800, 600) })
        {
            var stable = new RoamingPlanner(812);
            var area = (double)size.Item1 * size.Item2;
            stable.Configure(113, -27, size.Item1, size.Item2 - 54, size.Item1, size.Item2,
                2, .04, size.Item1 * 6d, 1);
            var fullSize = stable.Bodies[0].Width;
            var referenceSize = stable.Bodies[0].ReferenceSide;
            foreach (var floorHeight in new[] { 6, 0, 6 })
            foreach (var count in new[] { 2, 1, 2 })
            foreach (var fraction in new[] { .015, .04, .045 })
            {
                var reservation = size.Item1 * (double)floorHeight;
                var laneHeight = size.Item2 - 48 - floorHeight;
                stable.Configure(113, -27, size.Item1, laneHeight, size.Item1, size.Item2,
                    count, fraction, reservation, .25);
                foreach (var body in stable.Bodies)
                {
                    Require(body.ReferenceSide == referenceSize, "Flooring or companion count changed the design reference size.");
                    Require(body.Width == Math.Floor(fullSize * .25) && body.Height == body.Width,
                        "Quarter-size characters changed size or aspect ratio when switching flooring, count or footprint cap.");
                }
                Require(stable.OccupiedAreaFraction + reservation / area <= fraction + 1e-10,
                    "Stable quarter-size characters and flooring exceeded the selected footprint cap.");
                for (var tick = 0; tick < 30; tick++)
                {
                    stable.Step(.1);
                    Geometry(stable, 113, -27, size.Item1, laneHeight);
                }
            }
        }
        passed.Add("Roaming: 108 reconfigurations preserve quarter-size aspect ratio across None/visible flooring, solo/duo and footprint caps; flooring stays within the cap.");

        var desktop = new RoamingPlanner(27);
        desktop.Configure(0, 0, 1920, 1026, 1920, 1080, 2, .04, 1920 * 6);
        Require(desktop.Bodies.All(body => body.ReferenceSide == 188 && body.Width == 47 && body.Height == 47),
            "The 1920 by 1080 desktop no longer renders the 188-pixel design at 47-pixel quarter size.");
        passed.Add("Roaming: the 1920 by 1080 desktop uses a stable 188-pixel reference and 47 by 47 physical-pixel companions.");
        return passed;
    }
}
