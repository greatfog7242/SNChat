using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SNChat.App.ViewModels;
using SNChat.App.Views;

namespace SNChat.App.Behaviors;

/// <summary>
/// Lets conversations in the sidebar be picked in bunches and dragged onto a
/// group.
///
/// Selecting and dragging are handled together because they are one gesture:
/// whether a press turns into an open, a re-selection, or the start of a drag
/// is only known once the mouse either moves or comes back up. Splitting them
/// across XAML input bindings and a separate drag handler would mean each half
/// guessing what the other did.
/// </summary>
public static class ConversationDragDropBehavior
{
    /// <summary>
    /// Private to this app. The payload is a comma-joined list of conversation
    /// ids: a plain string travels through the drag plumbing without the
    /// serialization that arbitrary objects would need.
    /// </summary>
    private const string ConversationIdsFormat = "SNChat.ConversationIds";

    // One gesture is in flight at a time, so the press is tracked statically
    // rather than per item.
    private static Point _pressOrigin;
    private static ConversationInfo? _pressedConversation;
    private static bool _pressPending;

    #region Attached properties

    /// <summary>Set on a conversation row to make it selectable and draggable.</summary>
    public static readonly DependencyProperty IsConversationProperty =
        DependencyProperty.RegisterAttached(
            "IsConversation", typeof(bool), typeof(ConversationDragDropBehavior),
            new PropertyMetadata(false, OnIsConversationChanged));

    public static void SetIsConversation(DependencyObject element, bool value) =>
        element.SetValue(IsConversationProperty, value);

    public static bool GetIsConversation(DependencyObject element) =>
        (bool)element.GetValue(IsConversationProperty);

    /// <summary>Set on a group header to make it accept dropped conversations.</summary>
    public static readonly DependencyProperty IsGroupTargetProperty =
        DependencyProperty.RegisterAttached(
            "IsGroupTarget", typeof(bool), typeof(ConversationDragDropBehavior),
            new PropertyMetadata(false, OnIsGroupTargetChanged));

    public static void SetIsGroupTarget(DependencyObject element, bool value) =>
        element.SetValue(IsGroupTargetProperty, value);

    public static bool GetIsGroupTarget(DependencyObject element) =>
        (bool)element.GetValue(IsGroupTargetProperty);

    /// <summary>
    /// Set on the ungrouped heading. Dropping there is how a conversation gets
    /// back out of a group without hunting for a menu.
    /// </summary>
    public static readonly DependencyProperty IsUngroupedTargetProperty =
        DependencyProperty.RegisterAttached(
            "IsUngroupedTarget", typeof(bool), typeof(ConversationDragDropBehavior),
            new PropertyMetadata(false, OnIsUngroupedTargetChanged));

    public static void SetIsUngroupedTarget(DependencyObject element, bool value) =>
        element.SetValue(IsUngroupedTargetProperty, value);

    public static bool GetIsUngroupedTarget(DependencyObject element) =>
        (bool)element.GetValue(IsUngroupedTargetProperty);

    #endregion

    #region Dragging a conversation

