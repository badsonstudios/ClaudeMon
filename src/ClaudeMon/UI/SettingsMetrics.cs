namespace ClaudeMon.UI;

/// <summary>
/// DPI-scaled layout values for <see cref="SettingsForm"/>. Every dimension is derived from a
/// base (96-DPI) constant multiplied by the current DPI scale — the same approach as
/// <see cref="FlyoutMetrics"/> — so the hand-laid settings layout keeps its proportions at any
/// display scaling instead of cramming DPI-scaled fonts into fixed-pixel rows.
///
/// This type is pure (no UI dependency) so the layout math is unit-testable.
/// </summary>
public sealed class SettingsMetrics
{
    // Base values at 96 DPI (the original hand-tuned pixel layout).
    internal const int BaseWindowWidth = 480;
    internal const int BasePad = 24;
    internal const int BaseTopMargin = 6;
    internal const int BaseControlLeft = 250;
    internal const int BaseNumericWidth = 64;
    internal const int BaseToggleWidth = 40;
    internal const int BaseToggleHeight = 20;
    internal const int BaseIndent = 16;
    internal const int BaseRowHeight = 34;
    internal const int BaseHeaderHeight = 44;
    internal const int BaseHeaderTextOffset = 16;
    internal const int BaseHeaderDividerOffset = 38;
    internal const int BaseToggleLabelOffset = 8;
    internal const int BaseToggleOffset = 7;
    internal const int BaseComboLabelOffset = 6;
    internal const int BaseComboOffset = 3;
    internal const int BaseSuffixGap = 6;
    internal const int BaseButtonWidth = 82;
    internal const int BaseButtonHeight = 30;
    internal const int BaseOkButtonRightOffset = 174;
    internal const int BaseCancelButtonRightOffset = 84;
    internal const int BaseButtonTopGap = 14;

    /// <summary>Fixed dialog width.</summary>
    public int WindowWidth { get; }

    /// <summary>Left/right/bottom content margin.</summary>
    public int Pad { get; }

    /// <summary>Top margin above the first section header.</summary>
    public int TopMargin { get; }

    /// <summary>Right edge of the content column (<see cref="WindowWidth"/> − <see cref="Pad"/>).</summary>
    public int ContentRight { get; }

    /// <summary>Left edge of the right-aligned control column.</summary>
    public int ControlLeft { get; }

    /// <summary>Combo width filling the control column to <see cref="ContentRight"/>.</summary>
    public int ComboWidth { get; }

    public int NumericWidth { get; }
    public int ToggleWidth { get; }
    public int ToggleHeight { get; }

    /// <summary>Extra left inset for sub-option rows.</summary>
    public int Indent { get; }

    /// <summary>Height of a control row (toggle / combo / numeric).</summary>
    public int RowHeight { get; }

    /// <summary>Height of a section-header row (title + divider).</summary>
    public int HeaderHeight { get; }

    /// <summary>Y offset of the header title within its row.</summary>
    public int HeaderTextOffset { get; }

    /// <summary>Y offset of the hairline divider within the header row.</summary>
    public int HeaderDividerOffset { get; }

    /// <summary>Divider thickness (never rounds below one pixel).</summary>
    public int DividerHeight { get; }

    /// <summary>Y offset of a toggle row's label within the row.</summary>
    public int ToggleLabelOffset { get; }

    /// <summary>Y offset of the toggle switch within the row.</summary>
    public int ToggleOffset { get; }

    /// <summary>Y offset of a combo/numeric row's label within the row.</summary>
    public int ComboLabelOffset { get; }

    /// <summary>Y offset of the combo/numeric control within the row.</summary>
    public int ComboOffset { get; }

    /// <summary>Gap between a numeric control and its suffix label (e.g. "%").</summary>
    public int SuffixGap { get; }

    public int ButtonWidth { get; }
    public int ButtonHeight { get; }

    /// <summary>OK button's left edge, measured back from <see cref="ContentRight"/>.</summary>
    public int OkButtonRightOffset { get; }

    /// <summary>Cancel button's left edge, measured back from <see cref="ContentRight"/>.</summary>
    public int CancelButtonRightOffset { get; }

    /// <summary>Vertical gap between the last row and the button row.</summary>
    public int ButtonTopGap { get; }

    private SettingsMetrics(int dpi)
    {
        var scale = dpi / 96f;
        int S(int baseValue) => (int)Math.Round(baseValue * scale, MidpointRounding.AwayFromZero);

        WindowWidth = S(BaseWindowWidth);
        Pad = S(BasePad);
        TopMargin = S(BaseTopMargin);
        ContentRight = WindowWidth - Pad;
        ControlLeft = S(BaseControlLeft);
        ComboWidth = ContentRight - ControlLeft;
        NumericWidth = S(BaseNumericWidth);
        ToggleWidth = S(BaseToggleWidth);
        ToggleHeight = S(BaseToggleHeight);
        Indent = S(BaseIndent);
        RowHeight = S(BaseRowHeight);
        HeaderHeight = S(BaseHeaderHeight);
        HeaderTextOffset = S(BaseHeaderTextOffset);
        HeaderDividerOffset = S(BaseHeaderDividerOffset);
        DividerHeight = Math.Max(1, S(1));
        ToggleLabelOffset = S(BaseToggleLabelOffset);
        ToggleOffset = S(BaseToggleOffset);
        ComboLabelOffset = S(BaseComboLabelOffset);
        ComboOffset = S(BaseComboOffset);
        SuffixGap = S(BaseSuffixGap);
        ButtonWidth = S(BaseButtonWidth);
        ButtonHeight = S(BaseButtonHeight);
        OkButtonRightOffset = S(BaseOkButtonRightOffset);
        CancelButtonRightOffset = S(BaseCancelButtonRightOffset);
        ButtonTopGap = S(BaseButtonTopGap);
    }

    /// <summary>Builds the scaled metrics for the given device DPI (96 = 100%).</summary>
    public static SettingsMetrics ForDpi(int dpi) => new(dpi <= 0 ? 96 : dpi);
}
