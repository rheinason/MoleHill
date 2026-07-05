using System;

namespace MoleHill.Rhino.UI;

internal static class UiMetrics
{
    private const float Ch = 7f;

    public static void Invalidate()
    {
    }

    public static int Chs(double count) => (int)Math.Ceiling(count * Ch);

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
