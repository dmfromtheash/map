using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;

namespace Map.App.Views;

/// <summary>
/// R42: in «Версии» - «Выгрузить изменения» on a version writes a .mapchanges file, «Принять
/// пакет изменений…» opens one and lists what applies cleanly and what clashes; for each clash
/// the user ticks «взять из пакета», then «Применить» takes it all as one undo step.
/// </summary>
public sealed partial class EditorView
{
    private StackPanel? _packageBlock;
    private readonly HashSet<Guid> _packageTakeTheirs = [];

    private Control BuildPackageBlock()
    {
        _packageBlock = new StackPanel { Name = "PackageBlock", Spacing = 6 };
        var open = Ui.AsyncButton("OpenChangePackageButton", "Принять пакет изменений…", OpenChangePackageAsync, "compact");
        ToolTip.SetTip(open, "Файл .mapchanges с другой копии этой карты: что изменилось там с общей версии.");
        return new StackPanel { Spacing = 6, Children = { open, _packageBlock } };
    }

    private async Task ExportChangesAsync(MapCheckpoint checkpoint)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить пакет изменений",
            SuggestedFileName = $"{_editor.Session.Document.Title} — с «{checkpoint.Name}»{ChangePackageStore.Extension}",
            DefaultExtension = ChangePackageStore.Extension,
            FileTypeChoices = [new FilePickerFileType("Пакет изменений карты") { Patterns = ["*" + ChangePackageStore.Extension] }],
        });
        if (file is null || file.TryGetLocalPath() is not { } path) return;
        await _editor.ExportChangesAsync(checkpoint.Id, path);
        SyncStatus();
    }

    private async Task OpenChangePackageAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Пакет изменений",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Пакет изменений карты") { Patterns = ["*" + ChangePackageStore.Extension] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        _packageTakeTheirs.Clear();
        await _editor.OpenChangePackageAsync(path);
        SyncPackageBlock();
        SyncStatus();
    }

    private void SyncPackageBlock()
    {
        if (_packageBlock is null) return;
        _packageBlock.Children.Clear();
        if (_editor.PendingPackage is not { } package) return;

        var changes = _editor.PackageChanges;
        var clean = changes.Count(c => c.Outcome == ChangeOutcome.Clean);
        var same = changes.Count(c => c.Outcome == ChangeOutcome.Same);
        var conflicts = changes.Where(c => c.Outcome == ChangeOutcome.Conflict).ToList();
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new TextBlock { Text = $"Пакет «{package.SourceTitle}» с версии «{package.BaseName}»", TextWrapping = TextWrapping.Wrap }.Classed("role-panel"));
        body.Children.Add(new TextBlock
        {
            Name = "PackageSummary",
            Text = $"Применятся без вопросов: {clean}. Уже так: {same}. Конфликтов: {conflicts.Count}.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (conflicts.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Конфликт — это и тут, и там изменено по-разному. Отметьте, где взять из пакета; без отметки останется ваше.",
                TextWrapping = TextWrapping.Wrap,
            }.Classed("faint", "role-caption"));
            foreach (var conflict in conflicts)
            {
                var captured = conflict;
                var box = new CheckBox
                {
                    Name = "PackageTake_" + conflict.Id,
                    Content = conflict.Mine.Length == 0 || conflict.Mine == conflict.Name
                        ? $"{conflict.Kind} «{conflict.Name}» — {conflict.Action} (у вас изменён иначе)"
                        : $"{conflict.Kind}: у вас «{conflict.Mine}», в пакете «{conflict.Name}»",
                    IsChecked = _packageTakeTheirs.Contains(conflict.Id),
                };
                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true) _packageTakeTheirs.Add(captured.Id); else _packageTakeTheirs.Remove(captured.Id);
                };
                body.Children.Add(box);
            }
        }
        var cleanList = new StackPanel { Spacing = 2 };
        foreach (var change in changes.Where(c => c.Outcome == ChangeOutcome.Clean).Take(80))
            cleanList.Children.Add(new TextBlock { Text = $"{change.Kind} «{change.Name}» — {change.Action}", TextWrapping = TextWrapping.Wrap }.Classed("role-caption"));
        if (cleanList.Children.Count > 0) body.Children.Add(new Expander { Header = $"Без конфликта ({clean})", Content = cleanList });

        var apply = Ui.TextButton("ApplyChangePackageButton", "Применить", () =>
        {
            _editor.ApplyPendingPackage(_packageTakeTheirs.ToHashSet());
            _packageTakeTheirs.Clear();
            SyncPackageBlock();
            SyncStatus();
        }, "accent", "compact");
        apply.IsEnabled = clean + conflicts.Count > 0;
        var close = Ui.TextButton("CloseChangePackageButton", "Отмена", () =>
        {
            _editor.ClosePendingPackage();
            _packageTakeTheirs.Clear();
            SyncPackageBlock();
        }, "compact");
        body.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { apply, close } });
        _packageBlock.Children.Add(new Border { Padding = new Avalonia.Thickness(8), Child = body }.Classed("group"));
    }
}
