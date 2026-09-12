using Windows.Devices.Sensors;

namespace WindowsDuo;

internal readonly record struct LidHardwareReport(
    bool Hinge,
    bool Inclinometer,
    bool Accelerometer,
    bool Orientation,
    bool Light,
    string CameraName)
{
    public bool HasAngle => Hinge || Inclinometer;
    public string Summary
    {
        get
        {
            if (Hinge)
            {
                return "铰链角传感器";
            }

            if (Inclinometer)
            {
                return "屏幕倾角计";
            }

            if (!string.IsNullOrEmpty(CameraName))
            {
                return "摄像头光流（无盖子角度传感器）";
            }

            return "没有盖子角度，也没有摄像头";
        }
    }
}

internal static class LidHardware
{
    public static async Task<LidHardwareReport> ProbeAsync()
    {
        HingeAngleSensor? hinge = null;
        try
        {
            hinge = await HingeAngleSensor.GetDefaultAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("hinge probe: " + Log.Describe(ex));
        }

        var inclinometer = Inclinometer.GetDefault();
        var accelerometer = Accelerometer.GetDefault();
        var orientation = OrientationSensor.GetDefault();
        var light = LightSensor.GetDefault();
        var camera = "";
        try
        {
            var cams = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                Windows.Devices.Enumeration.DeviceClass.VideoCapture);
            if (cams.Count > 0)
            {
                camera = cams[0].Name;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("camera probe: " + Log.Describe(ex));
        }

        var report = new LidHardwareReport(
            hinge is not null,
            inclinometer is not null,
            accelerometer is not null,
            orientation is not null,
            light is not null,
            camera);
        Log.Info(
            $"lid probe hinge={report.Hinge} inclinometer={report.Inclinometer} " +
            $"accel={report.Accelerometer} ori={report.Orientation} light={report.Light} camera={camera}");
        return report;
    }
}
