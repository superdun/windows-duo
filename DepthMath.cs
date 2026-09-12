namespace WindowsDuo;

/// <summary>
/// Picture hinged at one screen edge, matching Mac Duo / iPhone Duo:
/// the glass turns under a fixed eye, so the far edge recedes, softens, and dims.
/// </summary>
internal static class DepthMath
{
    public readonly struct Mat3
    {
        public readonly double M00, M01, M02;
        public readonly double M10, M11, M12;
        public readonly double M20, M21, M22;

        public Mat3(
            double m00, double m01, double m02,
            double m10, double m11, double m12,
            double m20, double m21, double m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        public static Mat3 Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        public Mat3 Inverse()
        {
            var det =
                M00 * (M11 * M22 - M12 * M21) -
                M01 * (M10 * M22 - M12 * M20) +
                M02 * (M10 * M21 - M11 * M20);
            if (Math.Abs(det) < 1e-12)
            {
                return Identity;
            }

            var inv = 1.0 / det;
            return new Mat3(
                (M11 * M22 - M12 * M21) * inv,
                (M02 * M21 - M01 * M22) * inv,
                (M01 * M12 - M02 * M11) * inv,
                (M12 * M20 - M10 * M22) * inv,
                (M00 * M22 - M02 * M20) * inv,
                (M02 * M10 - M00 * M12) * inv,
                (M10 * M21 - M11 * M20) * inv,
                (M01 * M20 - M00 * M21) * inv,
                (M00 * M11 - M01 * M10) * inv);
        }
    }

    public readonly record struct Vec2(double X, double Y);

    /// <summary>
    /// Maps the picture rectangle onto <paramref name="corners"/>
    /// (bottom-left, bottom-right, top-right, top-left) in y-up pixels.
    /// </summary>
    public static Mat3 PictureToScreen(double width, double height, Vec2[] corners)
    {
        var (x0, y0) = (corners[0].X, corners[0].Y);
        var (x1, y1) = (corners[1].X, corners[1].Y);
        var (x2, y2) = (corners[2].X, corners[2].Y);
        var (x3, y3) = (corners[3].X, corners[3].Y);

        var dx1 = x1 - x2;
        var dx2 = x3 - x2;
        var dx3 = x0 - x1 + x2 - x3;
        var dy1 = y1 - y2;
        var dy2 = y3 - y2;
        var dy3 = y0 - y1 + y2 - y3;
        var g = 0.0;
        var h = 0.0;
        if (Math.Abs(dx3) > 1e-9 || Math.Abs(dy3) > 1e-9)
        {
            var determinant = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(determinant) > 1e-12)
            {
                g = (dx3 * dy2 - dx2 * dy3) / determinant;
                h = (dx1 * dy3 - dx3 * dy1) / determinant;
            }
        }

        var a = x1 - x0 + g * x1;
        var b = x3 - x0 + h * x3;
        var c = x0;
        var d = y1 - y0 + g * y1;
        var e = y3 - y0 + h * y3;
        var f = y0;

        return new Mat3(
            a / width, b / height, c,
            d / width, e / height, f,
            g / width, h / height, 1);
    }

    /// <summary>
    /// Hinge at the bottom (0°) or left (90°). <paramref name="travelDegrees"/> is how far the lid has closed from 90°.
    /// </summary>
    public static Vec2[] Corners(
        double width,
        double height,
        double travelDegrees,
        double hingeDegrees,
        double viewingDistance = 2.7,
        double recession = 1.0)
    {
        var startAngle = 90.0;
        var currentAngle = Math.Clamp(startAngle - travelDegrees, 10.0, 90.0);
        var swapped = hingeDegrees > 45 && hingeDegrees < 135;
        var alongHinge = swapped ? height : width;
        var away = swapped ? width : height;
        var local = BottomHingeCorners(alongHinge, away, startAngle, currentAngle, viewingDistance, recession);
        if (!swapped)
        {
            return local;
        }

        // local y is distance from the hinge; put that hinge on the left edge.
        return
        [
            new Vec2(local[0].Y, local[0].X),
            new Vec2(local[3].Y, local[3].X),
            new Vec2(local[2].Y, local[2].X),
            new Vec2(local[1].Y, local[1].X),
        ];
    }

    private static Vec2[] BottomHingeCorners(
        double width,
        double height,
        double startAngle,
        double currentAngle,
        double viewingDistanceRatio,
        double recession)
    {
        const double maxSeparationDegrees = 88;
        var start = startAngle * Math.PI / 180.0;
        var current = currentAngle * Math.PI / 180.0;
        var travel = Math.Max(startAngle - currentAngle, 0);
        var separation = Math.Min(recession * travel, maxSeparationDegrees) * Math.PI / 180.0;

        var reach = height * viewingDistanceRatio + height / 2 * Math.Cos(start);
        var rise = height / 2 * Math.Sin(start);
        var along = reach * Math.Cos(current) + rise * Math.Sin(current);
        var depth = Math.Max(reach * Math.Sin(current) - rise * Math.Cos(current), height / 10);
        var half = width / 2;

        Vec2 Project(double x, double y)
        {
            var scale = depth / (depth + y * Math.Sin(separation));
            return new Vec2(
                half + (x - half) * scale,
                along + (y * Math.Cos(separation) - along) * scale);
        }

        return [Project(0, 0), Project(width, 0), Project(width, height), Project(0, height)];
    }
}
