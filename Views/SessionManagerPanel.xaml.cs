using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TabbySSH.Models;
using TabbySSH.Services;
using Point = System.Windows.Point;
using WpfImage = System.Windows.Controls.Image;

namespace TabbySSH.Views;

public partial class SessionManagerPanel : UserControl
{
    private const double DRAG_DISTANCE_MULTIPLIER = 3.0;

    private SessionManager? _sessionManager;
    private object? _selectedItem;
    private Point _dragStartPoint;
    private TreeViewItem? _draggedItem;

    public event EventHandler<SessionConfiguration>? SessionSelected;
    public event EventHandler<SessionConfiguration>? SessionEditRequested;

    public SessionManagerPanel()
    {
        InitializeComponent();
    }

    public void SetSessionManager(SessionManager sessionManager)
    {
        _sessionManager = sessionManager;
        if (_sessionManager != null)
        {
            _sessionManager.Sessions.CollectionChanged += (s, e) => RefreshTreeView();
            _sessionManager.Groups.CollectionChanged += (s, e) => RefreshTreeView();
        }
        RefreshTreeView();
    }

    private void RefreshTreeView()
    {
        if (_sessionManager == null)
        {
            return;
        }

        SessionsTreeView.Items.Clear();

        var rootGroups = _sessionManager.Groups.Where(g => string.IsNullOrEmpty(g.ParentGroup)).OrderBy(g => g.Order).ThenBy(g => g.Name).ToList();
        var rootSessions = _sessionManager.Sessions.Where(s => string.IsNullOrEmpty(s.Group)).OrderBy(s => s.Order).ThenBy(s => s.Name).ToList();

        foreach (var group in rootGroups)
        {
            var groupItem = CreateGroupItem(group);
            SessionsTreeView.Items.Add(groupItem);
        }

        foreach (var session in rootSessions)
        {
            var sessionItem = CreateSessionItem(session);
            SessionsTreeView.Items.Add(sessionItem);
        }
    }

    private TreeViewItem CreateGroupItem(SessionGroup group)
    {
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new WpfImage
        {
            Source = GetFolderIcon(),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var textBlock = new TextBlock
        {
            Text = group.Name,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(icon);
        headerPanel.Children.Add(textBlock);

        var item = new TreeViewItem
        {
            Header = headerPanel,
            Tag = group,
            IsExpanded = true
        };

        var childGroups = _sessionManager!.Groups.Where(g => g.ParentGroup == group.Id).OrderBy(g => g.Order).ThenBy(g => g.Name).ToList();
        var childSessions = _sessionManager.Sessions.Where(s => s.Group == group.Id).OrderBy(s => s.Order).ThenBy(s => s.Name).ToList();

        foreach (var childGroup in childGroups)
        {
            item.Items.Add(CreateGroupItem(childGroup));
        }

        foreach (var session in childSessions)
        {
            item.Items.Add(CreateSessionItem(session));
        }

        return item;
    }

    private TreeViewItem CreateSessionItem(SessionConfiguration session)
    {
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new WpfImage
        {
            Source = GetComputerIcon(),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var textBlock = new TextBlock
        {
            Text = session.Name,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(icon);
        headerPanel.Children.Add(textBlock);

        return new TreeViewItem
        {
            Header = headerPanel,
            Tag = session
        };
    }

    private void SessionsTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectedItem is SessionConfiguration session)
        {
            SessionSelected?.Invoke(this, session);
        }
    }

    private void SessionsTreeView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _selectedItem is SessionConfiguration session)
        {
            SessionSelected?.Invoke(this, session);
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            if (_selectedItem is SessionConfiguration sessionToRename)
            {
                StartRenamingSession(sessionToRename);
            }
            else if (_selectedItem is SessionGroup groupToRename)
            {
                StartRenamingGroup(groupToRename);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (_selectedItem is SessionConfiguration sessionToDelete)
            {
                DeleteSession(sessionToDelete);
            }
            else if (_selectedItem is SessionGroup groupToDelete)
            {
                DeleteGroup(groupToDelete);
            }
            e.Handled = true;
        }
    }

    private void SessionsTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedItem = SessionsTreeView.SelectedItem is TreeViewItem item ? item.Tag : null;
        UpdateContextMenu();
    }

