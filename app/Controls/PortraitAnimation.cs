namespace AzerothCompanion.Controls;

internal readonly record struct PortraitPose(int Frame, double OffsetX, double OffsetY, int IntervalMilliseconds);

/// <summary>Deterministic finite-sheet animation. No audio device, images or timers.</summary>
internal sealed class PortraitAnimation
{
    private string state = "idle";
    private double stateStarted, lastFrameChanged = double.NegativeInfinity, blinkOffset;
    private float envelope;
    private int mouth, shownFrame;

    internal void Reset(double now, int seed = 0)
    {
        state = "idle"; stateStarted = now; lastFrameChanged = double.NegativeInfinity;
        blinkOffset = Math.Abs(seed % 9) * .11;
        envelope = 0; mouth = 0; shownFrame = 0;
    }

    internal void SetState(string value, double now)
    {
        if (state == value) return;
        state = value; stateStarted = now; lastFrameChanged = double.NegativeInfinity;
        envelope = 0; mouth = 0;
    }

    internal PortraitPose Sample(double now, float speechLevel)
    {
        double elapsed = Math.Max(0, now - stateStarted);
        int desired, interval = 250;
        double x = 0, y = 0;
        if (state == "speaking")
        {
            var level = float.IsFinite(speechLevel) ? Math.Clamp(speechLevel, 0, 1) : 0;
            envelope += (level - envelope) * (level > envelope ? .7f : .55f);
            mouth = mouth switch
            {
                0 => envelope > .18f ? 2 : envelope > .035f ? 1 : 0,
                1 => envelope > .18f ? 2 : envelope < .022f ? 0 : 1,
                _ => envelope < .022f ? 0 : envelope < .12f ? 1 : 2
            };
            // Frames 3/7 are closed-mouth gestures, 4/5 are small/wide speech, and
            // the authored lean (6) appears only during the small-mouth portion.
            int gesture = (int)(elapsed / 1.2) % 4;
            desired = mouth switch { 2 => 5, 1 => gesture == 2 ? 6 : 4, _ => gesture is 2 or 3 ? 7 : 3 };
            interval = 90;
            x = Math.Round(Math.Sin(elapsed * 1.8) * .7);
            y = Math.Round(Math.Sin(elapsed * 3.2) * 1.4);
        }
        else
        {
            double idlePhase = (elapsed + blinkOffset) % 5.2;
            desired = state switch
            {
                "quiet" or "paused" => 0,
                "thinking" or "reading" => 8,
                "listening" => 9,
                "amused" => elapsed % 3.2 < 2.3 ? 10 : 0,
                "wave" or "greeting" => elapsed < 1.8 ? ((int)(elapsed / .6) % 2 == 0 ? 11 : 2) : 0,
                _ => idlePhase is >= 3.9 and < 4.25 ? 1 : idlePhase is >= 1.8 and < 3.1 ? 2 : 0
            };
            if (state is not ("quiet" or "paused")) y = Math.Round(Math.Sin(elapsed * 1.4) * .7);
            interval = state is "quiet" or "paused" ? 1000 : state is "thinking" or "reading" or "listening" ? 200 : 250;
        }
        // At 90ms talk ticks, a frame remains for at least two ticks (~180ms).
        if (desired != shownFrame && now - lastFrameChanged >= .14)
        { shownFrame = desired; lastFrameChanged = now; }
        return new(shownFrame, x, y, interval);
    }
}
