namespace WindowsDuo;

internal static class Smooth
{
    /// <summary>
    /// Critically damped follow, Game Programming Gems 4 / Unity SmoothDamp.
    /// Keeps fold angle on the vsync loop even when the camera is only 30 fps.
    /// </summary>
    public static float Damp(float current, float target, ref float velocity, float smoothTime, float dt)
    {
        smoothTime = Math.Max(0.0001f, smoothTime);
        dt = Math.Clamp(dt, 1e-4f, 0.05f);
        var omega = 2f / smoothTime;
        var x = omega * dt;
        var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        var change = current - target;
        var original = target;
        var temp = (velocity + omega * change) * dt;
        velocity = (velocity - omega * temp) * exp;
        var output = target + (change + temp) * exp;
        if (original - current > 0f == output > original)
        {
            output = original;
            velocity = dt > 0f ? (output - original) / dt : 0f;
        }

        return output;
    }
}
