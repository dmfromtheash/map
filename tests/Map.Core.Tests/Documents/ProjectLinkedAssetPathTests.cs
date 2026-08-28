using Map.Core.Documents;

namespace Map.Core.Tests.Documents;

public sealed class ProjectLinkedAssetPathTests
{
    [Theory]
    [InlineData(@"C:\assets\tree.png")]
    [InlineData("C:assets/tree.png")]
    [InlineData(@"\assets\tree.png")]
    [InlineData("/assets/tree.png")]
    [InlineData(@"\\server\share\tree.png")]
    [InlineData("//server/share/tree.png")]
    [InlineData(@"\\?\C:\assets\tree.png")]
    [InlineData(@"\\?\UNC\server\share\tree.png")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("assets/tree.png:payload")]
    [InlineData("assets/tree.png.")]
    [InlineData("assets/tree.png ")]
    [InlineData("assets/CON.png")]
    [InlineData("assets/nul.txt")]
    [InlineData("assets/COM1.png")]
    [InlineData("assets/com\u00b9.png")]
    [InlineData("assets/LPT9.webp")]
    [InlineData("assets/lpt\u00b2.webp")]
    [InlineData("assets//tree.png")]
    [InlineData(":stream")]
    [InlineData("\0assets/tree.png")]
    [InlineData("   ")]
    public void Dangerous_saved_paths_are_not_project_relative(string path)
    {
        Assert.False(ProjectLinkedAssetPath.IsSafeRelative(path));
        Assert.Throws<ArgumentException>(() => Linked(path));
    }

    [Theory]
    [InlineData("image/png", "jpg")]
    [InlineData("image/jpeg", "png")]
    [InlineData("image/gif", "gif")]
    public void Asset_media_type_and_extension_must_name_one_allowlisted_format(
        string mediaType, string extension)
    {
        Assert.Throws<ArgumentException>(() => new MapAssetReference(
            new string('a', 64), "bad", mediaType, extension, ProjectAssetStorage.Linked,
            DateTimeOffset.UnixEpoch, "test", "test", catalogId: "catalog"));
    }

    [Theory]
    [InlineData("assets/tree.png")]
    [InlineData(@"assets\tree.png")]
    [InlineData("./assets/tree.png")]
    [InlineData("assets/sub/../tree.png")]
    [InlineData("../shared/tree.png")]
    public void Relocatable_local_paths_remain_supported(string path)
    {
        Assert.True(ProjectLinkedAssetPath.IsSafeRelative(path));
        Assert.Equal(path, Linked(path).RelativePath);
    }

    [Fact]
    public void Resolution_is_contained_beneath_the_project_directory()
    {
        var mapPath = Path.Combine(Path.GetTempPath(), "map-project", "map.mapproj");

        Assert.True(ProjectLinkedAssetPath.TryResolve(mapPath, "assets/tree.png", out var resolved));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mapPath)!, "assets/tree.png")),
            resolved);
        Assert.True(ProjectLinkedAssetPath.TryResolve(
            mapPath, "assets/sub/../tree.png", out var normalized));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(mapPath)!, "assets", "tree.png"), normalized);
        Assert.False(ProjectLinkedAssetPath.TryResolve(mapPath, "../shared/tree.png", out _));
    }

    [Fact]
    public void Writer_stores_a_relative_fallback_only_for_assets_inside_the_project_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-project");
        var mapPath = Path.Combine(root, "map.mapproj");

        Assert.True(ProjectLinkedAssetPath.TryMakeRelative(
            mapPath, Path.Combine(root, "assets", "tree.png"), out var relative));
        Assert.Equal("assets/tree.png", relative);
        Assert.False(ProjectLinkedAssetPath.TryMakeRelative(
            mapPath, Path.Combine(Path.GetTempPath(), "external", "tree.png"), out _));
    }

    [Fact]
    public void Resolution_and_writer_handle_a_project_stored_at_the_volume_root()
    {
        var volumeRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var mapPath = Path.Combine(volumeRoot, "map.mapproj");
        var assetPath = Path.Combine(volumeRoot, "assets", "tree.png");

        Assert.True(ProjectLinkedAssetPath.TryResolve(mapPath, "assets/tree.png", out var resolved));
        Assert.Equal(Path.GetFullPath(assetPath), resolved);
        Assert.True(ProjectLinkedAssetPath.TryMakeRelative(mapPath, assetPath, out var relative));
        Assert.Equal("assets/tree.png", relative);
    }

    [Fact]
    public void Existing_file_check_accepts_only_a_real_contained_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-project", Guid.NewGuid().ToString("N"));
        var assets = Path.Combine(root, "assets");
        var mapPath = Path.Combine(root, "map.mapproj");
        var assetPath = Path.Combine(assets, "tree.png");
        Directory.CreateDirectory(assets);
        File.WriteAllBytes(assetPath, [1, 2, 3]);

        try
        {
            Assert.True(ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(mapPath, assetPath));
            Assert.False(ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(mapPath, assets));
            Assert.False(ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(
                mapPath, Path.Combine(assets, "missing.png")));
            Assert.False(ProjectLinkedAssetPath.IsExistingFileWithoutReparsePoints(
                mapPath, Path.Combine(root, "..", "outside.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MapAssetReference Linked(string path) => new(
        new string('a', 64), "tree", "image/png", "png", ProjectAssetStorage.Linked,
        DateTimeOffset.UnixEpoch, "user import", "owned", relativePath: path);
}
