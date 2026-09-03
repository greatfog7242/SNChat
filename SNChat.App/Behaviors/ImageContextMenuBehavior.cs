using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig.Wpf;
using Microsoft.Win32;

namespace SNChat.App.Behaviors;

/// <summary>
/// Gives every picture in a rendered message a right-click menu offering to save
/// or copy it.
///
/// Markdig renders an image as a Button inside an InlineUIContainer and puts the
/// image's address in the Button's CommandParameter. Setting a ContextMenu on
/// that Button is not enough: the document sits in a viewer that shows its own
/// "Copy / Select All" text menu, and that wins, so the image menu never appears.
///
/// So the context-menu event is intercepted on the viewer instead. When the click
/// lands on or inside an image's Button the text menu is suppressed and the image
/// menu opened in its place; anywhere else the text menu is left alone, because
/// selecting and copying a reply is still worth having.
/// </summary>
public static class ImageContextMenuBehavior
{
    /// <summary>
    /// Shared deliberately; one handler serves every message view. It carries a
    /// User-Agent because several image hosts, Wikimedia among them, answer 403
    /// to a request without one.
    /// </summary>
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "SNChat/1.0" } }
    };

    /// <summary>Set to true on a MarkdownViewer to enable the menu.</summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(ImageContextMenuBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MarkdownViewer viewer)
            return;

        if ((bool)e.NewValue)
        {
            // The text menu is put up by the viewer's own editor, which sits
            // below this control and so would see a bubbling event first. This
            // one tunnels, reaching here before the editor, and marking it
            // handled stops the text menu being raised at all.
            //
            // PreviewMouseUp rather than PreviewMouseRightButtonUp: the latter
            // is a Direct event, so a handler here would never run for a click
            // that landed on a descendant, which is every click that matters.
            viewer.AddHandler(
                UIElement.PreviewMouseUpEvent,
                new MouseButtonEventHandler(OnPreviewMouseUp));

            // Belt and braces, for any path that still reaches the menu stage:
            // handledEventsToo, because the editor marks it handled itself.
            viewer.AddHandler(
                FrameworkElement.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(OnContextMenuOpening),
                handledEventsToo: true);
        }
        else
        {
            viewer.RemoveHandler(
                UIElement.PreviewMouseUpEvent,
                new MouseButtonEventHandler(OnPreviewMouseUp));

            viewer.RemoveHandler(
                FrameworkElement.ContextMenuOpeningEvent,
                new ContextMenuEventHandler(OnContextMenuOpening));
        }
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || sender is not MarkdownViewer viewer)
            return;

        if (!TryResolveImageAt(viewer, e.GetPosition(viewer), out var button, out var uri))
            return;

        // Stops the click ever becoming a request for the text menu.
        e.Handled = true;
        Show(button, uri);
    }

    private static void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not MarkdownViewer viewer)
            return;

        if (!TryResolveImageAt(viewer, Mouse.GetPosition(viewer), out var button, out var uri))
            return;

        e.Handled = true;

        // Only if the click above did not already put the menu up.
        if (button.ContextMenu is not { IsOpen: true })
            Show(button, uri);
    }

    /// <summary>
    /// Finds the image under a point, if there is one.
    ///
    /// The point has to be hit-tested rather than taken from the event's source:
    /// a click over a picture reports its source as the enclosing Paragraph, and
    /// that leads up through the document to the viewer without ever passing the
    /// image's Button. Hit-testing the visual tree returns the Image itself.
    /// </summary>
    private static bool TryResolveImageAt(
        MarkdownViewer viewer, Point position, out Button button, out Uri uri)
    {
        button = null!;
        uri = null!;

        var hit = VisualTreeHelper.HitTest(viewer, position)?.VisualHit;

        return hit != null && TryResolveImage(hit, out button, out uri);
    }

    private static void Show(Button button, Uri uri)
    {
        // The picture is already decoded on screen, so its pixels are available
        // without going back to the network. That matters for hosts that refuse
        // a second request, which is most of them once hotlink protection or a
        // one-off signed URL is involved.
        var menu = BuildMenu(uri, FindRenderedImage(button));

        // Held on the button so a second right-click can tell an already-open
        // menu from a fresh one.
        button.ContextMenu = menu;

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Walks up from whatever the mouse was over - usually the Image inside the
    /// Button's template - to the image Button and its address. Returns false for
    /// a click on ordinary text, and for a link target Markdig could not parse,
    /// which it reports as "#".
    /// </summary>
    internal static bool TryResolveImage(
        DependencyObject? source, out Button button, out Uri uri)
    {
        button = null!;
        uri = null!;

        var current = source;

        while (current != null)
        {
            if (current is Button candidate)
            {
                var address = candidate.CommandParameter?.ToString();

                if (!string.IsNullOrWhiteSpace(address) &&
                    address != "#" &&
                    Uri.TryCreate(address, UriKind.Absolute, out var parsed))
                {
                    button = candidate;
                    uri = parsed;
                    return true;
                }

                return false;
            }

            // Embedded images sit in the visual tree, but the walk can start on a
            // text element that only has a logical parent, so both are followed.
            current = current is Visual
                ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static ContextMenu BuildMenu(Uri uri, BitmapSource? rendered)
    {
        var menu = new ContextMenu();

        var save = new MenuItem { Header = "Save image as..." };
        save.Click += (_, _) => SaveAs(uri, rendered);

        var copy = new MenuItem { Header = "Copy image" };
        copy.Click += (_, _) => CopyImage(uri, rendered);

        var copyAddress = new MenuItem { Header = "Copy image address" };
        copyAddress.Click += (_, _) => TrySetClipboard(() =>
            Clipboard.SetText(uri.IsFile ? uri.LocalPath : uri.AbsoluteUri));

        menu.Items.Add(save);
        menu.Items.Add(copy);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyAddress);

        return menu;
    }

    private static async void SaveAs(Uri uri, BitmapSource? rendered)
    {
        var suggested = SuggestFileName(uri);

        var dialog = new SaveFileDialog
        {
            FileName = suggested,
            DefaultExt = Path.GetExtension(suggested),
            Filter = BuildFilter(Path.GetExtension(suggested)),
            Title = "Save image"
        };

        if (dialog.ShowDialog() != true)
            return;

        // Copying the cached file keeps the original bytes exactly, so it is
        // preferred over re-encoding whenever the picture is already on disk.
        if (uri.IsFile)
        {
            try
            {
                File.Copy(uri.LocalPath, dialog.FileName, overwrite: true);
                return;
            }
            catch (Exception ex)
            {
                Complain("save", ex.Message);
                return;
            }
        }

        // Fetching gives the original file rather than a re-encode, so it is
        // tried first, but it is the part that fails: hosts refuse hotlinked
        // requests, and signed URLs from a search result expire quickly.
        string? failure = null;

        try
        {
            var bytes = await HttpClient.GetByteArrayAsync(uri);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            return;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        if (rendered == null)
        {
            Complain("save", failure);
            return;
        }

        // The picture is on screen, so it can be written from the pixels already
        // decoded. Re-encoded rather than byte-identical, which is a fair trade
        // for saving a picture that otherwise could not be saved at all.
        try
        {
            Encode(rendered, dialog.FileName);
        }
        catch (Exception ex)
        {
            Complain("save", ex.Message);
        }
    }

    private static void CopyImage(Uri uri, BitmapSource? rendered)
    {
        if (rendered != null)
        {
            TrySetClipboard(() => Clipboard.SetImage(rendered));
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            TrySetClipboard(() => Clipboard.SetImage(bitmap));
        }
        catch (Exception ex)
        {
            Complain("copy", ex.Message);
        }
    }

    /// <summary>Writes a decoded picture out, in the format the name asks for.</summary>
    private static void Encode(BitmapSource source, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".gif" => new GifBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>The Image inside the picture's Button, once it has been drawn.</summary>
    private static BitmapSource? FindRenderedImage(DependencyObject root)
    {
        if (root is Image { Source: BitmapSource bitmap })
            return bitmap;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindRenderedImage(VisualTreeHelper.GetChild(root, i));
            if (found != null)
                return found;
        }

        return null;
    }

    private static void Complain(string verb, string? detail) =>
        MessageBox.Show(
            $"Could not {verb} the image.\n\n{detail}",
            $"{char.ToUpperInvariant(verb[0])}{verb[1..]} failed",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    /// <summary>
    /// The clipboard is regularly locked by another process, and the exception
    /// that causes must not take down the chat view.
    /// </summary>
    private static void TrySetClipboard(Action set)
    {
        try
        {
            set();
        }
        catch (Exception)
        {
            // Nothing useful to do; the user can simply try again.
        }
    }

    /// <summary>
    /// Cached pictures are named after a hash of their URL, which is meaningless
    /// to a person, so those get a plainer default. Anything else keeps the name
    /// it had, minus characters a filename cannot hold.
    /// </summary>
    internal static string SuggestFileName(Uri uri)
    {
        var name = Path.GetFileName(
            uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath));

        var extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension))
            extension = ".jpg";

        if (string.IsNullOrWhiteSpace(name) ||
            Path.GetFileNameWithoutExtension(name).StartsWith("web-", StringComparison.Ordinal))
        {
            return $"image{extension}";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return name;
    }

    private static string BuildFilter(string extension)
    {
        var label = extension.TrimStart('.').ToUpperInvariant();

        return string.IsNullOrEmpty(label)
            ? "All files|*.*"
            : $"{label} image|*{extension}|All files|*.*";
    }
}
