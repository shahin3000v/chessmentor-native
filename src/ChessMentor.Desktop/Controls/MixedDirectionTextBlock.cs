using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace ChessMentor.Desktop.Controls;

/// <summary>
/// Native WPF Persian text surface with physical right alignment and explicit
/// LTR islands for SAN/UCI. The element coordinate system stays LTR so WPF does
/// not mirror TextAlignment.Right; an RTL Span owns the paragraph direction.
/// </summary>
public sealed partial class MixedDirectionTextBlock : TextBlock
{
    public static readonly DependencyProperty MixedTextProperty = DependencyProperty.Register(
        nameof(MixedText),
        typeof(string),
        typeof(MixedDirectionTextBlock),
        new FrameworkPropertyMetadata(string.Empty, OnMixedTextChanged));

    public MixedDirectionTextBlock()
    {
        FlowDirection = FlowDirection.LeftToRight;
        TextAlignment = TextAlignment.Right;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        TextWrapping = TextWrapping.Wrap;
        Language = XmlLanguage.GetLanguage("fa-IR");
    }

    public string MixedText
    {
        get => (string)GetValue(MixedTextProperty);
        set => SetValue(MixedTextProperty, value);
    }

    private static void OnMixedTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs) =>
        ((MixedDirectionTextBlock)dependencyObject).RenderMixedText(
            eventArgs.NewValue as string ?? string.Empty);

    private void RenderMixedText(string text)
    {
        Inlines.Clear();
        var paragraph = new Span
        {
            FlowDirection = FlowDirection.RightToLeft,
        };
        Inlines.Add(paragraph);
        var cursor = 0;
        foreach (Match match in ChessTokenRegex().Matches(text))
        {
            if (match.Index > cursor)
            {
                paragraph.Inlines.Add(new Run(text[cursor..match.Index])
                {
                    FlowDirection = FlowDirection.RightToLeft,
                });
            }

            paragraph.Inlines.Add(new Run(match.Value)
            {
                FlowDirection = FlowDirection.LeftToRight,
                FontFamily = new FontFamily("Consolas, Segoe UI Symbol"),
            });
            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[cursor..])
            {
                FlowDirection = FlowDirection.RightToLeft,
            });
        }
    }

    [GeneratedRegex(
        "(?<![\\p{L}\\p{N}])(?:(?:[0-9]{1,3})(?:\\.\\.\\.|\\.)\\s*)?(?:O-O-O|O-O|0-0-0|0-0|[KQRBN♔♕♖♗♘♙♚♛♜♝♞♟]?[a-h]?[1-8]?(?:x|-)?[a-h][1-8](?:=[QRBN♕♖♗♘♛♜♝♞])?[+#]?[!?]{0,2})(?![\\p{L}\\p{N}])",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChessTokenRegex();
}
