using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IosBackupApp.Models;
using IosBackupApp.Services.Interfaces;

namespace IosBackupApp.Services;

public class DiffEngine : IDiffEngine
{
    public DiffResult Compare(DeviceState iphoneState, DeviceState? laptopState, string destinationDirectory)
    {
        var result = new DiffResult();

        // Build lookup of laptop files: Normalized Relative Path -> File Size
        var laptopLookup = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (laptopState?.Root != null)
        {
            IndexFiles(laptopState.Root, laptopLookup);
        }

        // Build hierarchical TreeView models from iPhone tree
        var rootVm = new FileDiffNodeViewModel
        {
            Name = iphoneState.Root.Name,
            IsDirectory = true,
            RelativePath = iphoneState.Root.RelativePath,
            FullPathSource = iphoneState.Root.RelativePath,
            FullPathDest = Path.Combine(destinationDirectory, iphoneState.Root.Name),
            IsExpanded = true
        };

        if (iphoneState.Root.Children != null)
        {
            foreach (var childNode in iphoneState.Root.Children)
            {
                var childVm = BuildViewModelTree(childNode, rootVm, laptopLookup, destinationDirectory, result);
                rootVm.Children.Add(childVm);
            }
        }

        rootVm.RecalculateMetrics();
        rootVm.SetIsChecked(ComputeInitialCheckState(rootVm), updateChildren: false, updateParent: false);

        result.RootNode = rootVm;
        return result;
    }

    private static void IndexFiles(MediaItemNode node, Dictionary<string, long> lookup)
    {
        if (!node.IsDirectory)
        {
            string normalizedPath = NormalizeRelativePath(node.RelativePath);
            lookup[normalizedPath] = node.Size;
        }
        else if (node.Children != null)
        {
            foreach (var child in node.Children)
            {
                IndexFiles(child, lookup);
            }
        }
    }

    private FileDiffNodeViewModel BuildViewModelTree(
        MediaItemNode node,
        FileDiffNodeViewModel parent,
        Dictionary<string, long> laptopLookup,
        string destinationDirectory,
        DiffResult summary)
    {
        string normalizedPath = NormalizeRelativePath(node.RelativePath);
        // Destination file path on local laptop
        string destPath = Path.Combine(destinationDirectory, normalizedPath.Replace('/', Path.DirectorySeparatorChar));

        var vm = new FileDiffNodeViewModel
        {
            Name = node.Name,
            IsDirectory = node.IsDirectory,
            RelativePath = node.RelativePath,
            FullPathSource = !string.IsNullOrEmpty(node.SourcePath) ? node.SourcePath : node.RelativePath,
            FullPathDest = destPath,
            Parent = parent,
            Size = node.Size,
            IsExpanded = true
        };

        if (node.IsDirectory)
        {
            if (node.Children != null)
            {
                foreach (var childNode in node.Children)
                {
                    var childVm = BuildViewModelTree(childNode, vm, laptopLookup, destinationDirectory, summary);
                    vm.Children.Add(childVm);
                }
            }

            // Folder status is Pending if any child is Pending
            bool anyPending = vm.Children.Any(c => c.Status == FileDiffStatus.PendingBackup);
            vm.Status = anyPending ? FileDiffStatus.PendingBackup : FileDiffStatus.BackedUp;
            vm.SetIsChecked(ComputeInitialCheckState(vm), updateChildren: false, updateParent: false);
        }
        else
        {
            summary.TotalFilesCount++;
            summary.TotalBytes += node.Size;

            // Check if file exists on laptop and has same size
            bool existsOnLaptop = laptopLookup.TryGetValue(normalizedPath, out long laptopSize);
            bool isIdentical = existsOnLaptop && laptopSize == node.Size;

            if (isIdentical)
            {
                vm.Status = FileDiffStatus.BackedUp;
                vm.SetIsChecked(false, updateChildren: false, updateParent: false);
                summary.BackedUpFilesCount++;
                summary.BackedUpBytes += node.Size;
            }
            else
            {
                vm.Status = FileDiffStatus.PendingBackup;
                vm.SetIsChecked(true, updateChildren: false, updateParent: false);
                summary.PendingFilesCount++;
                summary.PendingBytes += node.Size;
            }
        }

        return vm;
    }

    private static bool? ComputeInitialCheckState(FileDiffNodeViewModel folder)
    {
        if (folder.Children.Count == 0) return false;

        bool allTrue = true;
        bool allFalse = true;

        foreach (var child in folder.Children)
        {
            if (child.IsChecked == true)
            {
                allFalse = false;
            }
            else if (child.IsChecked == false)
            {
                allTrue = false;
            }
            else
            {
                allTrue = false;
                allFalse = false;
            }
        }

        if (allTrue) return true;
        if (allFalse) return false;
        return null;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var p = path.Replace('\\', '/').Trim('/');
        // Ensure starting with DCIM/ is consistent
        if (p.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase))
        {
            return p;
        }
        return $"DCIM/{p}";
    }
}
