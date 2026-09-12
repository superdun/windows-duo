using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace WindowsDuo;

/// <summary>
/// DXGI duplication cannot see the taskbar once a fullscreen overlay covers it,
/// because DWM stops painting the occluded tray. PrintWindow still can.
/// </summary>
internal static class TaskbarCapture
{
    public static void Blit(
        ID3D11DeviceContext context,
        ID3D11Texture2D picture,
        int padding,
        Rectangle desktop)
    {
        BlitClass(context, picture, padding, desktop, "Shell_TrayWnd");
        BlitClass(context, picture, padding, desktop, "Shell_SecondaryTrayWnd");
    }

    private static void BlitClass(
        ID3D11DeviceContext context,
        ID3D11Texture2D picture,
        int padding,
        Rectangle desktop,
        string className)
    {
        Native.EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(64);
            if (Native.GetClassNameW(hwnd, name, name.Capacity) > 0
                && string.Equals(name.ToString(), className, StringComparison.Ordinal))
            {
                BlitWindow(context, picture, padding, desktop, hwnd);
            }

            return true;
        }, 0);
    }

    private static void BlitWindow(
        ID3D11DeviceContext context,
        ID3D11Texture2D picture,
        int padding,
        Rectangle desktop,
        nint hwnd)
    {
        if (hwnd == 0 || !Native.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var dest = Rectangle.Intersect(
            new Rectangle(rect.Left, rect.Top, width, height),
            desktop);
        if (dest.Width <= 0 || dest.Height <= 0)
        {
            return;
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            var ok = Native.PrintWindow(hwnd, hdc, Native.PwRenderFullContent);
            graphics.ReleaseHdc(hdc);
            if (!ok)
            {
                return;
            }
        }

        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            if (IsMostlyBlack(data, width, height))
            {
                return;
            }

            var srcX = dest.X - rect.Left;
            var srcY = dest.Y - rect.Top;
            var dstLeft = padding + dest.X - desktop.X;
            var dstTop = padding + dest.Y - desktop.Y;
            var dstRight = dstLeft + dest.Width;
            var dstBottom = dstTop + dest.Height;
            var pictureDesc = picture.Description;
            if (dstLeft < 0 || dstTop < 0 || dstRight > pictureDesc.Width || dstBottom > pictureDesc.Height)
            {
                return;
            }

            var box = new Box(dstLeft, dstTop, 0, dstRight, dstBottom, 1);
            if (srcX == 0 && srcY == 0 && dest.Width == width)
            {
                context.UpdateSubresource(
                    new MappedSubresource(data.Scan0, (uint)data.Stride, 0),
                    picture,
                    0,
                    box);
                return;
            }

            var cropStride = dest.Width * 4;
            var cropped = new byte[cropStride * dest.Height];
            for (var y = 0; y < dest.Height; y++)
            {
                Marshal.Copy(
                    data.Scan0 + ((srcY + y) * data.Stride) + (srcX * 4),
                    cropped,
                    y * cropStride,
                    cropStride);
            }

            context.UpdateSubresource(cropped.AsSpan(), picture, 0, (uint)cropStride, 0, box);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static bool IsMostlyBlack(BitmapData data, int width, int height)
    {
        var lit = 0;
        var samples = 0;
        for (var y = 0; y < height; y += 4)
        {
            var row = data.Scan0 + (y * data.Stride);
            for (var x = 0; x < width; x += 8)
            {
                var b = Marshal.ReadByte(row, x * 4);
                var g = Marshal.ReadByte(row, (x * 4) + 1);
                var r = Marshal.ReadByte(row, (x * 4) + 2);
                samples++;
                if (b > 12 || g > 12 || r > 12)
                {
                    lit++;
                }
            }
        }

        return samples > 0 && lit * 10 < samples;
    }
}
