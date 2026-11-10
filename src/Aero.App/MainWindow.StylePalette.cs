using Windows.UI;

namespace Aero.App;

// Material 3 colour roles generated from the accent colour. Tones are CIELAB lightness (L*),
// which is what Material's HCT "tone" is; hue comes from the accent and chroma is fixed per
// palette, close to the Expressive scheme (tinted neutrals, livelier containers).
public sealed partial class MainWindow
{
    private sealed record MaterialScheme(
        Color Surface,
        Color ContainerLowest,
        Color ContainerLow,
        Color Container,
        Color ContainerHigh,
        Color ContainerHighest,
        Color OnSurface,
        Color OnSurfaceVariant,
        Color Outline,
        Color OutlineVariant,
        Color Primary,
        Color OnPrimary,
        Color PrimaryContainer,
        Color OnPrimaryContainer,
        Color SecondaryContainer,
        Color OnSecondaryContainer,
        Color TertiaryContainer,
        Color OnTertiaryContainer,
        Color InverseSurface,
        Color InverseOnSurface);

    private static MaterialScheme BuildMaterialScheme(Color seed, bool dark)
    {
        (_, double seedChroma, double hue) = ToLch(seed);
        double primaryChroma = Math.Clamp(seedChroma, 48, 72);
        Color P(double tone) => FromLch(tone, primaryChroma, hue);
        Color S(double tone) => FromLch(tone, 24, hue);
        Color T(double tone) => FromLch(tone, 40, hue + 60);
        Color N(double tone) => FromLch(tone, 6, hue);
        Color NV(double tone) => FromLch(tone, 12, hue);

        return dark
            ? new MaterialScheme(
                N(6), N(4), N(10), N(12), N(17), N(22), N(90), NV(80), NV(60), NV(30),
                P(80), P(20), P(30), P(90), S(30), S(90), T(30), T(90), N(90), N(20))
            : new MaterialScheme(
                N(98), N(100), N(96), N(94), N(92), N(90), N(10), NV(30), NV(50), NV(80),
                P(40), P(100), P(90), P(10), S(90), S(10), T(90), T(10), N(20), N(95));
    }

    private static (double L, double C, double H) ToLch(Color color)
    {
        static double Linear(byte channel)
        {
            double v = channel / 255d;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        double r = Linear(color.R), g = Linear(color.G), b = Linear(color.B);
        double x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
        static double F(double t) => t > 216d / 24389 ? Math.Cbrt(t) : (24389d / 27 * t + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        double l = 116 * fy - 16, a = 500 * (fx - fy), bb = 200 * (fy - fz);
        double h = Math.Atan2(bb, a) * 180 / Math.PI;
        return (l, Math.Sqrt(a * a + bb * bb), h < 0 ? h + 360 : h);
    }

    // Lowers chroma until the colour fits sRGB, the way tonal palettes keep their tone exact.
    private static Color FromLch(double tone, double chroma, double hue)
    {
        double radians = hue * Math.PI / 180;
        for (double c = chroma; c >= 0; c -= 0.5)
        {
            if (TryLabToSrgb(tone, c * Math.Cos(radians), c * Math.Sin(radians), out Color color))
            {
                return color;
            }
        }

        TryLabToSrgb(tone, 0, 0, out Color gray);
        return gray;
    }

    private static bool TryLabToSrgb(double l, double a, double b, out Color color)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double Inverse(double t) => t * t * t > 216d / 24389 ? t * t * t : (116 * t - 16) / (24389d / 27);
        double x = 0.95047 * Inverse(fx);
        double y = l > 8 ? fy * fy * fy : l / (24389d / 27);
        double z = 1.08883 * Inverse(fz);
        double[] rgb =
        {
            3.2404542 * x - 1.5371385 * y - 0.4985314 * z,
            -0.9692660 * x + 1.8760108 * y + 0.0415560 * z,
            0.0556434 * x - 0.2040259 * y + 1.0572252 * z
        };

        color = default;
        byte[] channels = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            if (rgb[i] < -0.001 || rgb[i] > 1.001)
            {
                return false;
            }

            double v = Math.Clamp(rgb[i], 0, 1);
            v = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            channels[i] = (byte)Math.Round(v * 255);
        }

        color = Color.FromArgb(255, channels[0], channels[1], channels[2]);
        return true;
    }

    private static string Css(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"rgba({color.R},{color.G},{color.B},{(color.A / 255d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
}
