using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig.Wpf;

namespace SNChat.App.Behaviors;

/// <summary>
/// Adds a copy button to every fenced code block rendered by a MarkdownViewer.
///
/// Markdig emits a code block as a plain Paragraph with no marker of its own, so
/// there is no renderer hook to attach a button to. Rather than replace Markdig's
/// pipeline - which would also mean rebuilding the image and hyperlink handling -
/// this walks the finished document and swaps each code paragraph for a bordered
/// panel containing the same text plus a copy button.
///
/// Post-processing in place matters: the document is still the one MarkdownViewer
/// built, so its own styles for images and links remain applied.
/// </summary>
public static class CodeBlockCopyBehavior
{
    /// <summary>Set to true on a MarkdownViewer to enable the buttons.</summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(CodeBlockCopyBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownViewer viewer)
            return;

        var descriptor = DependencyPropertyDescriptor.FromProperty(
            MarkdownViewer.DocumentProperty, typeof(MarkdownViewer));

        if ((bool)e.NewValue)
        {
            descriptor.AddValueChanged(viewer, OnDocumentChanged);
            Decorate(viewer.Document);
        }
        else
        {
            descriptor.RemoveValueChanged(viewer, OnDocumentChanged);
        }
    }

    private static void OnDocumentChanged(object? sender, EventArgs e)
    {
        if (sender is MarkdownViewer viewer)
            Decorate(viewer.Document);
    }

    private static void Decorate(FlowDocument? document)
    {
        if (document == null)
            return;

        // Collect first: the collection cannot be modified while enumerating.
        var codeBlocks = document.Blocks.OfType<Paragraph>().Where(IsCodeBlock).ToList();

        foreach (var paragraph in codeBlocks)
        {
            var code = ExtractText(paragraph);
            if (string.IsNullOrWhiteSpace(code))
                continue;

            document.Blocks.InsertAfter(paragraph, BuildCodePanel(code));
            document.Blocks.Remove(paragraph);
        }
    }

    /// <summary>
    /// Code paragraphs are the ones Markdig gives a monospace font and an
    /// explicit style; ordinary prose has neither.
    /// </summary>
    private static bool IsCodeBlock(Paragraph paragraph)
    {
        if (paragraph.Style == null)
            return false;

        var family = paragraph.FontFamily?.Source;
        return family != null &&
               (family.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
                family.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
                family.Contains("Typewriter", StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractText(Paragraph paragraph) =>
        new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd();

    private static BlockUIContainer BuildCodePanel(string code)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Wrapping rather than horizontal scrolling: a nested scroll viewer here
        // would swallow the mouse wheel the same way the message viewers do.
        var text = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x24, 0x29, 0x2E)),
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(text, 0);

        var button = new Button
        {
            Content = "Copy",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Copy this code block",
            Opacity = 0.55
        };
        Grid.SetColumn(button, 1);

        button.Click += (_, _) => CopyToClipboard(code, button);

        grid.Children.Add(text);
        grid.Children.Add(button);

        // Full opacity only while the block is hovered, to keep it unobtrusive.
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF8, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE4, 0xE8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 8, 8),
            Child = grid
        };

        border.MouseEnter += (_, _) => button.Opacity = 1.0;
        border.MouseLeave += (_, _) => button.Opacity = 0.55;

        return new BlockUIContainer(border) { Margin = new Thickness(0, 6, 0, 6) };
    }

    private static void CopyToClipboard(string code, Button button)
    {
        try
        {
            Clipboard.SetText(code);

            // Brief inline confirmation; avoids a modal for a trivial action.
            button.Content = "Copied";
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            timer.Tick += (s, _) =>
            {
                button.Content = "Copy";
                timer.Stop();
            };
            timer.Start();
        }
        catch (Exception)
        {
            // The clipboard is regularly locked by other processes; a failed
            // copy should not take down the chat view.
            button.Content = "Failed";
        }
    }
}
