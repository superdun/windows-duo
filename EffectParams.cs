namespace WindowsDuo;

public sealed class EffectParams
{
    /// <summary>Hinge orientation. 0 = bottom edge, 90 = left edge.</summary>
    public float Angle { get; set; }

    /// <summary>Fold travel in degrees. 0–55 stretches like the old 0–40 range; past 55 the screen fades to black.</summary>
    public float Amount { get; set; } = 32;

    /// <summary>Blur at the hinge as a fraction of the far-edge blur. 0 = far edge only.</summary>
    public float Gradient { get; set; }

    /// <summary>Gaussian radius at full effect, as a 0..1 fraction of 10–160px. Mac Duo default is 135.</summary>
    public float Highlight { get; set; } = 0.833f;

    public float HighlightThreshold { get; set; } = 0.82f;

    /// <summary>How dark the far edge goes at full fold. Mac Duo default is 1.</summary>
    public float Sheen { get; set; } = 1f;
}
