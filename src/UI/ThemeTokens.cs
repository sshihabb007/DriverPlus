using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DriverPlus.UI
{
    public static class ThemeTokens
    {
        public static readonly SolidColorBrush BgBase = FrozenBrush(0x0B, 0x0F, 0x17);
        public static readonly SolidColorBrush BgRail = FrozenBrush(0x0E, 0x14, 0x20);
        public static readonly SolidColorBrush BgSurface = FrozenBrush(0x11, 0x18, 0x27);
        public static readonly SolidColorBrush BgElevated = FrozenBrush(0x1E, 0x29, 0x3B);
        public static readonly SolidColorBrush BgHover = FrozenBrush(0x26, 0x34, 0x49);
        public static readonly SolidColorBrush BgSelected = FrozenBrushAlpha(0x38, 0x10, 0xB9, 0x81);

        public static readonly SolidColorBrush BorderSubtle = FrozenBrush(0x23, 0x2F, 0x42);
        public static readonly SolidColorBrush BorderAccent = FrozenBrush(0x10, 0xB9, 0x81);
        public static readonly SolidColorBrush BorderSky = FrozenBrush(0x38, 0xBD, 0xF8);

        public static readonly SolidColorBrush TextPrimary = FrozenBrush(0xF8, 0xFA, 0xFC);
        public static readonly SolidColorBrush TextSecondary = FrozenBrush(0xCB, 0xD5, 0xE1);
        public static readonly SolidColorBrush TextMuted = FrozenBrush(0x94, 0xA3, 0xB8);

        public static readonly SolidColorBrush Emerald = FrozenBrush(0x10, 0xB9, 0x81);
        public static readonly SolidColorBrush EmeraldStrong = FrozenBrush(0x05, 0x96, 0x69);
        public static readonly SolidColorBrush EmeraldText = FrozenBrush(0x34, 0xD3, 0x99);
        public static readonly SolidColorBrush EmeraldDim = FrozenBrushAlpha(0x29, 0x10, 0xB9, 0x81);

        public static readonly SolidColorBrush Sky = FrozenBrush(0x38, 0xBD, 0xF8);
        public static readonly SolidColorBrush SkyDim = FrozenBrushAlpha(0x26, 0x38, 0xBD, 0xF8);

        public static readonly SolidColorBrush Amber = FrozenBrush(0xFB, 0xBF, 0x24);
        public static readonly SolidColorBrush AmberDim = FrozenBrushAlpha(0x2B, 0xF5, 0x9E, 0x0B);

        public static readonly SolidColorBrush Rose = FrozenBrush(0xFB, 0x71, 0x85);
        public static readonly SolidColorBrush RoseDim = FrozenBrushAlpha(0x2E, 0xF4, 0x3F, 0x5E);

        public static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas");

        private static SolidColorBrush FrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush FrozenBrushAlpha(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        public static Border CreateCard(Thickness padding)
        {
            return new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = padding
            };
        }

        public static Button CreateButton(string label, bool isPrimary)
        {
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border), "BtnBorder");
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(14, 7, 14, 7));
            borderFactory.SetValue(Border.BackgroundProperty, isPrimary ? EmeraldStrong : BgElevated);
            borderFactory.SetValue(Border.BorderBrushProperty, isPrimary ? Emerald : BorderSubtle);

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);
            template.VisualTree = borderFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, isPrimary ? Emerald : BgHover, "BtnBorder"));
            hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, isPrimary ? EmeraldText : BorderSky, "BtnBorder"));
            template.Triggers.Add(hoverTrigger);

            var disabledTrigger = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabledTrigger);

            return new Button
            {
                Content = label,
                Template = template,
                Foreground = TextPrimary,
                FontFamily = UiFont,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        public static Button CreateNavButton(string iconGlyph, string label, bool selected)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = iconGlyph,
                Width = 22,
                FontSize = 13.5,
                Foreground = selected ? EmeraldText : TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = selected ? TextPrimary : TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            });

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border), "NavBorder");
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(12, 9, 12, 9));
            borderFactory.SetValue(Border.BackgroundProperty, selected ? EmeraldDim : Brushes.Transparent);
            borderFactory.SetValue(Border.BorderBrushProperty, selected ? BorderAccent : Brushes.Transparent);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(presenter);
            template.VisualTree = borderFactory;

            if (!selected)
            {
                var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Border.BackgroundProperty, BgElevated, "NavBorder"));
                template.Triggers.Add(hover);
            }

            return new Button
            {
                Content = row,
                Template = template,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };
        }

        public static Border CreateBadge(string text, SolidColorBrush bg, SolidColorBrush fg)
        {
            return new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = fg,
                    FontFamily = UiFont,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                }
            };
        }

        public static TextBox CreateStyledTextBox(string placeholderTooltip)
        {
            return new TextBox
            {
                Background = BgBase,
                Foreground = TextPrimary,
                CaretBrush = Sky,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6),
                FontFamily = UiFont,
                FontSize = 13,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = placeholderTooltip
            };
        }
    }
}
