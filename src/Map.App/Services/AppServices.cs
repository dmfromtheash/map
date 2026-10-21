using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Map.Platform.Windows;
using Map.Export;
using Map.Presentation.Services;

namespace Map.App.Services;

/// <summary>Adapts the bounded local recent index to the presentation interface.</summary>
public sealed class PlatformRecentMaps(RecentMapsStore store) : IRecentMaps
{
    public IReadOnlyList<RecentMapItem> Load() =>
        store.Load()
            .Select(e => new RecentMapItem(
                e.Path,
                e.Title,
                DateTimeOffset.TryParse(e.ModifiedUtc, out var modified) ? modified : DateTimeOffset.MinValue,
                e.IsAvailable,
                e.MapType,
                e.SizeBytes,
                DecodeThumbnail(e.ThumbnailBase64)))
            .ToList();

    public void Touch(
        string path,
        Guid documentId,
        string title,
        DateTimeOffset modifiedUtc,
        byte[]? thumbnailPng = null,
        string mapType = "Локальная карта") =>
        store.Touch(path, documentId, title, modifiedUtc, thumbnailPng, mapType);

    public void Remove(string path) => store.Remove(path);

    private static byte[]? DecodeThumbnail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }
}

public sealed class DiagnosticsLogAdapter(DiagnosticsLog log) : IAppLog
{
    public void Info(string message) => log.Info(message);

    public void Error(string message, Exception? exception = null) => log.Error(message, exception);
}

/// <summary>Native file dialogs over the Avalonia storage provider.</summary>
public sealed class AvaloniaFilePicker(Window owner, Func<string?>? startFolder = null) : IFilePicker
{
    private async Task<IStorageFolder?> StartLocation()
    {
        if (startFolder?.Invoke() is not { Length: > 0 } folder) return null;
        try { return await owner.StorageProvider.TryGetFolderFromPathAsync(folder); }
        catch (Exception) { return null; }
    }

    private static readonly FilePickerFileType MapProjectType = new("Карта Map")
    {
        Patterns = ["*.mapproj"],
    };

    public async Task<string?> PickOpenAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Открыть карту",
                SuggestedStartLocation = await StartLocation(),
                AllowMultiple = false,
                FileTypeFilter = [MapProjectType],
            });

            return files.Count == 1 ? files[0].TryGetLocalPath() : null;
        });
    }

    public async Task<string?> PickSaveAsync(string suggestedFileName)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить карту",
                SuggestedStartLocation = await StartLocation(),
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "mapproj",
                FileTypeChoices = [MapProjectType],
            });

            return file?.TryGetLocalPath();
        });
    }
}

/// <summary>Modal user decisions as plain windows; no work is ever lost silently.</summary>
public sealed class DialogPrompts(Window owner) : IUserPrompts
{
    public Task<bool> ConfirmMigrationCopyAsync() =>
        Dispatcher.UIThread.InvokeAsync(() => MigrationPromptWindow.ShowAsync(owner));

    public Task<bool> ConfirmDeleteProjectAsync(string projectTitle, bool fileAlreadyMissing = false) =>
        Dispatcher.UIThread.InvokeAsync(() => fileAlreadyMissing
            ? ConfirmationWindow.ShowAsync(
                owner,
                "Убрать запись о недоступной карте?",
                $"Файла карты «{projectTitle}» уже нет на диске, поэтому удалять нечего. " +
                    "Будет убрана только запись о ней из списка недавних. " +
                    "Остальные карты и данные восстановления не изменятся.",
                "Убрать запись",
                "DeleteProjectConfirmButton",
                destructive: true)
            : ConfirmationWindow.ShowAsync(
                owner,
                "Переместить карту в Корзину?",
                $"Файл карты «{projectTitle}» будет перемещён в Корзину Windows. " +
                    "Остальные карты и данные восстановления не изменятся.",
                "В Корзину",
                "DeleteProjectConfirmButton",
                destructive: true));