    private void UpdateContextMenu()
    {
        ConnectMenuItem.IsEnabled = _selectedItem is SessionConfiguration;
        EditMenuItem.IsEnabled = _selectedItem is SessionConfiguration;
        DuplicateMenuItem.IsEnabled = _selectedItem is SessionConfiguration;
        DeleteMenuItem.IsEnabled = _selectedItem != null;
        MoveToMenuItem.IsEnabled = _selectedItem is SessionConfiguration;
        
        MoveToMenuItem.Items.Clear();
        var rootMenuItem = new MenuItem { Header = "(Root)" };
        rootMenuItem.Click += MoveToRootMenuItem_Click;
        MoveToMenuItem.Items.Add(rootMenuItem);
        MoveToMenuItem.Items.Add(new Separator());
        
        if (_sessionManager != null && _selectedItem is SessionConfiguration)
        {
            var currentGroupId = ((SessionConfiguration)_selectedItem).Group;
            foreach (var group in _sessionManager.Groups.OrderBy(g => g.Order).ThenBy(g => g.Name))
            {
                if (group.Id != currentGroupId)
                {
                    var menuItem = new MenuItem
                    {
                        Header = group.Name,
                        Tag = group.Id
                    };
                    menuItem.Click += (s, e) => MoveSessionToGroup(((MenuItem)s).Tag?.ToString());
                    MoveToMenuItem.Items.Add(menuItem);
                }
            }
        }
    }

