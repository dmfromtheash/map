namespace Map.Presentation.ViewModels;

public sealed partial class DashboardViewModel
{
    private readonly Action<string>? _persistLibraryFolder;
    private string _libraryCategoryFilter = "";
    public string LibraryCategoryFilter
    {
        get => _libraryCategoryFilter;
        set { if (SetField(ref _libraryCategoryFilter, value ?? "")) { ApplyQuery(); Raise(nameof(LatestCards)); } }
    }
    private bool MatchesLibraryCategory(RecentCard card) => LibraryCategoryFilter.Length == 0 || card.Category?.Value == LibraryCategoryFilter;

    public bool ChangeLibraryFolder(string path)
    {
        if (_library is null) return false;
        try
        {
            _library.ChangeFolder(path, _persistLibraryFolder);
            _currentFolder = null;
            Refresh();
            Raise(nameof(Library)); Raise(nameof(CurrentFolder));
            LastError = "";
            LastNotice = "Папка библиотеки изменена: " + _library.Root;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { LastError = "Не удалось сменить папку библиотеки: " + e.Message; return false; }
    }
}
