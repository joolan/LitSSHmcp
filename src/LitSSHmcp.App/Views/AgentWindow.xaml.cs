using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using LitSSHmcp.Agent;
using LitSSHmcp.App.Services;
using LitSSHmcp.App.ViewModels;
using LitSSHmcp.Core.Services.Storage;
using Wpf.Ui.Controls;

namespace LitSSHmcp.App.Views;

public partial class AgentWindow : FluentWindow
{
    private readonly AgentViewModel _viewModel;
    private readonly IConfigService _configService;

    public AgentWindow(IConfigService configService, IContextStore store, string skillsDir)
    {
        InitializeComponent();
        _configService = configService;

        _viewModel = new AgentViewModel(configService, store, skillsDir, action => Dispatcher.Invoke(action));
        _viewModel.SettingsRequested += tab => Dispatcher.Invoke(() => OpenSettings(tab));
        _viewModel.TemporaryChatRequested += text => Dispatcher.Invoke(() => OpenTemporaryChat(text));
        _viewModel.SkillsPickerRequested += () => Dispatcher.Invoke(OpenSkillPicker);
        DataContext = _viewModel;
        WindowLayout.Attach(this, "agent");
        _viewModel.Turns.CollectionChanged += (_, _) => ChatScroll.ScrollToEnd();
        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closed += async (_, _) => await _viewModel.DisposeAsync();
    }

