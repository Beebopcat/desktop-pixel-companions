namespace AzerothCompanion.Core;

/// <summary>Pure physical-pixel geometry. Movement does not depend on a game window.</summary>
public sealed class RoamingPlanner
{
    private readonly Random random;
    private readonly List<RoamingBody> bodies = [];
    private double left, right, bottom, monitorArea, gap;
    public RoamingPlanner(int? seed = null) => random = seed is { } value ? new Random(value) : new Random();
    public IReadOnlyList<RoamingBody> Bodies => bodies;
    public double OccupiedAreaFraction => monitorArea <= 0 ? 0 : bodies.Sum(b => b.Width * b.Height) / monitorArea;

    public void Configure(double workLeft, double workTop, double workWidth, double workHeight,
        double monitorWidth, double monitorHeight, int count = 2, double fraction = .04, double reservedAreaPixels = 0, double linearScale = .25)
    {
        if (!new[] { workLeft, workTop, workWidth, workHeight, monitorWidth, monitorHeight }.All(double.IsFinite)
            || workWidth < 2 || workHeight < 2 || monitorWidth < 2 || monitorHeight < 2)
            throw new ArgumentOutOfRangeException(nameof(workWidth));
        count = Math.Clamp(count, 1, 2);
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, .015, .045) : .04;
        linearScale = double.IsFinite(linearScale) ? Math.Clamp(linearScale, .15, 1) : .25;
        var previousLeft = left; var previousWidth = right - left;
        var positions = bodies.Select(b => previousWidth > b.Width ? (b.X - previousLeft) / (previousWidth - b.Width) : .5).ToArray();
        left = workLeft; right = workLeft + workWidth; bottom = workTop + workHeight;
        monitorArea = monitorWidth * monitorHeight;
        reservedAreaPixels = double.IsFinite(reservedAreaPixels) ? Math.Clamp(reservedAreaPixels, 0, Math.Max(0, monitorArea * fraction - count)) : 0;
        var baseGap = Math.Min(12, workWidth * .015);
        // One design reference for a resolution. Switching flooring or removing
        // the second companion must not make the remaining character inflate.
        // The footprint setting is a maximum; it does not spend freed space.
        var referenceFloorArea=Math.Min(workWidth*6,Math.Max(0,monitorArea*.04-2));
        var referenceSide=Math.Max(1,Math.Floor(Math.Sqrt(Math.Max(2,monitorArea*.04-referenceFloorArea)/2)));
        var availableSide=Math.Min(Math.Sqrt((monitorArea*fraction-reservedAreaPixels)/count),
            Math.Min(workHeight,(workWidth-baseGap*(count-1))/count));
        var side=Math.Max(1,Math.Floor(Math.Min(referenceSide*linearScale,availableSide)));
        gap = Math.Max(1, Math.Floor(baseGap * linearScale));
        while (bodies.Count > count) bodies.RemoveAt(bodies.Count - 1);
        while (bodies.Count < count) bodies.Add(new RoamingBody(bodies.Count));
        for (var i = 0; i < count; i++)
        {
            var body = bodies[i]; body.ReferenceSide = referenceSide; body.Width = body.Height = side; body.Y = bottom - side;
            body.X = left + (right - left - side) * (i < positions.Length ? Math.Clamp(positions[i], 0, 1) : (i + 1d) / (count + 1));
            // About one stride per gait cycle: companions visibly cross the screen instead of drifting.
            body.Speed = Math.Clamp(side * (.55 + random.NextDouble() * .15), 20, 130);
            body.FacingLeft = i % 2 == 0;
            body.Remaining = .35 + random.NextDouble() * 1.7;
            body.Walking = false;
        }
        // Keep stable left/right ordering even after a very narrow display resize.
        for (var i = 0; i < count; i++)
        {
            var maximum = right - (count - i) * side - (count - i - 1) * gap;
            var minimum = i == 0 ? left : bodies[i - 1].X + side + gap;
            bodies[i].X = Math.Clamp(bodies[i].X, minimum, Math.Max(minimum, maximum));
        }
    }

    public void PauseBody(int index, bool paused)
    {
        if (index < 0 || index >= bodies.Count) return;
        var body = bodies[index]; body.Paused = paused;
        if (paused) body.Walking = false;
    }

    public void SetPosition(int index, double x)
    {
        if (index < 0 || index >= bodies.Count || !double.IsFinite(x)) return;
        var body = bodies[index];
        var minimum = index == 0 ? left : bodies[index - 1].X + bodies[index - 1].Width + gap;
        var maximum = index == bodies.Count - 1 ? right - body.Width : bodies[index + 1].X - body.Width - gap;
        body.X = Math.Clamp(x, minimum, Math.Max(minimum, maximum)); body.Y = bottom - body.Height;
    }

    public void Step(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        // A resumed or blocked UI must never jump across the desktop.
        var delta = Math.Min(elapsedSeconds, .1);
        foreach (var body in bodies)
        {
            if (body.Paused) { body.Walking = false; continue; }
            body.Remaining -= delta;
            if (body.Remaining <= 0)
            {
                body.Walking = !body.Walking;
                body.Remaining = body.Walking ? 4 + random.NextDouble() * 7 : 2 + random.NextDouble() * 3;
                if (body.Walking) body.FacingLeft = random.Next(2) == 0;
            }
            if (!body.Walking) continue;
            var target = body.X + (body.FacingLeft ? -1 : 1) * body.Speed * delta;
            SetPosition(body.Id, target);
            if (Math.Abs(body.X - target) > .001)
            {
                body.FacingLeft = !body.FacingLeft; body.Walking = false;
                body.Remaining = .5 + random.NextDouble() * 1.5;
            }
        }
    }
}

public sealed class RoamingBody(int id)
{
    public int Id { get; } = id;
    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Width { get; internal set; }
    public double Height { get; internal set; }
    public double ReferenceSide { get; internal set; }
    public bool Walking { get; internal set; }
    public bool FacingLeft { get; internal set; }
    internal bool Paused { get; set; }
    internal double Remaining { get; set; }
    internal double Speed { get; set; }
}
