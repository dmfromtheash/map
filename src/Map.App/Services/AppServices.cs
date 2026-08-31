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
                e.IsAvailable))
            .ToList();

    public void Touch(string path, Guid documentId, string title, DateTimeOffset modifiedUtc) =>
        store.Touch(path, documentId, title, modifiedUtc);

    public void Remove(string path) => store.Remove(path);
}

public sealed class DiagnosticsLogAdapter(DiagnosticsLog log) : IAppLog
{
    public void Info(string message) => log.Info(message);

    public void Error(string message, Exception? exception = null) => log.Error(message, exception);
}

/// <summary>Native file dialogs over the Avalonia storage provider.</summary>
public sealed class AvaloniaFilePicker(Window owner) : IFilePicker
{
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
            "Восстановить контрольную точку?",
            $"Текущее состояние карты будет заменено состоянием «{checkpointName}». "
                + "Это действие можно отменить одной командой Undo.",
            "Восстановить",
            "RestoreCheckpointConfirmButton"));

    public Task<bool> ConfirmDeleteCheckpointAsync(string checkpointName) =>
        Dispatcher.UIThread.InvokeAsync(() => ConfirmationWindow.ShowAsync(
            owner,
            "Удалить контрольную точку?",
            $"Контрольная точка «{checkpointName}» будет удалена. Текущая карта не изменится.",
            "Удалить",
            "DeleteCheckpointConfirmButton",
            destructive: true));
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

    public Task<string?> PickTilePackageAsync(string suggestedFolderName) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // A save-name dialog can name a folder that does not exist yet; a folder picker cannot.
            var folder = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Новая папка пакета тайлов",
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
