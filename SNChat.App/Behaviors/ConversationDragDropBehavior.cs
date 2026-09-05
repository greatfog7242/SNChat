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

    /// <summary>
    /// Carries a single group id when a header is dragged to reorder. Kept
    /// apart from <see cref="ConversationIdsFormat"/> because a header accepts
    /// both, and what it does with a drop depends on which arrived.
    /// </summary>
    private const string GroupIdFormat = "SNChat.GroupId";

    // One gesture is in flight at a time, so the press is tracked statically
    // rather than per item.
    private static Point _pressOrigin;
    private static ConversationInfo? _pressedConversation;
    private static bool _pressPending;

    private static Point _groupPressOrigin;
    private static ConversationGroupViewModel? _pressedGroup;
    private static bool _groupPressPending;

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

    /// <summary>
    /// Set on a group header. It clicks to fold, drags to reorder, and accepts
    /// both dropped conversations and another dragged header.
    /// </summary>
    public static readonly DependencyProperty IsGroupHeaderProperty =
        DependencyProperty.RegisterAttached(
            "IsGroupHeader", typeof(bool), typeof(ConversationDragDropBehavior),
            new PropertyMetadata(false, OnIsGroupHeaderChanged));

    public static void SetIsGroupHeader(DependencyObject element, bool value) =>
        element.SetValue(IsGroupHeaderProperty, value);

    public static bool GetIsGroupHeader(DependencyObject element) =>
        (bool)element.GetValue(IsGroupHeaderProperty);

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

    private static void OnIsGroupHeaderChanged(
        DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.AllowDrop = true;
            element.PreviewMouseLeftButtonDown += OnGroupMouseDown;
            element.PreviewMouseMove += OnGroupMouseMove;
            element.PreviewMouseLeftButtonUp += OnGroupMouseUp;
            element.DragEnter += OnGroupDragOver;
            element.DragOver += OnGroupDragOver;
            element.DragLeave += OnGroupDragLeave;
            element.Drop += OnGroupDrop;
        }
        else
        {
            element.AllowDrop = false;
            element.PreviewMouseLeftButtonDown -= OnGroupMouseDown;
            element.PreviewMouseMove -= OnGroupMouseMove;
            element.PreviewMouseLeftButtonUp -= OnGroupMouseUp;
            element.DragEnter -= OnGroupDragOver;
            element.DragOver -= OnGroupDragOver;
            element.DragLeave -= OnGroupDragLeave;
            element.Drop -= OnGroupDrop;
        }
    }

    private static void OnGroupMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not ConversationGroupViewModel group)
        {
            return;
        }

        if (IsInsideButton(e.OriginalSource as DependencyObject, element))
            return;

        _groupPressOrigin = e.GetPosition(null);
        _pressedGroup = group;
        _groupPressPending = true;
    }

    private static void OnGroupMouseMove(object sender, MouseEventArgs e)
    {
        if (!_groupPressPending || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (sender is not FrameworkElement element || _pressedGroup == null)
            return;

        var offset = e.GetPosition(null) - _groupPressOrigin;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var group = _pressedGroup;

        // Spent either way: DragDrop runs its own message loop, so the matching
        // mouse up never arrives here and the click must not fire afterwards.
        _groupPressPending = false;
        _pressedGroup = null;

        var data = new DataObject(GroupIdFormat, group.Id.ToString());
        DragDrop.DoDragDrop(element, data, DragDropEffects.Move);

        ClearReorderMarkers(FindViewModel(element));
    }

    private static void OnGroupMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_groupPressPending || sender is not FrameworkElement element)
            return;

        var group = _pressedGroup;
        _groupPressPending = false;
        _pressedGroup = null;

        if (group == null || !ReferenceEquals(element.DataContext, group))
            return;

        if (IsInsideButton(e.OriginalSource as DependencyObject, element))
            return;

        // Released without dragging: a plain click, which folds or unfolds.
        var viewModel = FindViewModel(element);
        viewModel?.ToggleGroupCommand.Execute(group);
        e.Handled = true;
    }

    private static void OnGroupDragOver(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not ConversationGroupViewModel group)
        {
            return;
        }

        // Conversations land inside the group.
        if (e.Data.GetDataPresent(ConversationIdsFormat))
        {
            group.IsDropTarget = true;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        // Another header lands either side of this one, so which half of the
        // header the pointer is over decides where the line is drawn.
        if (e.Data.GetDataPresent(GroupIdFormat))
        {
            var draggedId = ReadGroupId(e);

            if (draggedId == group.Id || draggedId == Guid.Empty)
            {
                group.IsReorderAbove = false;
                group.IsReorderBelow = false;
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var above = e.GetPosition(element).Y < element.ActualHeight / 2;
            group.IsReorderAbove = above;
            group.IsReorderBelow = !above;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnGroupDragLeave(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ConversationGroupViewModel group)
        {
            group.IsDropTarget = false;
            group.IsReorderAbove = false;
            group.IsReorderBelow = false;
        }
    }

    private static void OnGroupDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not ConversationGroupViewModel group)
        {
            return;
        }

        var insertBefore = group.IsReorderAbove;

        group.IsDropTarget = false;
        group.IsReorderAbove = false;
        group.IsReorderBelow = false;
        e.Handled = true;

        var viewModel = FindViewModel(element);
        if (viewModel == null)
            return;

        if (e.Data.GetDataPresent(GroupIdFormat))
        {
            var draggedId = ReadGroupId(e);
            if (draggedId != Guid.Empty)
                _ = viewModel.ReorderGroupAsync(draggedId, group.Id, insertBefore);

            return;
        }

        var ids = ReadIds(e);
        if (ids.Count > 0)
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

    private static Guid ReadGroupId(DragEventArgs e) =>
        e.Data.GetData(GroupIdFormat) is string payload && Guid.TryParse(payload, out var id)
            ? id
            : Guid.Empty;

    /// <summary>
    /// Wipes the insertion lines. A drag that ends outside every header leaves
    /// no DragLeave on the one it last crossed, which would strand a line.
    /// </summary>
    private static void ClearReorderMarkers(ConversationListViewModel? viewModel)
    {
        if (viewModel == null)
            return;

        foreach (var group in viewModel.Groups)
        {
            group.IsReorderAbove = false;
            group.IsReorderBelow = false;
            group.IsDropTarget = false;
        }
    }

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
