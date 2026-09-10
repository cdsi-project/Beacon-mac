using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CDSI.Agent.Application.Git;
using CDSI.Agent.Application.OpenWeb;
using CDSI.Agent.Application.Storage;
using CDSI.Agent.Core.Assets;
using CDSI.Agent.Core.Git;
using CDSI.Agent.Core.OpenWeb;
using CDSI.Agent.Core.Scanning;
using CDSI.Agent.Core.Storage;
using CDSI.Agent.Mac.Interaction;

namespace CDSI.Agent.Mac.Settings;

internal static class SettingsDialogs
{
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#343D45"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#657078"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#B42318"));
    private static readonly IBrush PrimaryBrush = new SolidColorBrush(Color.Parse("#18794E"));

    public static async Task ShowScanRootEditorAsync(
        Window owner,
        ScanRoot? root,
        Func<ScanRootEditorResult, Task<string?>> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(saveAsync);
        var adding = root is null;
        var window = CreateWindow(
            adding ? "添加扫描目录" : "设置扫描目录",
            720,
            590,
            minimumHeight: 520);
        var filter = root?.CreateFileFilter() ?? new ScanFileFilter(
            ScanFileFilter.AllFileTypes,
            []);
        var schedule = root?.GetIdleScanSchedule() ?? IdleScanSchedule.Disabled;

        var pathBox = new TextBox
        {
            Text = root?.Path ?? string.Empty,
            IsReadOnly = !adding,
            MinWidth = 500,
            Watermark = "选择只读扫描目录"
        };
        var browseButton = CreateButton("选择...", subtle: true);
        browseButton.IsEnabled = adding;
        browseButton.Click += async (_, _) =>
        {
            var selected = await UiDialogs.ChooseFolderAsync(
                window,
                "选择只读扫描目录",
                pathBox.Text);
            if (selected is not null)
            {
                pathBox.Text = selected;
            }
        };
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pathRow.Children.Add(pathBox);
        Grid.SetColumn(browseButton, 1);
        browseButton.Margin = new Thickness(8, 0, 0, 0);
        pathRow.Children.Add(browseButton);

        var fileTypeChoices = new[]
        {
            new FileTypeChoice(AssetFileTypeFilter.Video, "视频"),
            new FileTypeChoice(AssetFileTypeFilter.Audio, "音频"),
            new FileTypeChoice(AssetFileTypeFilter.Image, "图片"),
            new FileTypeChoice(AssetFileTypeFilter.Document, "文档"),
            new FileTypeChoice(AssetFileTypeFilter.Other, "其他")
        };
        var fileTypeChecks = fileTypeChoices.ToDictionary(
            choice => choice.Value,
            choice => new CheckBox
            {
                Content = choice.Label,
                IsChecked = filter.FileTypeFilters.Contains(choice.Value),
                VerticalAlignment = VerticalAlignment.Center
            });
        var fileTypeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14
        };
        foreach (var choice in fileTypeChoices)
        {
            fileTypeRow.Children.Add(fileTypeChecks[choice.Value]);
        }

        var useExtensions = new CheckBox
        {
            Content = "同时使用扩展名白名单",
            IsChecked = filter.UsesExtensionWhitelist
        };
        fileTypeRow.Children.Add(useExtensions);
        var extensionsBox = new TextBox
        {
            Text = string.Join(", ", filter.ExtensionWhitelist),
            AcceptsReturn = true,
            Height = 72,
            TextWrapping = TextWrapping.Wrap,
            Watermark = ".mp4, .mov, .jpg"
        };
        extensionsBox.IsEnabled = useExtensions.IsChecked == true;
        useExtensions.IsCheckedChanged += (_, _) =>
            extensionsBox.IsEnabled = useExtensions.IsChecked == true;

