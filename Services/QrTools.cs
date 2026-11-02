using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QRCoder;
using ZXing;
using ZXing.Common;

namespace EveryDns.Services;

/// <summary>Draws a share link as a QR code, and finds QR codes on the screen.</summary>
public static class QrTools
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    /// <summary>A PNG of the QR code for this text.</summary>
    public static byte[] ToPng(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(10);
    }

    /// <summary>Takes a picture of every monitor and returns the text of each QR code in it.</summary>
    public static List<string> ScanScreen()
    {
        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN), y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) return new List<string>();

        using var shot = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(x, y, 0, 0, shot.Size);
        return Decode(shot);
    }

    public static List<string> Decode(Bitmap image)
    {
        var bits = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[Math.Abs(bits.Stride) * bits.Height];
            Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
            var source = new RGBLuminanceSource(pixels, bits.Width, bits.Height, RGBLuminanceSource.BitmapFormat.BGRA32);

            var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
            var found = reader.DecodeMultiple(source) ?? Array.Empty<Result>();
            return found.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
        }
        finally { image.UnlockBits(bits); }
    }
}
