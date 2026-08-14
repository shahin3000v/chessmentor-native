using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChessMentor.Viewer;

namespace ChessMentor.Desktop.Controls;

/// <summary>
/// Native equivalent of the current viewer's nested «پاسخ:» disclosure.
/// Every following answer lives inside the previous answer's remainder, so it
/// cannot be revealed out of order.
/// </summary>
public sealed class ProgressiveCommentControl : StackPanel
{
    public static readonly DependencyProperty MixedTextProperty = DependencyProperty.Register(
        nameof(MixedText),
        typeof(string),
        typeof(ProgressiveCommentControl),
        new FrameworkPropertyMetadata(string.Empty, OnPresentationChanged));

    public static readonly DependencyProperty CommentFontSizeProperty = DependencyProperty.Register(
        nameof(CommentFontSize),
        typeof(double),
        typeof(ProgressiveCommentControl),
        new FrameworkPropertyMetadata(14d, OnPresentationChanged));

    public static readonly DependencyProperty CommentFontFamilyProperty = DependencyProperty.Register(
        nameof(CommentFontFamily),
        typeof(FontFamily),
        typeof(ProgressiveCommentControl),
        new FrameworkPropertyMetadata(new FontFamily("Tahoma, Segoe UI"), OnPresentationChanged));

    public ProgressiveCommentControl()
    {
        FlowDirection = FlowDirection.RightToLeft;
    }

    public string MixedText
    {
        get => (string)GetValue(MixedTextProperty);
        set => SetValue(MixedTextProperty, value);
    }

    public double CommentFontSize
    {
        get => (double)GetValue(CommentFontSizeProperty);
        set => SetValue(CommentFontSizeProperty, value);
    }

    public FontFamily CommentFontFamily
    {
        get => (FontFamily)GetValue(CommentFontFamilyProperty);
        set => SetValue(CommentFontFamilyProperty, value);
    }

    private static void OnPresentationChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs) =>
        ((ProgressiveCommentControl)dependencyObject).Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        var parsed = ProgressiveAnswerParser.Parse(MixedText);
        if (!string.IsNullOrEmpty(parsed.Before))
        {
            Children.Add(CreateText(parsed.Before));
        }

        if (parsed.Sections.Count > 0)
        {
            AddSection(Children, parsed.Sections, 0);
        }
    }

    private void AddSection(
        UIElementCollection parent,
        IReadOnlyList<ProgressiveAnswerSection> sections,
        int index)
    {
        var section = sections[index];
        var button = new Button
        {
            Content = section.Label,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 2),
            Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(Color.FromRgb(254, 243, 199)),
            Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14)),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.Bold,
        };
        var remainder = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Thickness(7, 0, 7, 0),
        };
        if (!string.IsNullOrEmpty(section.Content))
        {
            remainder.Children.Add(CreateText(section.Content));
        }

        if (index + 1 < sections.Count)
        {
            AddSection(remainder.Children, sections, index + 1);
        }

        button.Click += (_, _) =>
        {
            var reveal = remainder.Visibility != Visibility.Visible;
            remainder.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
            button.Background = new SolidColorBrush(reveal
                ? Color.FromRgb(220, 252, 231)
                : Color.FromRgb(254, 243, 199));
            button.Foreground = new SolidColorBrush(reveal
                ? Color.FromRgb(22, 101, 52)
                : Color.FromRgb(146, 64, 14));
        };
        parent.Add(button);
        parent.Add(remainder);
    }

    private MixedDirectionTextBlock CreateText(string text) => new()
    {
        MixedText = text,
        FontSize = CommentFontSize,
        FontFamily = CommentFontFamily,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Right,
        LineHeight = Math.Max(22, CommentFontSize * 1.75),
    };
}
