using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace FolderToFileName
{
    public enum RenameStatus
    {
        Normal,         // 正常（绿色）
        Duplicate,      // 重名（红色）
        TooLong,        // 文件名超长（红色）
        Renamed         // 已改名（可撤销）（蓝色）
    }

    public class FileRenameItem : INotifyPropertyChanged
    {
        private string _relativeSourcePath = string.Empty;
        private string _relativeTargetPath = string.Empty;
        private RenameStatus _status = RenameStatus.Normal;

        public string FullSourcePath { get; set; } = string.Empty;
        public string FullTargetPath { get; set; } = string.Empty;
        public bool IsReadOnlyOriginal { get; set; }

        public string RelativeSourcePath
        {
            get => _relativeSourcePath;
            set { _relativeSourcePath = value; OnPropertyChanged(); }
        }

        public string RelativeTargetPath
        {
            get => _relativeTargetPath;
            set { _relativeTargetPath = value; OnPropertyChanged(); }
        }

        public RenameStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplayText));
                OnPropertyChanged(nameof(StatusBrush));
            }
        }

        public string StatusDisplayText => Status switch
        {
            RenameStatus.Normal => "正常",
            RenameStatus.Duplicate => "重名",
            RenameStatus.TooLong => "文件名超长",
            RenameStatus.Renamed => "已改名（可撤销）",
            _ => string.Empty
        };

        public Brush StatusBrush => Status switch
        {
            RenameStatus.Normal => Brushes.ForestGreen,
            RenameStatus.Duplicate => Brushes.Red,
            RenameStatus.TooLong => Brushes.Red,
            RenameStatus.Renamed => Brushes.DodgerBlue,
            _ => Brushes.Black
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}