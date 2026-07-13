using System;
using Eto.Drawing;

namespace MoleHill.Rhino.UI;

internal static class UiMetrics
{
    private static float _characterWidth = -1f;

    private static float CharacterWidth
    {
        get
        {
            if (_characterWidth >= 0f)
                return _characterWidth;

            using var font = new Font(SystemFont.Bold);
            float measured = font.MeasureString("0000000000").Width / 10f;
            _characterWidth = measured > 0f ? measured : 7f;
            return _characterWidth;
        }
    }

    /// <summary>Call when Rhino's UI font or theme may have changed.</summary>
    public static void Invalidate() => _characterWidth = -1f;

    public static int Chs(double count) => (int)Math.Ceiling(count * CharacterWidth);

    public static int LabelColumn => Chs(10);

    public static int ShortLabel => Chs(8);

    public static int NumericField => Chs(9);

    public static int DropDown => Chs(13);

    public static int SliderMin => Chs(10);

    public static int SliderText => Chs(8);

    public static int RowBreak => Chs(42);

    public static int ControlGroupBreak => Chs(44);

    public static int HeaderStatusBreak => Chs(30);

    public static int Hysteresis => Chs(1);
}
