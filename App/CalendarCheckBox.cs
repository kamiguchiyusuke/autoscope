using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArmadaAnalyzer.Desktop;

// Numeric calendar choices retain CheckBox input and automation, without 60 repeated template trees.
internal sealed class CalendarCheckBox : CheckBox
{
    private FormattedText? label;
    private string labelText = "";
    private double labelDpi, labelFontSize;
    private Brush? labelBrush;
    private Typeface? labelTypeface;
    private FlowDirection labelDirection;
    private static readonly Style EmptyStyle = CreateEmptyStyle();
    private static readonly Geometry Tick = CreateTick();
    static CalendarCheckBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CalendarCheckBox), new FrameworkPropertyMetadata(typeof(CalendarCheckBox)));
        IsCheckedProperty.OverrideMetadata(typeof(CalendarCheckBox), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        IsEnabledProperty.OverrideMetadata(typeof(CalendarCheckBox), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
        ForegroundProperty.OverrideMetadata(typeof(CalendarCheckBox), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    }
    internal CalendarCheckBox()
    {
        // Explicit empty style prevents the implicit CheckBox template from being applied.
        Style = EmptyStyle;
        OverridesDefaultStyle = true;
        FocusVisualStyle = null;
        SetResourceReference(ForegroundProperty, "Ink");
    }
    private static Style CreateEmptyStyle() { var style = new Style(typeof(CalendarCheckBox)); style.Seal(); return style; }
    private static Geometry CreateTick()
    {
        var geometry = Geometry.Parse("M2,7 L6,11 L13,3"); geometry.Freeze(); return geometry;
    }
    private FormattedText Label()
    {
        string text = Content?.ToString() ?? "";
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        if (label == null || !Equals(labelTypeface, face) || labelDirection != FlowDirection || labelText != text || labelDpi != dpi || labelBrush != Foreground || labelFontSize != FontSize)
        {
            labelText = text; labelDpi = dpi; labelBrush = Foreground; labelFontSize = FontSize; labelTypeface = face; labelDirection = FlowDirection;
            label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection,
                face, FontSize, Foreground, dpi);
        }
        return label;
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsKeyboardFocusedProperty) InvalidateVisual();
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var text = Label(); return new Size(25 + text.WidthIncludingTrailingWhitespace, Math.Max(16, text.Height));
    }
    protected override void OnRender(DrawingContext drawing)
    {
        var text = Label(); double y = (ActualHeight - 16) / 2;
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (!IsEnabled) drawing.PushOpacity(0.45);
        var outline = new Pen(UiTheme.Get(IsChecked == true ? "Accent" : "Outline"), 1);
        drawing.DrawRoundedRectangle(UiTheme.Get(IsChecked == true ? "Accent" : "Raised"), outline, new Rect(1, y, 16, 16), 4, 4);
        if (IsChecked == true)
        {
            drawing.PushTransform(new TranslateTransform(1, y));
            drawing.DrawGeometry(null, new Pen(UiTheme.Get("Surface"), 2), Tick);
            drawing.Pop();
        }
        drawing.DrawText(text, new Point(25, (ActualHeight - text.Height) / 2));
        if (IsKeyboardFocused)
            drawing.DrawRoundedRectangle(null, new Pen(UiTheme.Get("Accent"), 1), new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)), 3, 3);
        if (!IsEnabled) drawing.Pop();
    }
}