        var idleEnabled = new CheckBox
        {
            Content = "空闲时扫描",
            IsChecked = schedule.Enabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        var intervalInput = new NumericUpDown
        {
            Minimum = IdleScanSchedule.MinimumInterval,
            Maximum = IdleScanSchedule.MaximumInterval,
            Increment = 1,
            Value = schedule.Interval,
            Width = 86,
            FormatString = "0"
        };
        var unitChoices = new[]
        {
            new IdleUnitChoice(IdleScanIntervalUnit.Minutes, "分钟"),
            new IdleUnitChoice(IdleScanIntervalUnit.Hours, "小时"),
            new IdleUnitChoice(IdleScanIntervalUnit.Days, "天")
        };
        var unitBox = new ComboBox
        {
            ItemsSource = unitChoices,
            SelectedItem = unitChoices.Single(choice => choice.Value == schedule.Unit),
            Width = 100
        };
        intervalInput.IsEnabled = schedule.Enabled;
        unitBox.IsEnabled = schedule.Enabled;
        idleEnabled.IsCheckedChanged += (_, _) =>
        {
            intervalInput.IsEnabled = idleEnabled.IsChecked == true;
            unitBox.IsEnabled = idleEnabled.IsChecked == true;
        };
        var idleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                idleEnabled,
                new TextBlock
                {
                    Text = "每",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = TextBrush
                },
                intervalInput,
                unitBox
            }
        };

        var error = CreateErrorText();
        var cancel = CreateButton("取消", subtle: true);
        var save = CreateButton(adding ? "添加目录" : "保存", primary: true);
        cancel.Click += (_, _) => window.Close();
        save.Click += async (_, _) =>
        {
            if (!TryCreateScanRootDraft(
                    root,
                    pathBox.Text,
                    adding,
                    fileTypeChoices,
                    fileTypeChecks,
                    useExtensions.IsChecked == true,
                    extensionsBox.Text,
                    idleEnabled.IsChecked == true,
                    intervalInput.Value,
                    (unitBox.SelectedItem as IdleUnitChoice)?.Value,
                    out var draft,
                    out var validationError))
            {
                error.Text = validationError;
                return;
            }

            await SubmitAsync(
                window,
                [cancel, save],
                error,
                () => saveAsync(draft!),
                closeOnSuccess: true);
        };

        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
            {
                CreateTitle(adding ? "添加扫描目录" : "编辑扫描设置"),
                CreateLabel("扫描目录"),
                pathRow,
                CreateLabel("扫描策略"),
                fileTypeRow,
                CreateLabel("扩展名白名单（可用逗号、分号、空格或换行分隔）"),
                extensionsBox,
                idleRow,
                error,
                CreateButtonRow([cancel, save])
            }
        };
        window.Content = new ScrollViewer { Content = content };
        window.Opened += (_, _) => pathBox.Focus();
        await window.ShowDialog(owner);
    }

    public static async Task ShowStorageProfileEditorAsync(
        Window owner,
        ObjectStorageProfile? profile,
        Func<SaveObjectStorageProfileRequest, Task<string?>> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(saveAsync);
        var adding = profile is null;
        var window = CreateWindow(
            adding ? "添加备份配置" : "编辑备份配置",
            680,
            620,
            minimumHeight: 560);
        var providers = new[]
        {
            new StorageProviderChoice(ObjectStorageProvider.AliyunOss, "阿里云 OSS"),
            new StorageProviderChoice(ObjectStorageProvider.QiniuKodo, "七牛云 Kodo"),
            new StorageProviderChoice(ObjectStorageProvider.TencentCos, "腾讯云 COS")
        };
        var providerBox = new ComboBox
        {
            ItemsSource = providers,
            SelectedItem = providers.Single(choice =>
                choice.Value == (profile?.Provider ?? ObjectStorageProvider.AliyunOss))
        };
        var nameBox = new TextBox { Text = profile?.DisplayName ?? "主备份", MaxLength = 100 };
        var endpointBox = new TextBox
        {
            Text = profile?.Endpoint ?? "oss-cn-hangzhou.aliyuncs.com",
            MaxLength = 255
        };
        var bucketBox = new TextBox { Text = profile?.BucketName ?? string.Empty, MaxLength = 63 };
        var regionBox = new TextBox
        {
            Text = profile?.Region ?? "cn-hangzhou",
            MaxLength = 100
        };
        var accessKeyIdBox = new TextBox
        {
            Text = profile?.AccessKeyId ?? string.Empty,
            MaxLength = 128
        };
        var secretBox = new TextBox
        {
            PasswordChar = '*',
            Watermark = adding ? "必填" : "留空保留现有凭据"
        };
        var useHttps = new CheckBox
        {
            Content = "使用 HTTPS",
            IsChecked = profile?.UseHttps ?? true
        };
        providerBox.SelectionChanged += (_, _) =>
        {
            if (providerBox.SelectedItem is not StorageProviderChoice choice)
            {
                return;
            }

            var detected = DetectStorageProvider(endpointBox.Text);
            var defaults = GetStorageDefaults(choice.Value);
            if (string.IsNullOrWhiteSpace(endpointBox.Text) || detected is not null)
            {
                endpointBox.Text = defaults.Endpoint;
            }

            if (string.IsNullOrWhiteSpace(regionBox.Text) || detected is not null)
            {
                regionBox.Text = defaults.Region;
            }
        };

        var form = CreateFormGrid(8);
        AddFormField(form, 0, "提供商", providerBox);
        AddFormField(form, 1, "配置名称", nameBox);
        AddFormField(form, 2, "Endpoint", endpointBox);
        AddFormField(form, 3, "Bucket", bucketBox);
        AddFormField(form, 4, "地域 / Region ID", regionBox);
        AddFormField(form, 5, "AccessKey ID / SecretId", accessKeyIdBox);
        AddFormField(form, 6, "AccessKey Secret / SecretKey", secretBox);
        AddFormField(form, 7, string.Empty, useHttps);

        var error = CreateErrorText();
        var cancel = CreateButton("取消", subtle: true);
        var save = CreateButton("保存", primary: true);
        cancel.Click += (_, _) => window.Close();
        save.Click += async (_, _) =>
        {
            var request = new SaveObjectStorageProfileRequest(
                profile?.Id,
                nameBox.Text ?? string.Empty,
                endpointBox.Text ?? string.Empty,
                bucketBox.Text ?? string.Empty,
                regionBox.Text,
                useHttps.IsChecked == true,
                accessKeyIdBox.Text ?? string.Empty,
                string.IsNullOrEmpty(secretBox.Text) ? null : secretBox.Text,
                (providerBox.SelectedItem as StorageProviderChoice)?.Value ??
                    ObjectStorageProvider.AliyunOss);
            await SubmitAsync(
                window,
                [cancel, save],
                error,
                () => saveAsync(request),
                closeOnSuccess: true);
        };

        window.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    CreateTitle(adding ? "添加备份配置" : "编辑备份配置"),
                    form,
                    new TextBlock
                    {
                        Text = "Secret 仅保存到 macOS 钥匙串，不写入 CDSI 数据库。",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = MutedBrush
                    },
                    error,
                    CreateButtonRow([cancel, save])
                }
            }
        };
        window.Opened += (_, _) => nameBox.Focus();
        await window.ShowDialog(owner);
    }

    public static async Task ShowOpenWebSourceEditorAsync(
        Window owner,
        OpenWebSource? source,
        Func<SaveOpenWebSourceRequest, Task<string?>> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(saveAsync);
        var adding = source is null;
        var window = CreateWindow(
            adding ? "添加 OpenWeb 源站" : "编辑 OpenWeb 源站",
            640,
            480,
            minimumHeight: 440);
        var nameBox = new TextBox { Text = source?.DisplayName ?? string.Empty, MaxLength = 100 };
        var domainBox = new TextBox
        {
            Text = source?.OriginDomain ?? string.Empty,
            Watermark = "example.com"
        };
        var usernameBox = new TextBox
        {
            Text = source?.WordPressUsername ?? string.Empty,
            MaxLength = 100
        };
        var passwordBox = new TextBox
        {
            PasswordChar = '*',
            Watermark = adding ? "必填" : "留空保留现有凭据"
        };
        var isDefault = new CheckBox
        {
            Content = "设为默认源站",
            IsChecked = source?.IsDefault ?? false,
            IsEnabled = source?.IsDefault != true
        };
        var form = CreateFormGrid(5);
        AddFormField(form, 0, "源站名称", nameBox);
        AddFormField(form, 1, "源站域名", domainBox);
        AddFormField(form, 2, "WordPress 用户名", usernameBox);
        AddFormField(form, 3, "应用程序密码", passwordBox);
        AddFormField(form, 4, string.Empty, isDefault);

        var error = CreateErrorText();
        var cancel = CreateButton("取消", subtle: true);
        var save = CreateButton("保存", primary: true);
        cancel.Click += (_, _) => window.Close();
        save.Click += async (_, _) =>
        {
            var request = new SaveOpenWebSourceRequest(
                source?.Id,
                nameBox.Text ?? string.Empty,
                domainBox.Text ?? string.Empty,
                usernameBox.Text ?? string.Empty,
                string.IsNullOrEmpty(passwordBox.Text) ? null : passwordBox.Text,
                isDefault.IsChecked == true);
            await SubmitAsync(
                window,
                [cancel, save],
                error,
                () => saveAsync(request),
                closeOnSuccess: true);
        };

        window.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                CreateTitle(adding ? "添加 OpenWeb 源站" : "编辑 OpenWeb 源站"),
                form,
                new TextBlock
                {
                    Text = "应用程序密码按源站独立保存到 macOS 钥匙串。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = MutedBrush
                },
                error,
                CreateButtonRow([cancel, save])
            }
        };
        window.Opened += (_, _) => nameBox.Focus();
        await window.ShowDialog(owner);
    }

    public static async Task ShowGitProfileEditorAsync(
        Window owner,
        GitProfile? profile,
        Func<SaveGitProfileRequest, Task<string?>> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(saveAsync);
        var adding = profile is null;
        var window = CreateWindow(
            adding ? "添加 Git 配置" : "编辑 Git 配置",
            760,
            650,
            minimumHeight: 570);
        var providers = new[]
        {
            new GitProviderChoice(GitHostingProvider.GitHub, "GitHub"),
            new GitProviderChoice(GitHostingProvider.Gitee, "Gitee（码云）")
        };
        var authenticationMethods = new[]
        {
            new GitAuthenticationChoice(GitAuthenticationMethod.Password, "密码"),
            new GitAuthenticationChoice(GitAuthenticationMethod.Ssh, "SSH")
        };
        var providerBox = new ComboBox
        {
            ItemsSource = providers,
            SelectedItem = providers.Single(choice =>
                choice.Value == (profile?.Provider ?? GitHostingProvider.GitHub))
        };
        var nameBox = new TextBox { Text = profile?.DisplayName ?? "主 Git 仓库", MaxLength = 100 };
        var repositoryBox = new TextBox
        {
            Text = profile?.RepositoryUrl ?? string.Empty
        };
        var branchBox = new TextBox
        {
            Text = profile?.DefaultBranch ?? "main",
            MaxLength = 255
        };
        var authenticationBox = new ComboBox
        {
            ItemsSource = authenticationMethods,
            SelectedItem = authenticationMethods.Single(choice =>
                choice.Value == (profile?.AuthenticationMethod ??
                    GitAuthenticationMethod.Password))
        };
        var usernameBox = new TextBox
        {
            Text = profile?.Username ?? string.Empty,
            MaxLength = 100
        };
        var passwordBox = new TextBox
        {
            PasswordChar = '*',
            Watermark = profile is null ||
                profile.AuthenticationMethod == GitAuthenticationMethod.Ssh
                    ? "必填；GitHub 可使用个人访问令牌作为密码"
                    : "留空保留现有密码"
        };
        var sshKeyBox = new TextBox
        {
            Text = profile?.SshPublicKeyPath ?? string.Empty,
            IsReadOnly = true,
            Watermark = "选择 ~/.ssh 中的 .pub 公钥"
        };
        var error = CreateErrorText();
        var cancel = CreateButton("取消", subtle: true);
        var save = CreateButton("保存", primary: true);
        var chooseSshKey = CreateButton("选择公钥...", subtle: true);
        chooseSshKey.Click += async (_, _) =>
        {
            var selected = await UiDialogs.ChooseOpenFileAsync(
                window,
                "选择 SSH 公钥",
                new FilePickerFileType("SSH 公钥") { Patterns = ["*.pub"] },
                FilePickerFileTypes.All);
            if (selected is not null)
            {
                sshKeyBox.Text = selected;
            }
        };
        var generateSshKey = CreateButton("生成新密钥...", subtle: true);
        var sshKeyGeneration = new SettingsDialogAsyncOperation();
        window.Closing += (_, e) =>
        {
            if (sshKeyGeneration.IsRunning)
            {
                e.Cancel = true;
            }
        };
        generateSshKey.Click += async (_, _) =>
        {
            await sshKeyGeneration.RunAsync(
                async () =>
                {
                    var sshDirectory = SshKeySupport.GetDefaultSshDirectory();
                    var keyPair = SshKeySupport.CreateUnusedKeyPairPaths(sshDirectory);
                    if (!await UiDialogs.ConfirmAsync(
                            window,
                            "生成新 SSH 密钥",
                            "Beacon 将在 Terminal 中打开系统 ssh-keygen，并使用以下未占用位置：\n\n" +
                            $"{keyPair.PrivateKeyPath}\n\n" +
                            "请在 Terminal 中自行设置密钥口令。生成后，系统 ssh-add 会请求把密钥加入 macOS 钥匙串，供非交互 Git 同步使用。Beacon 不会覆盖、读取或保存已有私钥。",
                            "打开 Terminal"))
                    {
                        return;
                    }

                    var directoryExisted = Directory.Exists(sshDirectory);
                    Directory.CreateDirectory(sshDirectory);
                    if (!directoryExisted && !OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(
                            sshDirectory,
                            UnixFileMode.UserRead |
                            UnixFileMode.UserWrite |
                            UnixFileMode.UserExecute);
                    }

                    using var process = Process.Start(
                        SshKeySupport.CreateSshKeyGenerationStartInfo(
                            usernameBox.Text,
                            keyPair.PrivateKeyPath)) ??
                        throw new InvalidOperationException("无法打开 Terminal 中的 ssh-keygen。");
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException(
                            $"Terminal 启动请求失败，退出代码为 {process.ExitCode}。");
                    }

                    if (!window.IsVisible)
                    {
                        return;
                    }

                    await UiDialogs.ShowMessageAsync(
                        window,
                        "正在生成 SSH 密钥",
                        "请在 Terminal 中完成 ssh-keygen 和 ssh-add 的提示，确认密钥已加入钥匙串。完成后回到 Beacon，再点“检查生成结果”。",
                        "检查生成结果");
                    if (!window.IsVisible)
                    {
                        return;
                    }

                    if (SshKeySupport.IsUsablePublicKeyPath(keyPair.PublicKeyPath))
                    {
                        sshKeyBox.Text = keyPair.PublicKeyPath;
                    }
                    else
                    {
                        await UiDialogs.ShowMessageAsync(
                            window,
                            "未发现 SSH 密钥",
                            "尚未发现完整的公钥和私钥对。您可以再次生成，或选择已有的 .pub 公钥。");
                    }
                },
                async exception =>
                {
                    if (!window.IsVisible)
                    {
                        return;
                    }

                    error.Text = $"无法打开系统 ssh-keygen：{exception.Message}";
                    await UiDialogs.ShowMessageAsync(
                        window,
                        "生成新 SSH 密钥失败",
                        $"{error.Text}\n\n请确认 macOS OpenSSH 和 Terminal 可用。");
                },
                running =>
                {
                    if (!running && !window.IsVisible)
                    {
                        return;
                    }

                    cancel.IsEnabled = !running;
                    save.IsEnabled = !running;
                    chooseSshKey.IsEnabled = !running;
                    generateSshKey.IsEnabled = !running;
                });
        };
        var sshKeyRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto")
        };
        sshKeyRow.Children.Add(sshKeyBox);
        Grid.SetColumn(chooseSshKey, 1);
        chooseSshKey.Margin = new Thickness(8, 0, 0, 0);
        sshKeyRow.Children.Add(chooseSshKey);
        Grid.SetColumn(generateSshKey, 2);
        generateSshKey.Margin = new Thickness(8, 0, 0, 0);
        sshKeyRow.Children.Add(generateSshKey);
        var isDefault = new CheckBox
        {
            Content = "设为默认 Git 配置",
            IsChecked = profile?.IsDefault ?? false,
            IsEnabled = profile?.IsDefault != true
        };

        var form = CreateFormGrid(8);
        AddFormField(form, 0, "配置名称", nameBox);
        AddFormField(form, 1, "平台", providerBox);
        AddFormField(form, 2, "仓库地址", repositoryBox);
        AddFormField(form, 3, "默认分支", branchBox);
        AddFormField(form, 4, "访问方式", authenticationBox);
        AddFormField(form, 5, "用户名", usernameBox);
        AddFormField(form, 6, "密码", passwordBox);
        AddFormField(form, 7, "SSH 公钥", sshKeyRow);

        void UpdateAuthenticationFields()
        {
            var method = (authenticationBox.SelectedItem as GitAuthenticationChoice)?.Value ??
                GitAuthenticationMethod.Password;
            var passwordMode = method == GitAuthenticationMethod.Password;
            usernameBox.IsVisible = passwordMode;
            passwordBox.IsVisible = passwordMode;
            form.Children.OfType<TextBlock>()
                .Where(label => Grid.GetRow(label) is 5 or 6)
                .ToList()
                .ForEach(label => label.IsVisible = passwordMode);
            sshKeyRow.IsVisible = !passwordMode;
            form.Children.OfType<TextBlock>()
                .Where(label => Grid.GetRow(label) == 7)
                .ToList()
                .ForEach(label => label.IsVisible = !passwordMode);
            if (!passwordMode && string.IsNullOrWhiteSpace(sshKeyBox.Text))
            {
                sshKeyBox.Text = SshKeySupport.FindDefaultKeyPair(
                    SshKeySupport.GetDefaultSshDirectory())?.PublicKeyPath ?? string.Empty;
            }

            UpdateGitRepositoryWatermark(providerBox, authenticationBox, repositoryBox);
        }

        authenticationBox.SelectionChanged += (_, _) => UpdateAuthenticationFields();
        providerBox.SelectionChanged += (_, _) =>
        {
            UpdateGitRepositoryWatermark(providerBox, authenticationBox, repositoryBox);
            if (adding && branchBox.Text is "main" or "master")
            {
                branchBox.Text = (providerBox.SelectedItem as GitProviderChoice)?.Value ==
                    GitHostingProvider.Gitee
                        ? "master"
                        : "main";
            }
        };
        UpdateAuthenticationFields();

        cancel.Click += (_, _) => window.Close();
        save.Click += async (_, _) =>
        {
            var method = (authenticationBox.SelectedItem as GitAuthenticationChoice)?.Value ??
                GitAuthenticationMethod.Password;
            var request = new SaveGitProfileRequest(
                profile?.Id,
                nameBox.Text ?? string.Empty,
                (providerBox.SelectedItem as GitProviderChoice)?.Value ??
                    GitHostingProvider.GitHub,
                repositoryBox.Text ?? string.Empty,
                branchBox.Text ?? string.Empty,
                method,
                method == GitAuthenticationMethod.Password ? usernameBox.Text : null,
                method == GitAuthenticationMethod.Password && !string.IsNullOrEmpty(passwordBox.Text)
                    ? passwordBox.Text
                    : null,
                method == GitAuthenticationMethod.Ssh ? sshKeyBox.Text : null,
                isDefault.IsChecked == true);
            await SubmitAsync(
                window,
                [cancel, save],
                error,
                () => saveAsync(request),
                closeOnSuccess: true);
        };

        window.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children =
                {
                    CreateTitle(adding ? "添加 Git 配置" : "编辑 Git 配置"),
                    form,
                    isDefault,
                    new TextBlock
                    {
                        Text = "密码仅保存到 macOS 钥匙串。SSH 模式只记录公钥路径，Beacon 不读取或保存私钥。",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = MutedBrush
                    },
                    error,
                    CreateButtonRow([cancel, save])
                }
            }
        };
        window.Opened += (_, _) => nameBox.Focus();
        await window.ShowDialog(owner);
    }

    private static bool TryCreateScanRootDraft(
        ScanRoot? root,
        string? pathText,
        bool requireAvailablePath,
        IReadOnlyList<FileTypeChoice> choices,
        IReadOnlyDictionary<AssetFileTypeFilter, CheckBox> checkBoxes,
        bool useExtensions,
        string? extensionText,
        bool idleEnabled,
        decimal? intervalValue,
        IdleScanIntervalUnit? unit,
        out ScanRootEditorResult? draft,
        out string? error)
    {
        draft = null;
        error = null;
        try
        {
            var path = pathText?.Trim();
            if (string.IsNullOrWhiteSpace(path) ||
                (requireAvailablePath && !Directory.Exists(path)))
            {
                error = "请选择当前可用的扫描目录。";
                return false;
            }

            var fileTypes = choices
                .Where(choice => checkBoxes[choice.Value].IsChecked == true)
                .Select(choice => choice.Value)
                .ToArray();
            var extensions = useExtensions
                ? ScanFileFilter.NormalizeExtensions(SplitExtensions(extensionText))
                : [];
            if (useExtensions && extensions.Count == 0)
            {
                error = "请至少添加一个文件扩展名。";
                return false;
            }

            _ = new ScanFileFilter(fileTypes, extensions);
            var interval = decimal.ToInt32(intervalValue ?? 1);
            var schedule = new IdleScanSchedule(
                idleEnabled,
                interval,
                unit ?? IdleScanIntervalUnit.Hours);
            draft = new ScanRootEditorResult(
                root?.Id,
                Path.GetFullPath(path),
                fileTypes,
                extensions,
                schedule);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static IEnumerable<string> SplitExtensions(string? value)
    {
        return (value ?? string.Empty).Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static ObjectStorageProvider? DetectStorageProvider(string? endpoint)
    {
        if (endpoint?.Contains("aliyuncs.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ObjectStorageProvider.AliyunOss;
        }

        if (endpoint?.Contains("qiniucs.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ObjectStorageProvider.QiniuKodo;
        }

        if (endpoint?.Contains("myqcloud.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ObjectStorageProvider.TencentCos;
        }

        return null;
    }

    private static StorageDefaults GetStorageDefaults(ObjectStorageProvider provider) =>
        provider switch
        {
            ObjectStorageProvider.QiniuKodo =>
                new StorageDefaults("s3.cn-east-1.qiniucs.com", "cn-east-1"),
            ObjectStorageProvider.TencentCos =>
                new StorageDefaults("cos.ap-guangzhou.myqcloud.com", "ap-guangzhou"),
            _ => new StorageDefaults("oss-cn-hangzhou.aliyuncs.com", "cn-hangzhou")
        };

    private static void UpdateGitRepositoryWatermark(
        ComboBox providerBox,
        ComboBox authenticationBox,
        TextBox repositoryBox)
    {
        var provider = (providerBox.SelectedItem as GitProviderChoice)?.Value ??
            GitHostingProvider.GitHub;
        var method = (authenticationBox.SelectedItem as GitAuthenticationChoice)?.Value ??
            GitAuthenticationMethod.Password;
        repositoryBox.Watermark = (provider, method) switch
        {
            (GitHostingProvider.GitHub, GitAuthenticationMethod.Password) =>
                "https://github.com/owner/repository.git",
            (GitHostingProvider.Gitee, GitAuthenticationMethod.Password) =>
                "https://gitee.com/owner/repository.git",
            (GitHostingProvider.GitHub, GitAuthenticationMethod.Ssh) =>
                "git@github.com:owner/repository.git",
            _ => "git@gitee.com:owner/repository.git"
        };
    }

    private static async Task SubmitAsync(
        Window window,
        IReadOnlyList<Button> buttons,
        TextBlock errorText,
        Func<Task<string?>> submitAsync,
        bool closeOnSuccess)
    {
        foreach (var button in buttons)
        {
            button.IsEnabled = false;
        }

        errorText.Text = string.Empty;
        try
        {
            var error = await submitAsync();
            if (error is null)
            {
                if (closeOnSuccess)
                {
                    window.Close();
                }

                return;
            }

            errorText.Text = error;
        }
        catch (Exception exception)
        {
            errorText.Text = exception.Message;
        }
        finally
        {
            foreach (var button in buttons)
            {
                button.IsEnabled = true;
            }
        }
    }

    private static Window CreateWindow(
        string title,
        double width,
        double height,
        double minimumHeight) => new()
    {
        Title = title,
        Width = width,
        Height = height,
        MinWidth = Math.Min(width, 560),
        MinHeight = minimumHeight,
        CanResize = true,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brushes.White
    };

    private static Grid CreateFormGrid(int rowCount)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("170,*"),
            RowSpacing = 9
        };
        for (var index = 0; index < rowCount; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        return grid;
    }

    private static void AddFormField(Grid grid, int row, string label, Control control)
    {
        var labelBlock = CreateLabel(label);
        labelBlock.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(labelBlock, row);
        grid.Children.Add(labelBlock);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static TextBlock CreateTitle(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold,
        Foreground = TextBrush,
        Margin = new Thickness(0, 0, 0, 3)
    };

    private static TextBlock CreateLabel(string text) => new()
    {
        Text = text,
        Foreground = TextBrush,
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock CreateErrorText() => new()
    {
        Foreground = ErrorBrush,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 18
    };

    private static StackPanel CreateButtonRow(IReadOnlyList<Button> buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0)
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
        bool subtle = false) => new()
    {
        Content = text,
        MinWidth = 88,
        MinHeight = 32,
        Padding = new Thickness(13, 4),
        Background = primary
            ? PrimaryBrush
            : subtle
                ? new SolidColorBrush(Color.Parse("#ECEFF2"))
                : Brushes.Transparent,
        Foreground = primary ? Brushes.White : TextBrush
    };

    private sealed record FileTypeChoice(AssetFileTypeFilter Value, string Label);

    private sealed record IdleUnitChoice(IdleScanIntervalUnit Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record StorageProviderChoice(ObjectStorageProvider Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record StorageDefaults(string Endpoint, string Region);

    private sealed record GitProviderChoice(GitHostingProvider Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record GitAuthenticationChoice(
        GitAuthenticationMethod Value,
        string Label)
    {
        public override string ToString() => Label;
    }
}