    private static void OnIsConversationChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += OnConversationMouseDown;
            element.PreviewMouseMove += OnConversationMouseMove;
            element.PreviewMouseLeftButtonUp += OnConversationMouseUp;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= OnConversationMouseDown;
            element.PreviewMouseMove -= OnConversationMouseMove;
            element.PreviewMouseLeftButtonUp -= OnConversationMouseUp;
        }
    }

    private static void OnConversationMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not ConversationInfo info)
            return;

        // The delete button lives inside the row. These are tunnelling handlers,
        // so they run before it does; claiming the press here would swallow its
        // click and let a drag start from it.
        if (IsInsideButton(e.OriginalSource as DependencyObject, element))
            return;

        var viewModel = FindViewModel(element);
        if (viewModel == null)
            return;

        var modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            viewModel.ToggleSelection(info);
            _pressPending = false;
            e.Handled = true;
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            viewModel.SelectRangeTo(info);
            _pressPending = false;
            e.Handled = true;
            return;
        }

        // A plain press on something already part of a multi-selection must not
        // collapse that selection yet: the press may be the start of dragging
        // the whole bunch. It collapses on mouse up instead, if no drag began.
        if (!info.IsSelected)
            viewModel.SelectOnly(info);

        _pressOrigin = e.GetPosition(null);
        _pressedConversation = info;
        _pressPending = true;
    }

    private static void OnConversationMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressPending || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (sender is not FrameworkElement element)
            return;

        var offset = e.GetPosition(null) - _pressOrigin;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var viewModel = FindViewModel(element);
        if (viewModel == null)
            return;

        var ids = viewModel.SelectedConversations.Select(c => c.Id).ToList();
        if (ids.Count == 0)
            return;

        // The press is spent either way: DragDrop runs its own message loop, so
        // the matching mouse up never arrives here.
        _pressPending = false;
        _pressedConversation = null;

        var data = new DataObject(ConversationIdsFormat, string.Join(",", ids));
        DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
    }

    private static void OnConversationMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressPending || sender is not FrameworkElement element)
            return;

        var info = _pressedConversation;
        _pressPending = false;
        _pressedConversation = null;

        if (info == null || !ReferenceEquals(element.DataContext, info))
            return;

        if (IsInsideButton(e.OriginalSource as DependencyObject, element))
            return;

        var viewModel = FindViewModel(element);
        if (viewModel == null)
            return;

        // Released without dragging: this was a click. Narrow any multi-selection
        // back down to the one row and open it.
        viewModel.SelectOnly(info);
        viewModel.SelectConversationCommand.Execute(info);
        e.Handled = true;
    }

    #endregion

    #region Dropping onto a group

    private static void OnIsGroupTargetChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.AllowDrop = true;
            element.DragEnter += OnGroupDragOver;
            element.DragOver += OnGroupDragOver;
            element.DragLeave += OnGroupDragLeave;
            element.Drop += OnGroupDrop;
        }
        else
        {
            element.AllowDrop = false;
            element.DragEnter -= OnGroupDragOver;
            element.DragOver -= OnGroupDragOver;
            element.DragLeave -= OnGroupDragLeave;
            element.Drop -= OnGroupDrop;
        }
    }

    private static void OnGroupDragOver(object sender, DragEventArgs e)
    {
        var group = (sender as FrameworkElement)?.DataContext as ConversationGroupViewModel;

        if (group == null || !e.Data.GetDataPresent(ConversationIdsFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        group.IsDropTarget = true;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private static void OnGroupDragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConversationGroupViewModel group)
            group.IsDropTarget = false;
    }

    private static void OnGroupDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not ConversationGroupViewModel group)
        {
            return;
        }

        group.IsDropTarget = false;
        e.Handled = true;

        var ids = ReadIds(e);
        if (ids.Count == 0)
            return;

        var viewModel = FindViewModel(element);
        if (viewModel == null)
            return;

        _ = viewModel.MoveToGroupAsync(ids, group.Id);
    }

    #endregion

    #region Dropping back out of a group

    private static void OnIsUngroupedTargetChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.AllowDrop = true;
            element.DragEnter += OnUngroupedDragOver;
            element.DragOver += OnUngroupedDragOver;
            element.DragLeave += OnUngroupedDragLeave;
            element.Drop += OnUngroupedDrop;
        }
        else
        {
            element.AllowDrop = false;
            element.DragEnter -= OnUngroupedDragOver;
            element.DragOver -= OnUngroupedDragOver;
            element.DragLeave -= OnUngroupedDragLeave;
            element.Drop -= OnUngroupedDrop;
        }
    }

    private static void OnUngroupedDragOver(object sender, DragEventArgs e)
    {
        var viewModel = FindViewModel(sender as FrameworkElement);

        if (viewModel == null || !e.Data.GetDataPresent(ConversationIdsFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        viewModel.IsUngroupedDropTarget = true;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private static void OnUngroupedDragLeave(object sender, DragEventArgs e)
    {
        var viewModel = FindViewModel(sender as FrameworkElement);
        if (viewModel != null)
            viewModel.IsUngroupedDropTarget = false;
    }

    private static void OnUngroupedDrop(object sender, DragEventArgs e)
    {
        var viewModel = FindViewModel(sender as FrameworkElement);
        if (viewModel == null)
            return;

        viewModel.IsUngroupedDropTarget = false;
        e.Handled = true;

        var ids = ReadIds(e);
        if (ids.Count > 0)
            _ = viewModel.RemoveFromGroupAsync(ids);
    }

    #endregion

    #region Helpers

    private static List<Guid> ReadIds(DragEventArgs e)
    {
        if (e.Data.GetData(ConversationIdsFormat) is not string payload)
            return new List<Guid>();

        return payload
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Guid.TryParse(part, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
    }

    /// <summary>
    /// True when the press landed on a button inside the row rather than the
    /// row itself. Stops at <paramref name="row"/> so a button elsewhere in the
    /// window is not mistaken for one of ours.
    /// </summary>
    private static bool IsInsideButton(DependencyObject? source, DependencyObject row)
    {
        while (source != null && !ReferenceEquals(source, row))
        {
            if (source is ButtonBase)
                return true;

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>
    /// Walks up to the sidebar and takes its view model. Rows and headers sit in
    /// the normal visual tree, so unlike a context menu they can find it.
    /// </summary>
    private static ConversationListViewModel? FindViewModel(FrameworkElement? element)
    {
        DependencyObject? current = element;

        while (current != null)
        {
            if (current is ConversationListView view)
                return view.DataContext as ConversationListViewModel;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    #endregion
}
