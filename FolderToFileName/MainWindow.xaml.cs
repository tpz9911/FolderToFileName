using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace FolderToFileName
{
    public partial class MainWindow : Window
    {
        private readonly AppConfig _config;
        private readonly ObservableCollection<FileRenameItem> _previewItems = [];
        private readonly DispatcherTimer _debounceTimer;

        // Windows 文件名保留非法字符: \ / : * ? " < > | 以及 ASCII 控制字符
        private static readonly HashSet<char> InvalidCharSet = [.. Path.GetInvalidFileNameChars(), '/', '\\'];
        private const int LargeFileThreshold = 5000;

        // 记录最近一次执行成功的重命名映射：<最终文件绝对路径, 原始文件绝对路径, 原先是否为只读>
        private readonly List<(string executedPath, string originalPath, bool wasReadOnly)> _executedHistory = [];
        private bool _isInitializing = true;
        private bool _isExecutedState = false;

        public MainWindow()
        {
            InitializeComponent();
            DgPreview.ItemsSource = _previewItems;

            // 文本框粘贴拦截处理
            DataObject.AddPastingHandler(TxtPrefix, OnTextBoxPaste);
            DataObject.AddPastingHandler(TxtSuffix, OnTextBoxPaste);
            DataObject.AddPastingHandler(TxtSeparator, OnTextBoxPaste);

            // 文本输入采用 300ms 防抖，复选框切换则立即刷新
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounceTimer.Tick += async (s, e) =>
            {
                _debounceTimer.Stop();
                await RefreshPreviewAsync();
            };

            _config = AppConfig.Load();
            ApplyConfigToUi();
            _isInitializing = false;
        }

        private void ApplyConfigToUi()
        {
            ChkPrefix.IsChecked = _config.UsePrefix;
            TxtPrefix.Text = _config.Prefix;
            ChkSuffix.IsChecked = _config.UseSuffix;
            TxtSuffix.Text = _config.Suffix;
            ChkSeparator.IsChecked = _config.UseSeparator;
            TxtSeparator.Text = _config.Separator;
            ChkIncludeSubdirs.IsChecked = _config.IncludeSubdirectories;
            ChkIncludeAllParents.IsChecked = _config.IncludeAllParentFolders;
            ChkLimitExtensions.IsChecked = _config.LimitExtensionsByConfig;
            ChkMoveToRoot.IsChecked = _config.MoveToRootDirectory;
        }

        private void SaveUiToConfig()
        {
            _config.UsePrefix = ChkPrefix.IsChecked == true;
            _config.Prefix = TxtPrefix.Text;
            _config.UseSuffix = ChkSuffix.IsChecked == true;
            _config.Suffix = TxtSuffix.Text;
            _config.UseSeparator = ChkSeparator.IsChecked == true;
            _config.Separator = TxtSeparator.Text;
            _config.IncludeSubdirectories = ChkIncludeSubdirs.IsChecked == true;
            _config.IncludeAllParentFolders = ChkIncludeAllParents.IsChecked == true;
            _config.LimitExtensionsByConfig = ChkLimitExtensions.IsChecked == true;
            _config.MoveToRootDirectory = ChkMoveToRoot.IsChecked == true;
            _config.Save();
        }

        private void ResetUndoState()
        {
            if (_isExecutedState)
            {
                _isExecutedState = false;
                _executedHistory.Clear();
                BtnUndo.IsEnabled = false;
            }
        }

        // 键盘键入时直接阻止非法字符
        private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Any(c => InvalidCharSet.Contains(c)))
            {
                e.Handled = true; // 阻止键入
                System.Media.SystemSounds.Beep.Play();
            }
        }

        // 粘贴时拦截非法字符
        private void OnTextBoxPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText, true))
            {
                string? text = e.DataObject.GetData(DataFormats.UnicodeText, true) as string;
                if (!string.IsNullOrEmpty(text) && text.Any(c => InvalidCharSet.Contains(c)))
                {
                    e.CancelCommand(); // 取消粘贴
                    System.Media.SystemSounds.Beep.Play();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        // 选项（勾选框）状态改变，立即刷新
        private async void OnOptionChangedImmediate(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            ResetUndoState();
            SaveUiToConfig();
            _debounceTimer.Stop();
            await RefreshPreviewAsync();
        }

        // 文本框输入变动，防抖刷新
        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;
            ResetUndoState();
            SaveUiToConfig();
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择目标目录"
            };

            if (dialog.ShowDialog() == true)
            {
                string selectedPath = dialog.FolderName;

                string? root = Path.GetPathRoot(selectedPath);
                if (string.Equals(root?.TrimEnd('\\'), selectedPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("目标目录不能为盘符根目录，请选择具体的子文件夹！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                TxtTargetDir.Text = selectedPath;
                ResetUndoState();
                await RefreshPreviewAsync();
            }
        }

        private async Task RefreshPreviewAsync()
        {
            string targetDir = TxtTargetDir.Text.Trim();
            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            {
                _previewItems.Clear();
                BtnExecute.IsEnabled = false;
                return;
            }

            bool includeSub = ChkIncludeSubdirs.IsChecked == true;
            bool limitExt = ChkLimitExtensions.IsChecked == true;
            bool includeAllParents = ChkIncludeAllParents.IsChecked == true;
            bool moveToRoot = ChkMoveToRoot.IsChecked == true;
            string prefix = ChkPrefix.IsChecked == true ? TxtPrefix.Text : string.Empty;
            string suffix = ChkSuffix.IsChecked == true ? TxtSuffix.Text : string.Empty;
            string separator = ChkSeparator.IsChecked == true ? TxtSeparator.Text : string.Empty;

            var allowedExts = _config.AllowedExtensions
                .Select(x => x.StartsWith('.') ? x : "." + x)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string targetDirClean = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar);

            // 后台异步检索并构建预览
            List<FileRenameItem>? results = await Task.Run(() =>
            {
                var enumOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = includeSub,
                    IgnoreInaccessible = true,                                     // 自动忽略受保护或无权限目录，避免遍历中断崩溃
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, // 跳过系统和隐藏文件
                    MatchCasing = MatchCasing.CaseInsensitive
                };

                // 文件计数与超量预警检测
                int count = 0;
                try
                {
                    foreach (var _ in Directory.EnumerateFiles(targetDirClean, "*", enumOptions))
                    {
                        count++;
                        if (count > LargeFileThreshold) break;
                    }
                }
                catch
                {
                    return null;
                }

                if (count > LargeFileThreshold)
                {
                    bool proceed = false;
                    Dispatcher.Invoke(() =>
                    {
                        var msgResult = MessageBox.Show(
                            $"目标目录下文件数量已超过 {LargeFileThreshold} 个，生成预览可能需要较长时间，是否继续？",
                            "大目录提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        proceed = (msgResult == MessageBoxResult.Yes);
                    });

                    if (!proceed) return null;
                }

                // 收集符合条件的文件
                var filePaths = new List<string>();
                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(targetDirClean, "*", enumOptions))
                    {
                        if (limitExt)
                        {
                            string ext = Path.GetExtension(filePath);
                            if (!allowedExts.Contains(ext))
                                continue;
                        }
                        filePaths.Add(filePath);
                    }
                }
                catch
                {
                    return null;
                }

                // 按原文件的相对路径进行自然排序（先排目录层级，同一目录下再排文件名）
                filePaths.Sort((a, b) =>
                {
                    string relA = Path.GetRelativePath(targetDirClean, a);
                    string relB = Path.GetRelativePath(targetDirClean, b);
                    return NaturalStringComparer.ComparePath(relA, relB);
                });

                var items = new List<FileRenameItem>();

                foreach (var filePath in filePaths)
                {
                    var fi = new FileInfo(filePath);
                    string relSource = Path.GetRelativePath(targetDirClean, filePath);

                    // 计算前缀目录字符串
                    string currentFileDir = fi.DirectoryName ?? targetDirClean;
                    string folderSegments = BuildFolderPrefix(targetDirClean, currentFileDir, includeAllParents, prefix, suffix, separator);

                    string newFileName = string.IsNullOrEmpty(folderSegments)
                        ? fi.Name
                        : $"{folderSegments}{separator}{fi.Name}";

                    // 目标物理路径
                    string targetDirectory = moveToRoot ? targetDirClean : currentFileDir;
                    string fullTarget = Path.Combine(targetDirectory, newFileName);
                    string relTarget = Path.GetRelativePath(targetDirClean, fullTarget);

                    var item = new FileRenameItem
                    {
                        FullSourcePath = filePath,
                        FullTargetPath = fullTarget,
                        RelativeSourcePath = relSource,
                        RelativeTargetPath = relTarget,
                        IsReadOnlyOriginal = (fi.Attributes & FileAttributes.ReadOnly) != 0,
                        Status = RenameStatus.Normal
                    };

                    // 校验单文件名超长（Windows 限制单文件名 255 字符）
                    if (newFileName.Length > 255)
                    {
                        item.Status = RenameStatus.TooLong;
                    }

                    items.Add(item);
                }

                // 重名判定：批内冲突检测
                var groupedByTarget = items.GroupBy(x => x.FullTargetPath, StringComparer.OrdinalIgnoreCase);
                foreach (var group in groupedByTarget)
                {
                    if (group.Count() > 1)
                    {
                        foreach (var item in group)
                        {
                            item.Status = RenameStatus.Duplicate;
                        }
                    }
                }

                // 重名判定：外部冲突检测
                foreach (var item in items)
                {
                    if (item.Status == RenameStatus.Normal)
                    {
                        if (File.Exists(item.FullTargetPath) &&
                            !string.Equals(item.FullSourcePath, item.FullTargetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            item.Status = RenameStatus.Duplicate;
                        }
                    }
                }

                return items;
            });

            _previewItems.Clear();
            if (results != null)
            {
                foreach (var item in results)
                {
                    _previewItems.Add(item);
                }
            }

            // 更新执行按钮状态
            bool hasItems = _previewItems.Count > 0;
            bool allNormal = hasItems && _previewItems.All(x => x.Status == RenameStatus.Normal);
            BtnExecute.IsEnabled = allNormal;
        }

        private static string BuildFolderPrefix(string rootDir, string currentDir, bool includeAllParents, string prefix, string suffix, string separator)
        {
            string rootClean = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar);
            string curClean = Path.GetFullPath(currentDir).TrimEnd(Path.DirectorySeparatorChar);

            var folders = new List<string>();
            var cursor = new DirectoryInfo(curClean);

            if (!includeAllParents)
            {
                // 仅加入当前直接父目录名
                folders.Add($"{prefix}{cursor.Name}{suffix}");
            }
            else
            {
                // 包含从根目录到直接父目录的所有目录名（从根到叶）
                var chain = new List<string>();
                var p = cursor;
                while (p != null)
                {
                    chain.Insert(0, $"{prefix}{p.Name}{suffix}");
                    if (string.Equals(p.FullName.TrimEnd(Path.DirectorySeparatorChar), rootClean, StringComparison.OrdinalIgnoreCase))
                        break;
                    p = p.Parent;
                }
                folders.AddRange(chain);
            }

            return string.Join(separator, folders);
        }

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            if (_previewItems.Any(x => x.Status != RenameStatus.Normal))
            {
                MessageBox.Show("当前存在重名或超长文件，请核对后再执行！", "无法执行", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnExecute.IsEnabled = false;
            BtnBrowse.IsEnabled = false;

            var itemsToProcess = _previewItems.Where(x => !string.Equals(x.FullSourcePath, x.FullTargetPath, StringComparison.OrdinalIgnoreCase)).ToList();

            // 1. 预检阶段：检查文件占用与写入权限
            var lockedFiles = new List<string>();
            await Task.Run(() =>
            {
                foreach (var item in itemsToProcess)
                {
                    if (IsFileLocked(item.FullSourcePath))
                    {
                        lockedFiles.Add(item.FullSourcePath);
                    }
                }
            });

            if (lockedFiles.Count > 0)
            {
                string msg = "以下文件正被其他程序占用，无法执行改名：\n" + string.Join("\n", lockedFiles.Take(10));
                if (lockedFiles.Count > 10) msg += $"\n... 等共 {lockedFiles.Count} 个文件";
                MessageBox.Show(msg, "执行前预检失败", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnExecute.IsEnabled = true;
                BtnBrowse.IsEnabled = true;
                return;
            }

            // 2. 事务执行：两阶段安全改名（原文件 -> .tmp_{GUID} -> 目标文件）
            var step1History = new List<(string original, string tmp, bool wasReadOnly)>();
            var step2History = new List<(string tmp, string finalTarget, string original, bool wasReadOnly)>();
            bool isSuccess = true;
            string errorDetails = string.Empty;

            await Task.Run(() =>
            {
                try
                {
                    // 第一阶段：全部转为目标目录下的唯一临时文件
                    foreach (var item in itemsToProcess)
                    {
                        var fi = new FileInfo(item.FullSourcePath);
                        bool isReadOnly = (fi.Attributes & FileAttributes.ReadOnly) != 0;
                        if (isReadOnly)
                        {
                            File.SetAttributes(item.FullSourcePath, fi.Attributes & ~FileAttributes.ReadOnly);
                        }

                        string targetDir = Path.GetDirectoryName(item.FullTargetPath)!;
                        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                        string tmpFile = Path.Combine(targetDir, $".tmp_{Guid.NewGuid():N}");
                        File.Move(item.FullSourcePath, tmpFile);
                        step1History.Add((item.FullSourcePath, tmpFile, isReadOnly));
                    }

                    // 第二阶段：临时文件转为最终目标文件
                    foreach (var (original, tmp, wasReadOnly) in step1History)
                    {
                        var matchItem = itemsToProcess.First(x => x.FullSourcePath == original);
                        File.Move(tmp, matchItem.FullTargetPath);
                        if (wasReadOnly)
                        {
                            File.SetAttributes(matchItem.FullTargetPath, File.GetAttributes(matchItem.FullTargetPath) | FileAttributes.ReadOnly);
                        }
                        step2History.Add((tmp, matchItem.FullTargetPath, original, wasReadOnly));
                    }
                }
                catch (Exception ex)
                {
                    isSuccess = false;
                    errorDetails = ex.Message;

                    // 发生异常时全自动逆向恢复零脏数据
                    foreach (var (tmp, finalTarget, original, wasReadOnly) in step2History)
                    {
                        try
                        {
                            if (File.Exists(finalTarget))
                            {
                                File.Move(finalTarget, original);
                                if (wasReadOnly) File.SetAttributes(original, File.GetAttributes(original) | FileAttributes.ReadOnly);
                            }
                        }
                        catch { }
                    }

                    foreach (var (original, tmp, wasReadOnly) in step1History)
                    {
                        try
                        {
                            if (File.Exists(tmp))
                            {
                                File.Move(tmp, original);
                                if (wasReadOnly) File.SetAttributes(original, File.GetAttributes(original) | FileAttributes.ReadOnly);
                            }
                        }
                        catch { }
                    }
                }
            });

            BtnBrowse.IsEnabled = true;

            if (!isSuccess)
            {
                MessageBox.Show($"执行过程中发生异常，所有改名已自动安全回滚至初始状态。\n原因：{errorDetails}", "执行失败", MessageBoxButton.OK, MessageBoxImage.Error);
                await RefreshPreviewAsync();
                return;
            }

            // 执行成功
            _executedHistory.Clear();
            foreach (var record in step2History)
            {
                _executedHistory.Add((record.finalTarget, record.original, record.wasReadOnly));
            }

            _isExecutedState = true;
            foreach (var item in _previewItems)
            {
                item.Status = RenameStatus.Renamed;
            }

            BtnExecute.IsEnabled = false;
            BtnUndo.IsEnabled = true;
            MessageBox.Show("改名执行完成！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (!_isExecutedState || _executedHistory.Count == 0) return;

            BtnUndo.IsEnabled = false;

            // 撤销前检查：文件是否存在且未被外部占用
            var missingOrLocked = new List<string>();
            await Task.Run(() =>
            {
                foreach (var (executedPath, _, _) in _executedHistory)
                {
                    if (!File.Exists(executedPath) || IsFileLocked(executedPath))
                    {
                        missingOrLocked.Add(executedPath);
                    }
                }
            });

            if (missingOrLocked.Count > 0)
            {
                string msg = "撤销失败！以下改名后的文件已被删除或正被占用：\n" + string.Join("\n", missingOrLocked.Take(10));
                if (missingOrLocked.Count > 10) msg += $"\n... 等共 {missingOrLocked.Count} 个文件";
                MessageBox.Show(msg, "撤销拦截", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnUndo.IsEnabled = true;
                return;
            }

            // 执行逆向撤销
            bool undoSuccess = true;
            string undoError = string.Empty;

            await Task.Run(() =>
            {
                try
                {
                    foreach (var (executedPath, originalPath, wasReadOnly) in _executedHistory)
                    {
                        if (wasReadOnly)
                        {
                            File.SetAttributes(executedPath, File.GetAttributes(executedPath) & ~FileAttributes.ReadOnly);
                        }

                        string targetDir = Path.GetDirectoryName(originalPath)!;
                        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                        File.Move(executedPath, originalPath);

                        if (wasReadOnly)
                        {
                            File.SetAttributes(originalPath, File.GetAttributes(originalPath) | FileAttributes.ReadOnly);
                        }
                    }
                }
                catch (Exception ex)
                {
                    undoSuccess = false;
                    undoError = ex.Message;
                }
            });

            if (!undoSuccess)
            {
                MessageBox.Show($"撤销过程中发生错误：{undoError}", "撤销中断", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show("撤销完成，文件已恢复原样。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            ResetUndoState();
            await RefreshPreviewAsync();
        }

        private static bool IsFileLocked(string filePath)
        {
            if (!File.Exists(filePath)) return false;
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            // .NET 8 必须指定 UseShellExecute = true 才能由系统默认浏览器打开 URL
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
    }
}