    public Task<CloseChoice> ConfirmCloseDirtyAsync(string documentTitle) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var result = await ClosePromptWindow.ShowAsync(owner, documentTitle);
            return result;
        });

    public Task ShowErrorAsync(string title, string message) =>
        Dispatcher.UIThread.InvokeAsync(() => ErrorWindow.ShowAsync(owner, title, message));

    public Task<bool> ConfirmRestoreCheckpointAsync(string checkpointName) =>
        Dispatcher.UIThread.InvokeAsync(() => ConfirmationWindow.ShowAsync(
            owner,
            "Восстановить версию?",
            $"Текущее состояние карты будет заменено состоянием «{checkpointName}». "
                + "Это действие можно отменить одной командой «Отменить».",
            "Восстановить",
            "RestoreCheckpointConfirmButton"));

    public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) =>
        Dispatcher.UIThread.InvokeAsync(() => ConfirmationWindow.ShowAsync(
            owner,
            "Удалить версию?",
            $"Версия «{checkpointName}» будет удалена. Текущая карта не изменится.",
            "Удалить",
            "DeleteCheckpointConfirmButton",
            destructive: true));
}

/// <summary>Windows Recycle Bin adapter. It accepts one already-resolved project file only.</summary>
public sealed class WindowsProjectTrash : IProjectTrash
{
    public ProjectTrashOutcome MoveToTrash(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return ProjectTrashOutcome.FileAlreadyMissing;
        }

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                fullPath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (FileNotFoundException)
        {
            // The file disappeared between the probe and the call. Nothing was deleted here, and
            // nothing is wrong: the caller still gets to clean up its stale entry.
            return ProjectTrashOutcome.FileAlreadyMissing;
        }
        catch (DirectoryNotFoundException)
        {
            return ProjectTrashOutcome.FileAlreadyMissing;
        }

        return ProjectTrashOutcome.MovedToTrash;
    }
}

/// <summary>Native export destination dialogs. The tile-package picker chooses a new folder name.</summary>
public sealed class AvaloniaExportDestinationPicker(Window owner) : IExportDestinationPicker
{
    public Task<string?> PickImageAsync(string suggestedFileName, ExportImageFormat format) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var extension = ImageExportEngine.Extension(format);
            var type = new FilePickerFileType(ImageExportEngine.FormatName(format))
            {
                Patterns = format == ExportImageFormat.Jpeg
                    ? ["*.jpg", "*.jpeg"]
                    : [$"*.{extension}"],
            };
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Экспорт изображения",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = extension,
                FileTypeChoices = [type],
            });
            return file?.TryGetLocalPath();
        });

    public Task<string?> PickVttAsync(string suggestedFileName) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Экспорт для виртуальных столов",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "dd2vtt",
                FileTypeChoices = [new FilePickerFileType("Universal VTT") { Patterns = ["*.dd2vtt"] }],
            });
            return file?.TryGetLocalPath();
        });

    public Task<string?> PickFileAsync(string suggestedFileName, string extension, string description) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = description,
                SuggestedFileName = suggestedFileName,
                DefaultExtension = extension,
                FileTypeChoices = [new FilePickerFileType(description) { Patterns = ["*." + extension] }],
            });
            return file?.TryGetLocalPath();
        });

    public Task<string?> PickTilePackageAsync(string suggestedFolderName) =>
        PickPackageAsync(suggestedFolderName, "Новая папка пакета тайлов");

    public Task<string?> PickObsidianPackageAsync(string suggestedFolderName) =>
        PickPackageAsync(suggestedFolderName, "Новая папка Obsidian");

    public Task<string?> PickLayerPackageAsync(string suggestedFolderName) =>
        PickPackageAsync(suggestedFolderName, "Новая папка PNG-слоёв");

    private Task<string?> PickPackageAsync(string suggestedFolderName, string title) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // A save-name dialog can name a folder that does not exist yet; a folder picker cannot.
            var folder = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedFolderName,
            });
            return folder?.TryGetLocalPath();
        });
}

public sealed class WindowsFolderLauncher : IFolderLauncher
{
    public void OpenContainingFolder(string resultPath, ExportOutputMode outputMode)
    {
        var target = outputMode == ExportOutputMode.TilePackage
            ? resultPath
            : Path.GetDirectoryName(resultPath);
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            throw new DirectoryNotFoundException("Папка экспортированного результата не найдена.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{target}\"",
            UseShellExecute = true,
        });
    }
}
