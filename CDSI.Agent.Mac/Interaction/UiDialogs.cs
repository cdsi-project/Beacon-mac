using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CDSI.Agent.Application.Reader;
using CDSI.Agent.Application.OpenWeb;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Collections;
using CDSI.Agent.Core.OpenWeb;
using CDSI.Agent.Core.Storage;

namespace CDSI.Agent.Mac.Interaction;

internal static class UiDialogs
{
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#343D45"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#657078"));
    private static readonly IBrush PrimaryBrush = new SolidColorBrush(Color.Parse("#18794E"));
    private static readonly IBrush DangerBrush = new SolidColorBrush(Color.Parse("#B42318"));

    public static async Task ShowMessageAsync(
        Window owner,
        string title,
        string message,
        string buttonText = "好")
    {
        var window = CreateWindow(title, 520, 240);
        var button = CreateButton(buttonText, primary: true);
        button.Click += (_, _) => window.Close();
        window.Content = CreateDialogLayout(message, [button]);
        await window.ShowDialog(owner);
    }

    public static Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText = "继续",
        bool destructive = false)
    {
        var window = CreateWindow(title, 560, 270);
        var cancel = CreateButton("取消");
        var confirm = CreateButton(confirmText, primary: !destructive, destructive);
        cancel.Click += (_, _) => window.Close(false);
        confirm.Click += (_, _) => window.Close(true);
        window.Content = CreateDialogLayout(message, [cancel, confirm]);
        return window.ShowDialog<bool>(owner);
    }

    public static Task<bool> ConfirmAssetMoveAsync(
        Window owner,
        IReadOnlyList<AssetListItem> assets)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(assets);
        var window = CreateWindow("确认移动到 CDSI 工作目录", 720, 500);
        window.CanResize = true;
        window.MinWidth = 560;
        window.MinHeight = 380;
        var paths = new TextBox
        {
            Text = string.Join(Environment.NewLine, assets.Select(asset => asset.Path)),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Menlo, SF Mono, monospace")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(
            paths,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(
            paths,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var cancel = CreateButton("取消");
        var move = CreateButton("移动", destructive: true);
        cancel.Click += (_, _) => window.Close(false);
        move.Click += (_, _) => window.Close(true);
        var buttonRow = CreateButtonRow([cancel, move]);
        window.Content = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 12,
            Children =
            {
                CreateTitle("移动后将删除以下源文件"),
                new TextBlock
                {
                    Text = $"共 {assets.Count:N0} 个文件。每个文件复制到受管工作目录并通过 SHA-256 校验后，才会删除对应源文件。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = TextBrush
                },
                paths,
                buttonRow
            }
        };
        Grid.SetRow(((Grid)window.Content).Children[1], 1);
        Grid.SetRow(paths, 2);
        Grid.SetRow(buttonRow, 3);
        return window.ShowDialog<bool>(owner);
    }

    public static async Task<MissingDatabaseRecoveryChoice>
        ShowMissingDatabaseRecoveryAsync(
            Window owner,
            string message)
    {
        var window = CreateWindow("检测到 Beacon 状态数据库缺失", 660, 430);
        var exit = CreateButton("退出");
        var createEmpty = CreateButton("创建空白数据库", destructive: true);
        var restore = CreateButton("从备份恢复...", primary: true);
        exit.Click += (_, _) =>
            window.Close(MissingDatabaseRecoveryChoice.Exit);
        createEmpty.Click += (_, _) =>
            window.Close(MissingDatabaseRecoveryChoice.CreateEmpty);
        restore.Click += (_, _) =>
            window.Close(MissingDatabaseRecoveryChoice.Restore);

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextBrush,
            FontSize = 14
        };
        var layout = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 16
        };
        layout.Children.Add(CreateTitle("状态数据库缺失"));
        var scroll = new ScrollViewer
        {
            Content = messageBlock,
            VerticalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        var buttons = CreateButtonRow([exit, createEmpty, restore]);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        window.Content = layout;
        return await window.ShowDialog<MissingDatabaseRecoveryChoice?>(owner) ??
            MissingDatabaseRecoveryChoice.Exit;
    }

    public static async Task<string?> PromptAsync(
        Window owner,
        string title,
        string label,
        string? initialValue = null,
        string confirmText = "保存")
    {
        var window = CreateWindow(title, 520, 220);
        var textBox = new TextBox
        {
            Text = initialValue ?? string.Empty,
            MinWidth = 420,
            MaxLength = 500
        };
        var cancel = CreateButton("取消");
        var confirm = CreateButton(confirmText, primary: true);
        cancel.Click += (_, _) => window.Close(null);
        confirm.Click += (_, _) =>
        {
            var value = textBox.Text?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                window.Close(value);
            }
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                CreateTitle(title),
                new TextBlock { Text = label, Foreground = TextBrush },
                textBox,
                CreateButtonRow([cancel, confirm])
            }
        };
        window.Opened += (_, _) => textBox.Focus();
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task<string?> ChooseFolderAsync(
        Window owner,
        string title,
        string? suggestedPath = null)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(suggestedPath) && Directory.Exists(suggestedPath))
        {
            start = await owner.StorageProvider.TryGetFolderFromPathAsync(suggestedPath);
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = start
            });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> ChooseOpenFileAsync(
        Window owner,
        string title,
        params FilePickerFileType[] fileTypes)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = fileTypes.Length == 0 ? null : fileTypes
            });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> ChooseSaveFileAsync(
        Window owner,
        string title,
        string suggestedName,
        FilePickerFileType fileType)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = fileType.Patterns?.FirstOrDefault()?.TrimStart('*', '.'),
                FileTypeChoices = [fileType],
                ShowOverwritePrompt = true
            });
        return file?.TryGetLocalPath();
    }

    public static async Task<WorkspaceDialogResult?> ShowWorkspaceDialogAsync(
        Window owner,
        string suggestedPath,
        bool firstRun)
    {
        var window = CreateWindow(
            firstRun ? "设置 CDSI 工作目录" : "更改 CDSI 工作目录",
            660,
            250);
        var pathBox = new TextBox { Text = suggestedPath, MinWidth = 500 };
        var browse = CreateButton("选择...");
        browse.Click += async (_, _) =>
        {
            var selected = await ChooseFolderAsync(window, "选择 CDSI 工作目录", pathBox.Text);
            if (selected is not null)
            {
                pathBox.Text = selected;
            }
        };
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pathRow.Children.Add(pathBox);
        Grid.SetColumn(browse, 1);
        browse.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(browse);

        var cancel = CreateButton(firstRun ? "退出" : "取消");
        var confirm = CreateButton(firstRun ? "创建并继续" : "使用此目录", primary: true);
        cancel.Click += (_, _) => window.Close(null);
        confirm.Click += (_, _) =>
        {
            var path = pathBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(path))
            {
                window.Close(new WorkspaceDialogResult(path));
            }
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                CreateTitle(firstRun ? "CDSI 工作目录" : "选择新的工作目录"),
                pathRow,
                new TextBlock
                {
                    Text = "将在该目录创建 Inbox、Assets、Exports、Cache、Temp 和 System；切换时不会移动或删除旧目录内容。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = MutedBrush
                },
                CreateButtonRow([cancel, confirm])
            }
        };
        return await window.ShowDialog<WorkspaceDialogResult?>(owner);
    }

    public static async Task<ProjectDialogResult?> ShowProjectDialogAsync(
        Window owner,
        IReadOnlyList<ConfiguredObjectStorageProfile> profiles,
        string? initialName = null,
        AssetCollectionType initialType = AssetCollectionType.Mixed)
    {
        var editing = initialName is not null;
        var window = CreateWindow(editing ? "编辑项目" : "新建项目", 620, editing ? 290 : 430);
        var nameBox = new TextBox { Text = initialName ?? string.Empty, MaxLength = 120 };
        var typeChoices = new[]
        {
            new ProjectTypeChoice(AssetCollectionType.Video, "视频"),
            new ProjectTypeChoice(AssetCollectionType.Audio, "音频"),
            new ProjectTypeChoice(AssetCollectionType.Image, "图片"),
            new ProjectTypeChoice(AssetCollectionType.Text, "文字"),
            new ProjectTypeChoice(AssetCollectionType.Mixed, "综合")
        };
        var typeBox = new ComboBox
        {
            ItemsSource = typeChoices,
            SelectedItem = typeChoices.Single(item => item.Type == initialType)
        };
        var availableProfiles = profiles.Where(item => item.HasStoredSecret).ToArray();
        var enableBackup = new CheckBox
        {
            Content = availableProfiles.Length == 0
                ? "开启云端备份（暂无可用凭据）"
                : "开启云端备份",
            IsEnabled = availableProfiles.Length > 0 && !editing
        };
        var profileList = new ListBox
        {
            ItemsSource = availableProfiles.Select(item => new StorageProfileChoice(item)).ToArray(),
            SelectionMode = SelectionMode.Multiple,
            Height = 105,
            IsEnabled = false
        };
        enableBackup.IsCheckedChanged += (_, _) =>
            profileList.IsEnabled = enableBackup.IsChecked == true;

        var cancel = CreateButton("取消");
        var confirm = CreateButton(editing ? "保存" : "创建", primary: true);
        cancel.Click += (_, _) => window.Close(null);
        confirm.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim();
            var type = (typeBox.SelectedItem as ProjectTypeChoice)?.Type;
            var selectedProfiles = profileList.SelectedItems?
                .OfType<StorageProfileChoice>()
                .Select(item => item.Profile.Profile.Id)
                .ToArray() ?? [];
            if (!string.IsNullOrWhiteSpace(name) && type is not null &&
                (enableBackup.IsChecked != true || selectedProfiles.Length > 0))
            {
                window.Close(new ProjectDialogResult(name, type.Value, selectedProfiles));
            }
        };

        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
            {
                CreateTitle(editing ? "编辑项目" : "新建项目"),
                new TextBlock { Text = "名称", Foreground = TextBrush },
                nameBox,
                new TextBlock { Text = "类型", Foreground = TextBrush },
                typeBox
            }
        };
        if (!editing)
        {
            content.Children.Add(enableBackup);
            content.Children.Add(profileList);
        }

        content.Children.Add(CreateButtonRow([cancel, confirm]));
        window.Content = content;
        window.Opened += (_, _) => nameBox.Focus();
        return await window.ShowDialog<ProjectDialogResult?>(owner);
    }

    public static async Task<ReaderSubscribeRequest?> ShowReaderSubscriptionDialogAsync(
        Window owner,
        ReaderFeedDialogInitial? initial = null)
    {
        var editing = initial is not null;
        var window = CreateWindow(editing ? "编辑 RSS 订阅" : "添加 RSS 订阅", 620, 380);
        var urlBox = new TextBox { Text = initial?.FeedUrl ?? string.Empty };
        var titleBox = new TextBox { Text = initial?.Title ?? string.Empty };
        var folderBox = new TextBox { Text = initial?.Folder ?? string.Empty };
        var privateNetwork = new CheckBox
        {
            Content = "允许访问本机或局域网地址",
            IsChecked = initial?.AllowPrivateNetwork ?? false
        };
        var cancel = CreateButton("取消");
        var confirm = CreateButton(editing ? "保存" : "添加", primary: true);
        cancel.Click += (_, _) => window.Close(null);
        confirm.Click += (_, _) =>
        {
            var feedUrl = urlBox.Text?.Trim();
            if (Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
            {
                window.Close(new ReaderSubscribeRequest(
                    uri.AbsoluteUri,
                    Optional(titleBox.Text),
                    Optional(folderBox.Text),
                    privateNetwork.IsChecked == true));
            }
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 9,
            Children =
            {
                CreateTitle(editing ? "编辑 RSS 订阅" : "添加 RSS 订阅"),
                new TextBlock { Text = "Feed URL", Foreground = TextBrush },
                urlBox,
                new TextBlock { Text = "显示名称", Foreground = TextBrush },
                titleBox,
                new TextBlock { Text = "文件夹", Foreground = TextBrush },
                folderBox,
                privateNetwork,
                CreateButtonRow([cancel, confirm])
            }
        };
        window.Opened += (_, _) => urlBox.Focus();
        return await window.ShowDialog<ReaderSubscribeRequest?>(owner);
    }

    public static async Task<T?> ChooseAsync<T>(
        Window owner,
        string title,
        string message,
        IReadOnlyList<T> choices,
        string confirmText = "选择") where T : class
    {
        if (choices.Count == 0)
        {
            return null;
        }

        var window = CreateWindow(title, 560, 250);
        var combo = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
        var cancel = CreateButton("取消");
        var confirm = CreateButton(confirmText, primary: true);
        cancel.Click += (_, _) => window.Close(null);
        confirm.Click += (_, _) => window.Close(combo.SelectedItem as T);
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                CreateTitle(title),
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                combo,
                CreateButtonRow([cancel, confirm])
            }
        };
        return await window.ShowDialog<T?>(owner);
    }

    public static async Task ShowTextAsync(
        Window owner,
        string title,
        string text,
        bool monospace = false)
    {
        var window = CreateWindow(title, 760, 580);
        window.CanResize = true;
        window.MinWidth = 560;
        window.MinHeight = 360;
        var close = CreateButton("关闭", primary: true);
        close.Click += (_, _) => window.Close();
        var textBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = monospace
                ? new FontFamily("Menlo, SF Mono, monospace")
                : FontFamily.Default
        };
        ScrollViewer.SetVerticalScrollBarVisibility(
            textBox,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(
            textBox,
            monospace
                ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        window.Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                CreateTitle(title),
                textBox,
                CreateButtonRow([close])
            }
        };
        Grid.SetRow(textBox, 1);
        Grid.SetRow((Control)((Grid)window.Content).Children[2], 2);
        await window.ShowDialog(owner);
    }

    public static async Task<IReadOnlyList<string>?> ShowAssetTagsDialogAsync(
        Window owner,
        IReadOnlyCollection<string> availableTags,
        IReadOnlyCollection<string> selectedTags)
    {
        var window = CreateWindow("管理资产标签", 560, 500);
        window.CanResize = true;
        var selected = selectedTags.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var checkPanel = new StackPanel { Spacing = 6 };
        var checkBoxes = new List<CheckBox>();

        void AddTag(string name, bool isChecked)
        {
            var normalized = name.Trim();
            if (normalized.Length == 0 || checkBoxes.Any(item =>
                    string.Equals(item.Content as string, normalized,
                        StringComparison.CurrentCultureIgnoreCase)))
            {
                return;
            }

            var checkBox = new CheckBox
            {
                Content = normalized,
                IsChecked = isChecked
            };
            checkBoxes.Add(checkBox);
            checkPanel.Children.Add(checkBox);
        }

        foreach (var tag in availableTags
                     .Concat(selectedTags)
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase))
        {
            AddTag(tag, selected.Contains(tag));
        }

        var customTag = new TextBox
        {
            Watermark = "新标签名称",
            MaxLength = 40
        };
        var add = CreateButton("添加");
        add.Click += (_, _) =>
        {
            var name = customTag.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                AddTag(name, isChecked: true);
                customTag.Clear();
            }
        };
        var customRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        customRow.Children.Add(customTag);
        Grid.SetColumn(add, 1);
        add.Margin = new Thickness(8, 0, 0, 0);
        customRow.Children.Add(add);

        var cancel = CreateButton("取消");
        var save = CreateButton("保存", primary: true);
        cancel.Click += (_, _) => window.Close(null);
        save.Click += (_, _) => window.Close(
            checkBoxes
                .Where(item => item.IsChecked == true)
                .Select(item => (string)item.Content!)
                .ToArray());

        var layout = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 12
        };
        var title = CreateTitle("管理资产标签");
        layout.Children.Add(title);
        Grid.SetRow(customRow, 1);
        layout.Children.Add(customRow);
        var scroll = new ScrollViewer { Content = checkPanel };
        Grid.SetRow(scroll, 2);
        layout.Children.Add(scroll);
        var buttons = CreateButtonRow([cancel, save]);
        Grid.SetRow(buttons, 3);
        layout.Children.Add(buttons);
        window.Content = layout;
        return await window.ShowDialog<IReadOnlyList<string>?>(owner);
    }

    public static async Task<OpenWebPublishDialogResult?>
        ShowOpenWebPublishDialogAsync(
            Window owner,
            string sourcePath,
            string initialTitle,
            IReadOnlyList<ConfiguredOpenWebSource> sources)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        var window = CreateWindow("发布到 OpenWeb", 640, 390);
        var titleBox = new TextBox { Text = initialTitle, MaxLength = 200 };
        var sourceOptions = sources.Select(item => new OpenWebSourceChoice(item)).ToArray();
        var sourceBox = new ComboBox
        {
            ItemsSource = sourceOptions,
            SelectedItem = sourceOptions.FirstOrDefault(item => item.Source.Source.IsDefault) ??
                sourceOptions[0]
        };
        var statusOptions = new[]
        {
            new OpenWebStatusChoice("保存为草稿", OpenWebArticleStatus.Draft),
            new OpenWebStatusChoice("立即发布", OpenWebArticleStatus.Published)
        };
        var statusBox = new ComboBox
        {
            ItemsSource = statusOptions,
            SelectedIndex = 0
        };
        var cancel = CreateButton("取消");
        var publish = CreateButton("发布", primary: true);
        cancel.Click += (_, _) => window.Close(null);
        publish.Click += (_, _) =>
        {
            var title = titleBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(title) &&
                sourceBox.SelectedItem is OpenWebSourceChoice source &&
                statusBox.SelectedItem is OpenWebStatusChoice status)
            {
                window.Close(new OpenWebPublishDialogResult(
                    source.Source.Source.Id,
                    title,
                    status.Status));
            }
        };

        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 9,
            Children =
            {
                CreateTitle("发布文章"),
                new TextBlock
                {
                    Text = sourcePath,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = MutedBrush
                },
                new TextBlock { Text = "标题", Foreground = TextBrush },
                titleBox,
                new TextBlock { Text = "目标源站", Foreground = TextBrush },
                sourceBox,
                new TextBlock { Text = "发布状态", Foreground = TextBrush },
                statusBox,
                CreateButtonRow([cancel, publish])
            }
        };
        return await window.ShowDialog<OpenWebPublishDialogResult?>(owner);
    }

    public static async Task<ObjectStorageRestoreDestination?>
        ChooseRestoreDestinationAsync(Window owner)
    {
        var window = CreateWindow("选择取回位置", 560, 250);
        var cancel = CreateButton("取消");
        var choose = CreateButton("选择其他目录...");
        var workspace = CreateButton("CDSI 工作目录", primary: true);
        cancel.Click += (_, _) => window.Close(null);
        workspace.Click += (_, _) => window.Close(
            new ObjectStorageRestoreDestination(
                ObjectStorageRestoreDestinationKind.ManagedWorkspace));
        choose.Click += async (_, _) =>
        {
            var path = await ChooseFolderAsync(window, "选择资产取回目录");
            if (path is not null)
            {
                window.Close(new ObjectStorageRestoreDestination(
                    ObjectStorageRestoreDestinationKind.SelectedDirectory,
                    path));
            }
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                CreateTitle("从云端取回"),
                new TextBlock
                {
                    Text = "可取回到受管理的 CDSI 工作目录，或选择一个普通目录。",
                    TextWrapping = TextWrapping.Wrap
                },
                CreateButtonRow([cancel, choose, workspace])
            }
        };
        return await window.ShowDialog<ObjectStorageRestoreDestination?>(owner);
    }

    private static Window CreateWindow(string title, double width, double height) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = Math.Min(width, 480),
        MinHeight = Math.Min(height, 200),
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brushes.White
    };

    private static Control CreateDialogLayout(string message, IReadOnlyList<Button> buttons) =>
        new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = TextBrush,
                    FontSize = 14,
                    MaxHeight = 150
                },
                CreateButtonRow(buttons)
            }
        };

    private static TextBlock CreateTitle(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        Foreground = TextBrush
    };

    private static StackPanel CreateButtonRow(IReadOnlyList<Button> buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        foreach (var button in buttons)
        {
            panel.Children.Add(button);
        }

        return panel;
    }

    private static Button CreateButton(
        string text,
        bool primary = false,
        bool destructive = false) => new()
    {
        Content = text,
        MinWidth = 88,
        MinHeight = 32,
        Padding = new Thickness(14, 4),
        Background = destructive ? DangerBrush : primary ? PrimaryBrush : Brushes.Transparent,
        Foreground = primary || destructive ? Brushes.White : TextBrush
    };

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ProjectTypeChoice(AssetCollectionType Type, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record StorageProfileChoice(ConfiguredObjectStorageProfile Profile)
    {
        public override string ToString() =>
            $"{Profile.Profile.DisplayName} · {Profile.Profile.Provider} · {Profile.Profile.BucketName}";
    }

    private sealed record OpenWebSourceChoice(ConfiguredOpenWebSource Source)
    {
        public override string ToString() =>
            $"{Source.Source.DisplayName} · {Source.Source.OriginDomain}" +
            (Source.HasApplicationPassword ? string.Empty : "（凭据缺失）");
    }

    private sealed record OpenWebStatusChoice(
        string DisplayName,
        OpenWebArticleStatus Status)
    {
        public override string ToString() => DisplayName;
    }
}

internal sealed record WorkspaceDialogResult(string Path);

internal sealed record ProjectDialogResult(
    string Name,
    AssetCollectionType Type,
    IReadOnlyList<Guid> BackupProfileIds);

internal sealed record ReaderFeedDialogInitial(
    string FeedUrl,
    string? Title,
    string? Folder,
    bool AllowPrivateNetwork);

internal sealed record OpenWebPublishDialogResult(
    Guid SourceId,
    string Title,
    OpenWebArticleStatus Status);

internal enum MissingDatabaseRecoveryChoice
{
    Exit,
    CreateEmpty,
    Restore
}
