using Map.Core.Documents;
using Map.Core.Editing;
using Map.Persistence;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R42: offline exchange of changes - what changed since a version goes into a <c>.mapchanges</c>
/// file; another copy of the map opens it, sees which changes apply cleanly and which clash with
/// its own edits, picks for each clash, and takes it all as one undo step.
/// </summary>
public sealed partial class EditorViewModel
{
    public ChangePackage? PendingPackage { get; private set; }
    public IReadOnlyList<PackageChange> PackageChanges { get; private set; } = [];

    /// <summary>Writes the changes made since version <paramref name="id"/>; null when nothing changed or it failed.</summary>
    public async Task<ChangePackage?> ExportChangesAsync(CheckpointId id, string path)
    {
        if (Session.Document.Checkpoints.FirstOrDefault(c => c.Id == id) is not { } checkpoint) { StatusDetail = "Версия не найдена."; return null; }
        var current = Session.Document;
        var version = checkpoint.Snapshot.Materialize(current.Id, current.Checkpoints);
        var package = ChangePackage.Create(version, current, checkpoint.Name, _clock());
        // R42b: say plainly what changed but does not travel in a package.
        var left = ChangePackage.NotCarried(version, current);
        var notCarried = left.Count == 0 ? "" : $" Не вошло в пакет: {string.Join(", ", left)} — пакет переносит только {ChangePackage.Carries}.";
        if (package.Count == 0)
        {
            StatusDetail = left.Count == 0
                ? $"С версии «{checkpoint.Name}» ничего не изменилось — пакет не нужен."
                : $"Пакет пуст: с версии «{checkpoint.Name}» изменились только {string.Join(", ", left)}, а пакет переносит {ChangePackage.Carries}.";
            return null;
        }
        try
        {
            await Task.Run(() => ChangePackageStore.Save(package, path, _appVersion));
        }
        catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось сохранить пакет: " + ex.Message;
            _log.Error("CHANGES_EXPORT_FAILED", ex);
            return null;
        }
        StatusDetail = $"Пакет изменений с версии «{checkpoint.Name}»: {package.Count} шт. — {Path.GetFileName(path)}.{notCarried}";
        return package;
    }

    /// <summary>Reads a package and checks it against this map; nothing changes until <see cref="ApplyPendingPackage"/>.</summary>
    public async Task<bool> OpenChangePackageAsync(string path)
    {
        ChangePackage package;
        try { package = await Task.Run(() => ChangePackageStore.Open(path)); }
        catch (Exception ex) when (ex is MapProjectException or IOException or UnauthorizedAccessException)
        {
            StatusDetail = "Не удалось открыть пакет: " + ex.Message;
            return false;
        }
        PendingPackage = package;
        PackageChanges = ChangePackageMerge.Check(Session.Document, package);
        Raise(nameof(PendingPackage));
        Raise(nameof(PackageChanges));
        var clean = PackageChanges.Count(c => c.Outcome == ChangeOutcome.Clean);
        var conflicts = PackageChanges.Count(c => c.Outcome == ChangeOutcome.Conflict);
        var other = package.SourceMap != Session.Document.Id.Value ? " Пакет сделан с другой карты — совпадут только общие объекты." : "";
        StatusDetail = $"Пакет «{package.SourceTitle}» с версии «{package.BaseName}»: без конфликта {clean}, конфликтов {conflicts}.{other}";
        return true;
    }

    /// <summary>Applies the open package: clean changes and the clashes listed in <paramref name="takeTheirs"/>.</summary>
    public bool ApplyPendingPackage(IReadOnlySet<Guid> takeTheirs)
    {
        if (PendingPackage is not { } package) return false;
        var before = Session.Document;
        var after = ChangePackageMerge.Apply(before, package, takeTheirs);
        var taken = ChangePackageMerge.Check(before, package).Count(c => c.Outcome == ChangeOutcome.Clean || (c.Outcome == ChangeOutcome.Conflict && takeTheirs.Contains(c.Id)));
        ClosePendingPackage();
        if (ReferenceEquals(after, before)) { StatusDetail = "Из пакета нечего брать: всё уже так или оставлено своё."; return false; }
        Session.Execute(new ApplyChangePackageCommand(before, after));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
        StatusDetail = $"Взято изменений из пакета: {taken}. Ctrl+Z вернёт как было.";
        return true;
    }

    public void ClosePendingPackage()
    {
        if (PendingPackage is null) return;
        PendingPackage = null;
        PackageChanges = [];
        Raise(nameof(PendingPackage));
        Raise(nameof(PackageChanges));
    }
}