    // 点击用户指令：展开/折叠本轮工具过程
    private void OnUserClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentTurn turn && turn.HasSteps)
            turn.ShowTools = !turn.ShowTools;
    }

    /// <summary>对外暴露的助手 ViewModel（供主页快捷提问写入输入/附件）。</summary>
    public AgentViewModel ViewModel => _viewModel;

    /// <summary>主页「AI 快捷提问」：把问题与附件填入输入框并聚焦（不自动发送）。</summary>
    public void SubmitPrompt(string text, IReadOnlyList<string>? attachmentPaths = null)
    {
        _viewModel.Input = text;
        if (attachmentPaths is { Count: > 0 })
            _viewModel.AddAttachmentFiles(attachmentPaths);
        Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => OpenSettings(null);

    private void OpenSettings(string? tab)
    {
        new AgentSettingsWindow(_configService, tab) { Owner = this }.ShowDialog();
        _ = _viewModel.ReloadProvidersAsync();
        _ = _viewModel.ReloadSessionsAsync();
        _viewModel.ReloadPromptTemplates();
    }

    // 输入区「＋」更多：左键打开其右键菜单
    private void OnComposerMore(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is { } menu)
        {
            menu.PlacementTarget = element;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // 技能选择：/技能 命令、+ 菜单、chip 上的「技能…」按钮共用
    private void OnSelectSkills(object sender, RoutedEventArgs e) => OpenSkillPicker();

    private void OpenSkillPicker()
    {
        if (_viewModel.AvailableSkills.Count == 0)
        {
            System.Windows.MessageBox.Show("没有可用技能：请在「AI 助手设置 → 技能目录」指定目录，或使用内置技能。",
                "AI 运维助手", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var root = _viewModel.SkillsRoot;
        if (string.IsNullOrEmpty(root))
            return;

        var activeIds = _viewModel.ActiveSkills.Select(s => s.Id).ToList();
        var picker = new SkillPickerWindow(root, _viewModel.AvailableSkills.ToList(), activeIds) { Owner = this };
        if (picker.ShowDialog() == true)
            _viewModel.ApplySkillSelection(picker.SelectedSkillIds);
    }

    private void OnRemoveActiveSkill(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SkillInfo skill)
            _viewModel.RemoveActiveSkill(skill);
    }

    // 工具审批策略：+ 菜单打开
    private void OnToolPolicy(object sender, RoutedEventArgs e) => OpenToolPolicy();

    // 用量 / 成本：+ 菜单打开
    private void OnShowUsage(object sender, RoutedEventArgs e) =>
        new UsageWindow(_viewModel.BuildUsageReport()) { Owner = this }.ShowDialog();

    private void OpenToolPolicy()
    {
        var catalog = _viewModel.ToolCatalog;
        if (catalog.Count == 0)
        {
            System.Windows.MessageBox.Show("未获取到工具目录：请先确保 AI 助手已连接模型。",
                "AI 运维助手", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var window = new ToolPolicyWindow(catalog, _viewModel.ToolPolicy) { Owner = this };
        if (window.ShowDialog() == true)
            _viewModel.ApplyToolPolicy(window.Result);
    }

    // 拖动输入区顶部把手调整高度
    private void OnComposerResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var height = InputBox.Height;
        if (double.IsNaN(height) || height <= 0)
            height = InputBox.ActualHeight;
        InputBox.Height = Math.Clamp(height - e.VerticalChange, 64, 400);
    }

    // 右键会话：在 ListBox 层定位鼠标所在项；仅当命中“会话行”时才让列表菜单弹出，
    // 命中“分组标题”则放行其自身菜单，命中空白处则不弹菜单。
    private AgentSessionRow? _contextSession;
    private SidebarItemVM? _highlightItem;
    private bool _rightClickOnList;
    private const string SessionDragFormat = "litssh.agent.sessionId";
    private const string GroupDragFormat = "litssh.agent.groupName";
    private System.Windows.Point _dragStart;
    private SidebarItemVM? _dragItem;
    private System.Windows.Point _groupDragStart;
    private SidebarItemVM? _groupDragHeader;
    private bool _groupDragging;

    private void OnSessionListRightClick(object sender, MouseButtonEventArgs e)
    {
        _rightClickOnList = true;
        _contextSession = FindSidebarItem(e.OriginalSource as DependencyObject)?.Session;
        SetContextHighlight(_contextSession);
        if (_contextSession is not null)
            e.Handled = true;   // 会话项：拦截，让列表菜单作用于该项；分组标题项：放行其自身菜单
    }

    // 高亮右键命中的会话行（菜单打开期间保持悬浮态）
    private void SetContextHighlight(AgentSessionRow? session)
    {
        if (_highlightItem is not null)
            _highlightItem.IsContextTarget = false;
        _highlightItem = session is null
            ? null
            : _viewModel.SidebarItems.FirstOrDefault(i => i.Session?.Id == session.Id);
        if (_highlightItem is not null)
            _highlightItem.IsContextTarget = true;
    }

    // 列表菜单即将打开：鼠标右键空白处不弹菜单；键盘（上下文键/Shift+F10）则作用于当前会话
    private void OnSessionListMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (_contextSession is null)
        {
            if (_rightClickOnList)
                e.Handled = true;
            else
                _contextSession = _viewModel.SelectedSession;
            if (_contextSession is null)
                e.Handled = true;
        }
    }

    private void OnSessionMenuClosed(object sender, RoutedEventArgs e)
    {
        SetContextHighlight(null);
        _contextSession = null;
        _rightClickOnList = false;
    }

    // 分组右键菜单即将打开：未分组（默认分组）不可重命名/删除
    private void OnGroupMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (System.Windows.Controls.ContextMenu)sender;
        var header = (menu.PlacementTarget as FrameworkElement)?.DataContext as SidebarItemVM;
        var isUngrouped = string.IsNullOrEmpty(header?.GroupName);
        foreach (var obj in menu.Items)
            if (obj is System.Windows.Controls.MenuItem mi)
                mi.IsEnabled = !isUngrouped;
    }

    private static SidebarItemVM? FindSidebarItem(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.ListBoxItem item)
                return item.DataContext as SidebarItemVM;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static bool ExceedsDragThreshold(System.Windows.Point start, System.Windows.Point now) =>
        Math.Abs(now.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(now.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    // 分组标题左键：折叠/展开（拖拽过程中不触发）
    private void OnGroupHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (_groupDragging)
            return;
        if ((sender as FrameworkElement)?.DataContext is SidebarItemVM { IsHeader: true } header)
            _viewModel.ToggleGroup(header.GroupName);
    }

    // 会话行：记录拖动起点
    private void OnSessionRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (sender as FrameworkElement)?.DataContext as SidebarItemVM;
    }

    // 会话行：按住左键拖动超过阈值 → 发起拖拽
    private void OnSessionRowMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem?.Session is not { } session)
            return;
        if (!ExceedsDragThreshold(_dragStart, e.GetPosition(null)))
            return;
        _dragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(SessionDragFormat, session.Id), DragDropEffects.Move);
    }

    // 分组标题：记录拖动起点
    private void OnGroupHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _groupDragStart = e.GetPosition(null);
        _groupDragHeader = (sender as FrameworkElement)?.DataContext as SidebarItemVM;
    }

    // 分组标题：按住左键拖动超过阈值 → 发起分组排序拖拽（未分组固定置顶，不可拖）
    private void OnGroupHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _groupDragHeader is not { IsHeader: true } header ||
            header.GroupName.Length == 0)
            return;
        if (!ExceedsDragThreshold(_groupDragStart, e.GetPosition(null)))
            return;
        _groupDragHeader = null;
        _groupDragging = true;
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(GroupDragFormat, header.GroupName), DragDropEffects.Move);
        }
        finally
        {
            _groupDragging = false;
        }
    }

    // 分组标题：接收会话入组 / 分组排序
    private void OnGroupHeaderDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(SessionDragFormat) || e.Data.GetDataPresent(GroupDragFormat);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnGroupHeaderDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not SidebarItemVM { IsHeader: true } header)
            return;

        if (e.Data.GetDataPresent(SessionDragFormat) && e.Data.GetData(SessionDragFormat) is long id)
        {
            var session = _viewModel.Sessions.FirstOrDefault(s => s.Id == id);
            if (session is not null)
                await _viewModel.MoveSessionAsync(session, header.GroupName, after: null);
        }
        else if (e.Data.GetDataPresent(GroupDragFormat) && e.Data.GetData(GroupDragFormat) is string src)
        {
            if (header.GroupName.Length > 0)   // 不能以「未分组」为排序目标
                await _viewModel.ReorderGroupAsync(src, header.GroupName);
        }
    }

    // 会话行：接住落点 → 移到该会话所在分组，并排到它之后
    private void OnSessionRowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SessionDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnSessionRowDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not SidebarItemVM { Session: { } target })
            return;
        if (e.Data.GetDataPresent(SessionDragFormat) &&
            e.Data.GetData(SessionDragFormat) is long id && id != target.Id)
        {
            var dragged = _viewModel.Sessions.FirstOrDefault(s => s.Id == id);
            if (dragged is not null)
                await _viewModel.MoveSessionAsync(dragged, target.Group, after: target);
        }
    }

    // 新建分组
    private async void OnNewGroup(object sender, RoutedEventArgs e)
    {
        var name = TextInputDialog.Prompt(this, "新建分组", "分组名称:", string.Empty);
        if (!string.IsNullOrWhiteSpace(name))
            await _viewModel.AddGroupAsync(name);
    }

    // 分组标题右键：重命名
    private async void OnRenameGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarItemVM { IsHeader: true } header)
            return;
        var name = TextInputDialog.Prompt(this, "重命名分组", "分组名称:", header.HeaderText);
        if (!string.IsNullOrWhiteSpace(name))
            await _viewModel.RenameGroupAsync(header.GroupName, name);
    }

    // 分组标题右键：删除（其下任务归入未分组）
    private async void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarItemVM { IsHeader: true } header)
            return;
        if (header.GroupName.Length == 0)
        {
            System.Windows.MessageBox.Show("「未分组」不可删除。", "删除分组");
            return;
        }
        if (System.Windows.MessageBox.Show($"删除分组「{header.HeaderText}」？其下任务将归入「未分组」。", "删除分组",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.OK)
            return;
        await _viewModel.DeleteGroupAsync(header.GroupName);
    }

    // 会话右键菜单打开：构建「移动到分组」子菜单（未分组 + 各分组，带勾选）
    private void OnSessionMenuOpened(object sender, RoutedEventArgs e)
    {
        MoveGroupMenu.Items.Clear();
        var session = _contextSession;
        var current = session?.Group ?? string.Empty;

        void AddItem(string header, string group)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = header,
                IsCheckable = true,
                IsChecked = string.Equals(current, group, StringComparison.Ordinal)
            };
            item.Click += async (_, _) =>
            {
                if (session is not null)
                    await _viewModel.MoveSessionToGroupAsync(session, group);
            };
            MoveGroupMenu.Items.Add(item);
        }

        AddItem("未分组", string.Empty);
        foreach (var g in _viewModel.Groups)
            AddItem(g, g);
    }

    private void OnDeleteSession(object sender, RoutedEventArgs e)
    {
        var session = _contextSession;
        if (session is null)
            return;
        var result = System.Windows.MessageBox.Show(
            $"确定删除会话「{session.Title}」？此操作不可恢复，会话及其消息将被永久删除。",
            "删除会话", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result == System.Windows.MessageBoxResult.Yes)
            _ = _viewModel.DeleteSessionAsync(session);
    }

    private void OnArchiveSession(object sender, RoutedEventArgs e)
    {
        var session = _contextSession;
        if (session is not null)
            _ = _viewModel.ArchiveSessionAsync(session);
    }

    private void OnClearDisplay(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.ClearSessionMessagesAsync(_contextSession ?? _viewModel.SelectedSession);
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = _viewModel.WorkspaceDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开工作区文件夹失败: " + ex.Message, "AI 运维助手");
        }
    }

    private void OnRenameSession(object sender, RoutedEventArgs e)
    {
        var session = _contextSession;
        if (session is null)
            return;
        var name = TextInputDialog.Prompt(this, "重命名会话", "会话名称:", session.Title);
        if (name is not null)
            _ = _viewModel.RenameSessionAsync(session, name);
    }

    private void OnEditTurn(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentTurn turn)
            return;

        if (!string.IsNullOrWhiteSpace(_viewModel.Input) || _viewModel.HasAttachments)
        {
            var result = System.Windows.MessageBox.Show(
                "输入区已有内容，编辑该消息将覆盖当前输入与附件，是否继续？",
                "AI 运维助手", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes)
                return;
        }

        _viewModel.BeginEdit(turn);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // 斜杠命令面板导航/选择
        if (_viewModel.IsSlashPaletteOpen)
        {
            if (e.Key == Key.Down) { _viewModel.MoveSlashSelection(1); ScrollSlashIntoView(); e.Handled = true; return; }
            if (e.Key == Key.Up) { _viewModel.MoveSlashSelection(-1); ScrollSlashIntoView(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { _viewModel.CloseSlashPalette(); e.Handled = true; return; }
            if ((e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) || e.Key == Key.Tab)
            {
                if (_viewModel.ExecuteSelectedSlashCommand())
                {
                    InputBox.Focus();
                    e.Handled = true;
                    return;
                }
            }
        }

        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;

        // 输入恰好是某个斜杠命令 → 直接执行（即使主任务进行中也可用，如 /临时聊天）
        _viewModel.Input = InputBox.Text;
        if (_viewModel.TryExecuteInputCommand())
        {
            InputBox.Focus();
            e.Handled = true;
            return;
        }

        if (_viewModel.IsBusy)
            return;
        if (_viewModel.SendCommand.CanExecute(null))
            _viewModel.SendCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSlashSuggestionClick(object sender, MouseButtonEventArgs e)
    {
        if (SlashList.SelectedItem is SlashSuggestion)
        {
            _viewModel.ExecuteSelectedSlashCommand();
            InputBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>键盘上下移动斜杠建议时，把选中项滚动到可见区。</summary>
    private void ScrollSlashIntoView()
    {
        if (_viewModel.SelectedSlashSuggestion is { } item)
            SlashList.ScrollIntoView(item);
    }

    private TemporaryChatWindow? _temporaryChatWindow;

    private void OpenTemporaryChat(string? initialText)
    {
        if (_temporaryChatWindow is { IsLoaded: true })
        {
            _temporaryChatWindow.Activate();
            if (!string.IsNullOrWhiteSpace(initialText))
                _temporaryChatWindow.Prefill(initialText!);
            return;
        }

        var window = new TemporaryChatWindow(_configService, _viewModel.SelectedProvider?.Id)
        {
            Owner = this,
            Topmost = false
        };
        window.Closed += (_, _) => _temporaryChatWindow = null;
        _temporaryChatWindow = window;
        if (!string.IsNullOrWhiteSpace(initialText))
            window.Prefill(initialText!);
        window.Show();
    }

    // 整窗 Ctrl+V：剪贴板含图片/文件则作为附件；纯文本交给输入框正常粘贴
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && TryAttachFromClipboard())
            e.Handled = true;
    }

    private bool TryAttachFromClipboard()
    {
        try
        {
            // 1) 文件（资源管理器复制文件）
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (files.Length > 0)
                {
                    _viewModel.AddAttachmentFiles(files);
                    return true;
                }
            }

            // 2) 图片（标准图片，或浏览器复制的 PNG 数据流）
            if (Clipboard.ContainsImage())
                return SaveClipboardImage(Clipboard.GetImage());

            var data = Clipboard.GetDataObject();
            if (data is not null && data.GetDataPresent("PNG") && data.GetData("PNG") is Stream stream)
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                return SaveClipboardImage(bmp);
            }
        }
        catch { /* 剪贴板占用/格式异常忽略 */ }
        return false;
    }

    private bool SaveClipboardImage(System.Windows.Media.Imaging.BitmapSource? image)
    {
        if (image is null)
            return false;
        var dir = Path.Combine(Path.GetTempPath(), "litssh-attach");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using (var fs = File.Create(file))
            encoder.Save(fs);
        _viewModel.AddAttachmentFiles(new[] { file });
        return true;
    }

    private void OnAddImage(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "添加图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
            _viewModel.AddAttachmentFiles(dialog.FileNames);
    }

    private void OnAddFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "添加文件",
            Filter = "文档|*.txt;*.md;*.log;*.json;*.yaml;*.yml;*.xml;*.csv;*.docx;*.pdf;*.ini;*.conf;*.properties;*.sql;*.sh;*.ps1;*.cs;*.java;*.py;*.js;*.ts;*.go;*.html;*.css|所有文件|*.*"
        };
        if (dialog.ShowDialog() == true)
            _viewModel.AddAttachmentFiles(dialog.FileNames);
    }

    private void OnRemoveAttachment(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentAttachment attachment)
            _viewModel.RemoveAttachment(attachment);
    }

    private void OnClearAttachments(object sender, RoutedEventArgs e) => _viewModel.ClearAttachments();

    // 点击附件芯片：图片则预览大图（点“移除”按钮时不触发）
    private void OnPreviewAttachment(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        if ((sender as FrameworkElement)?.DataContext is AgentAttachment { IsImage: true } attachment)
            new ImagePreviewWindow(attachment.Path) { Owner = this }.ShowDialog();
    }

    // 点击气泡内缩略图：预览大图
    private void OnPreviewImage(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string path && !string.IsNullOrWhiteSpace(path))
            new ImagePreviewWindow(path) { Owner = this }.ShowDialog();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void OnComposerDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComposerDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _viewModel.AddAttachmentFiles(files);
        e.Handled = true;
    }

    private void OnCopyAnswer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AgentTurn turn && !string.IsNullOrEmpty(turn.AssistantText))
        {
            try { Clipboard.SetText(turn.AssistantText); } catch { /* ignore */ }
        }
    }

    // 复制用户消息：纯文字→文字；纯附件→附件；两者都有→弹出选择
    private void OnCopyUserMessage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AgentTurn turn)
            return;

        var text = _viewModel.GetUserPlainText(turn);
        var paths = _viewModel.GetAttachmentPaths(turn);
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasAttachments = paths.Count > 0;

        if (hasText && hasAttachments)
        {
            var menu = new System.Windows.Controls.ContextMenu();
            var copyText = new System.Windows.Controls.MenuItem { Header = "复制文字" };
            copyText.Click += (_, _) => CopyText(text);
            var copyAttachments = new System.Windows.Controls.MenuItem { Header = "复制附件" };
            copyAttachments.Click += (_, _) => CopyAttachments(paths);
            menu.Items.Add(copyText);
            menu.Items.Add(copyAttachments);
            menu.PlacementTarget = sender as UIElement;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        else if (hasAttachments)
        {
            CopyAttachments(paths);
        }
        else if (hasText)
        {
            CopyText(text);
        }
    }

    private void CopyText(string text) =>
        CopyToClipboard(() => Clipboard.SetText(text), "已复制文字");

    private void CopyAttachments(IReadOnlyList<string> paths) =>
        CopyToClipboard(() =>
        {
            if (paths.Count == 1 && IsImageFile(paths[0]))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(paths[0]);
                bitmap.EndInit();
                Clipboard.SetImage(bitmap);
            }
            else
            {
                var files = new System.Collections.Specialized.StringCollection();
                foreach (var path in paths)
                    files.Add(path);
                Clipboard.SetFileDropList(files);
            }
        }, "已复制附件");

    // 剪贴板 API 需要 STA 且可能较慢：放到后台 STA 线程，避免阻塞 UI
    private void CopyToClipboard(Action clipboardAction, string doneMessage)
    {
        _viewModel.StatusMessage = "正在复制…";
        var dispatcher = Dispatcher;
        var thread = new System.Threading.Thread(() =>
        {
            var ok = false;
            try { clipboardAction(); ok = true; } catch { /* ignore */ }
            dispatcher.BeginInvoke(new Action(() => _viewModel.StatusMessage = ok ? doneMessage : "复制失败"));
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var session = _contextSession ?? _viewModel.SelectedSession;
        if (session is null)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"litssh-agent-{DateTime.Now:yyyyMMdd-HHmmss}.md",
            Filter = "Markdown 文件|*.md|文本文件|*.txt|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
            return;

        var sb = new StringBuilder();
        if (session.Id == _viewModel.CurrentSessionId)
        {
            foreach (var turn in _viewModel.Turns)
            {
                sb.AppendLine($"## 🧑 用户 · {turn.UserTime}");
                sb.AppendLine();
                sb.AppendLine(turn.UserText);
                sb.AppendLine();

                if (turn.Steps.Count > 0)
                {
                    sb.AppendLine($"<details><summary>工具过程 ({turn.Steps.Count})</summary>");
                    sb.AppendLine();
                    foreach (var step in turn.Steps)
                        sb.AppendLine($"- {step.Label}：{step.Detail.Replace("\n", " ")}");
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }

                if (!string.IsNullOrEmpty(turn.AssistantText) || !string.IsNullOrEmpty(turn.Note))
                {
                    sb.AppendLine($"## 🤖 助手 · {turn.Footer}");
                    sb.AppendLine();
                    if (!string.IsNullOrEmpty(turn.AssistantText))
                    {
                        sb.AppendLine(turn.AssistantText);
                        sb.AppendLine();
                    }
                    if (!string.IsNullOrEmpty(turn.Note))
                    {
                        sb.AppendLine($"> {turn.Note}");
                        sb.AppendLine();
                    }
                }
            }
        }
        else
        {
            foreach (var row in await _viewModel.GetMessagesAsync(session.Id))
            {
                sb.AppendLine(row.Role == "user" ? $"## 🧑 用户 · {row.Timestamp}" : "## 🤖 助手");
                sb.AppendLine();
                sb.AppendLine(row.Content);
                sb.AppendLine();
            }
        }

        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 导出失败忽略
        }
    }
}
