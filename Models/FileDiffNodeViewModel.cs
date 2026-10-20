using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IosBackupApp.Models;

public partial class FileDiffNodeViewModel : ObservableObject
{
    private bool? _isChecked = true;
    private bool _isUpdatingState;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _relativePath = string.Empty;

    [ObservableProperty]
    private string _fullPathSource = string.Empty;

    [ObservableProperty]
    private string _fullPathDest = string.Empty;

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private long _size;

    [ObservableProperty]
    private FileDiffStatus _status = FileDiffStatus.PendingBackup;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private int _itemCount;

    public FileDiffNodeViewModel? Parent { get; set; }

    public ObservableCollection<FileDiffNodeViewModel> Children { get; set; } = new();

    public bool? IsChecked
    {
        get => _isChecked;
        set => SetIsChecked(value, updateChildren: true, updateParent: true);
    }

    public string FormattedSize => StorageMetrics.FormatBytes(Size);

    public string StatusText => Status switch
    {
        FileDiffStatus.PendingBackup => "Pending Backup",
        FileDiffStatus.BackedUp => "Backed Up",
        FileDiffStatus.Success => "Completed",
        FileDiffStatus.Failed => "Failed",
        _ => Status.ToString()
    };

    public string IconKind => IsDirectory 
        ? (IsExpanded ? "FolderOpen" : "Folder") 
        : GetFileIcon(Name);

    private static string GetFileIcon(string filename)
    {
        var ext = System.IO.Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".heic" or ".jpg" or ".jpeg" or ".png" or ".gif" or ".raw" or ".dng" => "Image",
            ".mov" or ".mp4" or ".m4v" => "Video",
            ".aae" => "AdjustmentFile",
            _ => "File"
        };
    }

    public void SetIsChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value) return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        if (updateChildren && value.HasValue && IsDirectory)
        {
            _isUpdatingState = true;
            try
            {
                foreach (var child in Children)
                {
                    child.SetIsChecked(value.Value, updateChildren: true, updateParent: false);
                }
            }
            finally
            {
                _isUpdatingState = false;
            }
        }

        if (updateParent && Parent != null && !Parent._isUpdatingState)
        {
            Parent.VerifyCheckState();
        }
    }

    private void VerifyCheckState()
    {
        bool? state = null;
        for (int i = 0; i < Children.Count; i++)
        {
            bool? current = Children[i].IsChecked;
            if (i == 0)
            {
                state = current;
            }
            else if (state != current)
            {
                state = null;
                break;
            }
        }

        SetIsChecked(state, updateChildren: false, updateParent: true);
    }

    public void RecalculateMetrics()
    {
        if (IsDirectory)
        {
            long totalSize = 0;
            int count = 0;
            foreach (var child in Children)
            {
                child.RecalculateMetrics();
                totalSize += child.Size;
                count += child.IsDirectory ? child.ItemCount : 1;
            }
            Size = totalSize;
            ItemCount = count;
            OnPropertyChanged(nameof(FormattedSize));
        }
    }

    public void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
    }

    public IEnumerable<FileDiffNodeViewModel> GetAllFiles()
    {
        if (!IsDirectory)
        {
            yield return this;
        }
        else
        {
            foreach (var child in Children)
            {
                foreach (var file in child.GetAllFiles())
                {
                    yield return file;
                }
            }
        }
    }

    public IEnumerable<FileDiffNodeViewModel> GetSelectedFiles()
    {
        if (!IsDirectory)
        {
            if (IsChecked == true)
            {
                yield return this;
            }
        }
        else
        {
            foreach (var child in Children)
            {
                foreach (var file in child.GetSelectedFiles())
                {
                    yield return file;
                }
            }
        }
    }
}