    private void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true && dialog.Configuration != null && _sessionManager != null)
        {
            _sessionManager.AddSession(dialog.Configuration);
            RefreshTreeView();
        }
    }

    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var inputDialog = new InputDialog("New Group", "Group name:", "")
        {
            Owner = Window.GetWindow(this)
        };

        if (inputDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(inputDialog.InputText) && _sessionManager != null)
        {
            var group = new SessionGroup
            {
                Name = inputDialog.InputText.Trim()
            };
            _sessionManager.AddGroup(group);
            RefreshTreeView();
        }
    }

    private void ConnectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is SessionConfiguration session)
        {
            SessionSelected?.Invoke(this, session);
        }
    }

    private void EditMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is SessionConfiguration session)
        {
            SessionEditRequested?.Invoke(this, session);
        }
    }

    private void DuplicateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is SessionConfiguration session && _sessionManager != null)
        {
            var duplicated = DuplicateSession(session);
            _sessionManager.AddSession(duplicated);
            RefreshTreeView();
        }
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is SessionConfiguration session)
        {
            DeleteSession(session);
        }
        else if (_selectedItem is SessionGroup group)
        {
            DeleteGroup(group);
        }
    }

    private void StartRenamingSession(SessionConfiguration session)
    {
        var treeViewItem = FindTreeViewItem(session);
        if (treeViewItem != null)
        {
            var textBox = new TextBox
            {
                Text = session.Name
            };
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    session.Name = textBox.Text;
                    if (_sessionManager != null)
                    {
                        _sessionManager.UpdateSession(session);
                    }
                    treeViewItem.Header = session.Name;
                    treeViewItem.Tag = session;
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    treeViewItem.Header = session.Name;
                    e.Handled = true;
                }
            };
            textBox.LostFocus += (s, e) =>
            {
                session.Name = textBox.Text;
                if (_sessionManager != null)
                {
                    _sessionManager.UpdateSession(session);
                }
                treeViewItem.Header = session.Name;
            };

            treeViewItem.Header = textBox;
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void StartRenamingGroup(SessionGroup group)
    {
        var treeViewItem = FindTreeViewItem(group);
        if (treeViewItem != null)
        {
            var textBox = new TextBox
            {
                Text = group.Name
            };
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    group.Name = textBox.Text;
                    if (_sessionManager != null)
                    {
                        _sessionManager.UpdateGroup(group);
                    }
                    group.Name = textBox.Text;
                    if (_sessionManager != null)
                    {
                        _sessionManager.UpdateGroup(group);
                    }
                    UpdateTreeViewItemHeader(treeViewItem, group);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    UpdateTreeViewItemHeader(treeViewItem, group);
                    e.Handled = true;
                }
            };
            textBox.LostFocus += (s, e) =>
            {
                group.Name = textBox.Text;
                if (_sessionManager != null)
                {
                    _sessionManager.UpdateGroup(group);
                }
                UpdateTreeViewItemHeader(treeViewItem, group);
            };

            treeViewItem.Header = textBox;
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private TreeViewItem? FindTreeViewItem(object tag)
    {
        return FindTreeViewItemRecursive(SessionsTreeView.Items, tag);
    }

    private TreeViewItem? FindTreeViewItemRecursive(ItemCollection items, object tag)
    {
        foreach (TreeViewItem item in items)
        {
            if (item.Tag == tag)
            {
                return item;
            }
            var found = FindTreeViewItemRecursive(item.Items, tag);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private SessionConfiguration DuplicateSession(SessionConfiguration original)
    {
        if (original is SshSessionConfiguration sshConfig)
        {
            return new SshSessionConfiguration
            {
                Name = $"{sshConfig.Name} (Copy)",
                Host = sshConfig.Host,
                Port = sshConfig.Port,
                Username = sshConfig.Username,
                Password = sshConfig.Password,
                UsePasswordAuthentication = sshConfig.UsePasswordAuthentication,
                ConnectionType = sshConfig.ConnectionType,
                Color = sshConfig.Color,
                Encoding = sshConfig.Encoding,
                Group = sshConfig.Group
            };
        }
        throw new NotSupportedException($"Cannot duplicate session type: {original.ConnectionType}");
    }

    private void DeleteSession(SessionConfiguration session)
    {
        var result = MessageBox.Show(
            $"Are you sure you want to delete session '{session.Name}'?",
            "Delete Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes && _sessionManager != null)
        {
            _sessionManager.DeleteSession(session.Id);
            RefreshTreeView();
        }
    }

    private void DeleteGroup(SessionGroup group)
    {
        var sessionCount = _sessionManager?.Sessions.Count(s => s.Group == group.Id) ?? 0;
        var message = sessionCount > 0
            ? $"Are you sure you want to delete group '{group.Name}'? This will also remove {sessionCount} session(s) in this group."
            : $"Are you sure you want to delete group '{group.Name}'?";

        var result = MessageBox.Show(
            message,
            "Delete Group",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes && _sessionManager != null)
        {
            _sessionManager.DeleteGroup(group.Id);
            RefreshTreeView();
        }
    }

    public void RefreshAfterEdit()
    {
        RefreshTreeView();
    }

    private void SessionsTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var hit = e.OriginalSource as DependencyObject;
        TreeViewItem? clickedItem = null;
        
        while (hit != null && hit != SessionsTreeView)
        {
            if (hit is TreeViewItem item && item.Tag != null)
            {
                clickedItem = item;
                break;
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
        
        if (clickedItem != null)
        {
            if (!clickedItem.IsSelected)
            {
                clickedItem.IsSelected = true;
            }
        }
    }

    private void SessionsTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _draggedItem = null;
        
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit != SessionsTreeView)
        {
            if (hit is TreeViewItem item && item.Tag != null)
            {
                _draggedItem = item;
                break;
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
    }

    private void SessionsTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null)
        {
            var currentPosition = e.GetPosition(null);
            var minHorizontalDistance = SystemParameters.MinimumHorizontalDragDistance * DRAG_DISTANCE_MULTIPLIER;
            var minVerticalDistance = SystemParameters.MinimumVerticalDragDistance * DRAG_DISTANCE_MULTIPLIER;
            if (Math.Abs(currentPosition.X - _dragStartPoint.X) > minHorizontalDistance ||
                Math.Abs(currentPosition.Y - _dragStartPoint.Y) > minVerticalDistance)
            {
                if (_draggedItem.Tag is SessionConfiguration session)
                {
                    var dataObject = new DataObject(typeof(SessionConfiguration), session);
                    DragDrop.DoDragDrop(_draggedItem, dataObject, DragDropEffects.Move);
                    _draggedItem = null;
                }
                else if (_draggedItem.Tag is SessionGroup group)
                {
                    var dataObject = new DataObject(typeof(SessionGroup), group);
                    DragDrop.DoDragDrop(_draggedItem, dataObject, DragDropEffects.Move);
                    _draggedItem = null;
                }
            }
        }
    }

    private void SessionsTreeView_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(SessionConfiguration)) || e.Data.GetDataPresent(typeof(SessionGroup)))
        {
            e.Effects = DragDropEffects.Move;
            
            var item = GetItemUnderMouse(e.GetPosition(SessionsTreeView));
            if (item != null)
            {
                item.IsSelected = true;
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void SessionsTreeView_Drop(object sender, DragEventArgs e)
    {
        if (_sessionManager == null)
        {
            return;
        }

        e.Handled = true;
        var dropPosition = e.GetPosition(SessionsTreeView);
        var dropTarget = GetItemUnderMouse(dropPosition);
        
        SessionConfiguration? draggedSession = null;
        SessionGroup? draggedGroup = null;
        
        if (e.Data.GetDataPresent(typeof(SessionConfiguration)))
        {
            draggedSession = e.Data.GetData(typeof(SessionConfiguration)) as SessionConfiguration;
        }
        else if (e.Data.GetDataPresent(typeof(SessionGroup)))
        {
            draggedGroup = e.Data.GetData(typeof(SessionGroup)) as SessionGroup;
        }
        
        if (draggedSession != null)
        {
            if (dropTarget != null && dropTarget.Tag != null)
            {
                if (dropTarget.Tag is SessionGroup targetGroup)
                {
                    MoveSessionToGroup(draggedSession, targetGroup.Id);
                    RefreshTreeView();
                }
                else if (dropTarget.Tag is SessionConfiguration targetSession)
                {
                    var targetGroupId = targetSession.Group;
                    var insertIndex = GetInsertIndex(dropTarget, dropPosition);
                    MoveSessionToGroup(draggedSession, targetGroupId, insertIndex);
                    RefreshTreeView();
                }
            }
            else
            {
                var parentItem = GetParentGroupItem(dropPosition);
                if (parentItem != null && parentItem.Tag is SessionGroup parentGroup)
                {
                    MoveSessionToGroup(draggedSession, parentGroup.Id);
                }
                else
                {
                    var insertIndex = GetRootInsertIndex(dropPosition);
                    MoveSessionToGroup(draggedSession, null, insertIndex);
                }
                RefreshTreeView();
            }
        }
        else if (draggedGroup != null)
        {
            if (dropTarget != null)
            {
                if (dropTarget.Tag is SessionGroup targetGroup)
                {
                    MoveGroupToGroup(draggedGroup, targetGroup.Id);
                }
                else if (dropTarget.Tag is SessionConfiguration targetSession)
                {
                    MoveGroupToGroup(draggedGroup, targetSession.Group);
                }
            }
            else
            {
                var insertIndex = GetRootInsertIndex(dropPosition);
                MoveGroupToGroup(draggedGroup, null, insertIndex);
            }
            RefreshTreeView();
        }
    }

    private TreeViewItem? GetItemUnderMouse(Point position)
    {
        var hit = SessionsTreeView.InputHitTest(position) as DependencyObject;
        TreeViewItem? foundItem = null;
        
        while (hit != null && hit != SessionsTreeView)
        {
            if (hit is TreeViewItem item)
            {
                foundItem = item;
                if (item.Tag != null)
                {
                    return item;
                }
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
        
        return foundItem;
    }

    private int GetInsertIndex(TreeViewItem targetItem, Point dropPosition)
    {
        if (targetItem.Tag is SessionConfiguration targetSession && _sessionManager != null)
        {
            var groupId = targetSession.Group;
            var sessionsInGroup = _sessionManager.Sessions.Where(s => s.Group == groupId).OrderBy(s => s.Order).ToList();
            
            var targetIndex = sessionsInGroup.FindIndex(s => s.Id == targetSession.Id);
            if (targetIndex < 0) return sessionsInGroup.Count;
            
            var itemBounds = targetItem.TransformToAncestor(SessionsTreeView).TransformBounds(new Rect(0, 0, targetItem.ActualWidth, targetItem.ActualHeight));
            var relativeY = dropPosition.Y - itemBounds.Top;
            
            if (relativeY < itemBounds.Height / 2)
            {
                return targetIndex;
            }
            else
            {
                return targetIndex + 1;
            }
        }
        return -1;
    }

    private int GetRootInsertIndex(Point dropPosition)
    {
        if (_sessionManager == null)
        {
            return 0;
        }

        var rootSessions = _sessionManager.Sessions.Where(s => string.IsNullOrEmpty(s.Group)).OrderBy(s => s.Order).ToList();
        if (rootSessions.Count == 0)
        {
            return 0;
        }

        foreach (var session in rootSessions)
        {
            var item = FindTreeViewItem(session);
            if (item != null)
            {
                var itemBounds = item.TransformToAncestor(SessionsTreeView).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
                if (dropPosition.Y < itemBounds.Top + itemBounds.Height / 2)
                {
                    var index = rootSessions.FindIndex(s => s.Id == session.Id);
                    return index >= 0 ? index : rootSessions.Count;
                }
            }
        }

        return rootSessions.Count;
    }

    private TreeViewItem? GetParentGroupItem(Point position)
    {
        var hit = SessionsTreeView.InputHitTest(position) as DependencyObject;
        
        while (hit != null && hit != SessionsTreeView)
        {
            if (hit is TreeViewItem item)
            {
                var parent = VisualTreeHelper.GetParent(item) as TreeViewItem;
                if (parent != null && parent.Tag is SessionGroup)
                {
                    return parent;
                }
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
        
        return null;
    }

    private void MoveSessionToGroup(SessionConfiguration session, string? groupId, int? insertIndex = null)
    {
        if (_sessionManager == null)
        {
            return;
        }

        var oldGroupId = session.Group;
        var sessionsInGroup = _sessionManager.Sessions.Where(s => s.Group == groupId && s.Id != session.Id).OrderBy(s => s.Order).ToList();
        
        session.Group = groupId;
        
        if (insertIndex.HasValue && insertIndex.Value >= 0 && insertIndex.Value <= sessionsInGroup.Count)
        {
            for (int i = insertIndex.Value; i < sessionsInGroup.Count; i++)
            {
                sessionsInGroup[i].Order = i + 1;
                _sessionManager.UpdateSession(sessionsInGroup[i]);
            }
            session.Order = insertIndex.Value;
        }
        else
        {
            session.Order = sessionsInGroup.Count > 0 ? sessionsInGroup.Max(s => s.Order) + 1 : 0;
        }
        
        _sessionManager.UpdateSession(session);
    }

    private void MoveSessionToGroup(string? groupId)
    {
        if (_selectedItem is SessionConfiguration session)
        {
            MoveSessionToGroup(session, groupId);
        }
    }

    private void MoveGroupToGroup(SessionGroup group, string? parentGroupId, int? insertIndex = null)
    {
        if (_sessionManager == null)
        {
            return;
        }

        if (parentGroupId == group.Id)
        {
            return;
        }

        var groupsInParent = _sessionManager.Groups.Where(g => g.ParentGroup == parentGroupId && g.Id != group.Id).OrderBy(g => g.Order).ToList();
        group.ParentGroup = parentGroupId;
        
        if (insertIndex.HasValue && insertIndex.Value >= 0 && insertIndex.Value <= groupsInParent.Count)
        {
            for (int i = insertIndex.Value; i < groupsInParent.Count; i++)
            {
                groupsInParent[i].Order = i + 1;
                _sessionManager.UpdateGroup(groupsInParent[i]);
            }
            group.Order = insertIndex.Value;
        }
        else
        {
            group.Order = groupsInParent.Count > 0 ? groupsInParent.Max(g => g.Order) + 1 : 0;
        }
        
        _sessionManager.UpdateGroup(group);
    }

    private void MoveToRootMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is SessionConfiguration session)
        {
            MoveSessionToGroup(session, null);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static ImageSource? GetFolderIcon()
    {
        try
        {
            var shell32Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
            var iconHandle = ExtractIcon(IntPtr.Zero, shell32Path, 3);
            if (iconHandle != IntPtr.Zero)
            {
                var icon = Icon.FromHandle(iconHandle);
                var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(iconHandle);
                return bitmapSource;
            }
        }
        catch
        {
        }

        try
        {
            var folderIcon = Icon.ExtractAssociatedIcon(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            if (folderIcon != null)
            {
                return GetIconFromSystemIcons(folderIcon);
            }
        }
        catch
        {
        }

        return null;
    }

    private static ImageSource? GetComputerIcon()
    {
        try
        {
            var shell32Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
            var iconHandle = ExtractIcon(IntPtr.Zero, shell32Path, 15);
            if (iconHandle != IntPtr.Zero)
            {
                var icon = Icon.FromHandle(iconHandle);
                var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(iconHandle);
                return bitmapSource;
            }
        }
        catch
        {
        }

        try
        {
            var computerIcon = Icon.ExtractAssociatedIcon(Environment.GetFolderPath(Environment.SpecialFolder.System));
            if (computerIcon != null)
            {
                return GetIconFromSystemIcons(computerIcon);
            }
        }
        catch
        {
        }

        return null;
    }

    private static ImageSource GetIconFromSystemIcons(Icon icon)
    {
        var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        return bitmapSource;
    }

    private void UpdateTreeViewItemHeader(TreeViewItem item, SessionConfiguration session)
    {
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new WpfImage
        {
            Source = GetComputerIcon(),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var textBlock = new TextBlock
        {
            Text = session.Name,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(icon);
        headerPanel.Children.Add(textBlock);
        item.Header = headerPanel;
    }

    private void UpdateTreeViewItemHeader(TreeViewItem item, SessionGroup group)
    {
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new WpfImage
        {
            Source = GetFolderIcon(),
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var textBlock = new TextBlock
        {
            Text = group.Name,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(icon);
        headerPanel.Children.Add(textBlock);
        item.Header = headerPanel;
    }
}
