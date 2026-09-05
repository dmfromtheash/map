using Map.Platform.Windows;

namespace Map.Presentation.Tests;

public sealed class NativeProfileIsolationTests
{
    [Fact]
    public void Native_driver_profile_is_absolute_and_cannot_touch_production_state()
    {
        var isolated = Path.Combine(
            Path.GetTempPath(), "map-stage11-isolated", Guid.NewGuid().ToString("N"));

        Assert.Equal(Path.GetFullPath(isolated), AppPaths.ResolveTestProfileRoot(isolated));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveTestProfileRoot(testOverride: null));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveTestProfileRoot("relative-profile"));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveTestProfileRoot(Path.GetPathRoot(isolated)));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveTestProfileRoot(AppPaths.Root));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveTestProfileRoot(Path.Combine(AppPaths.Root, "native-test")));
        Assert.Throws<InvalidOperationException>(() =>
            AppPaths.ResolveTestProfileRoot(Directory.GetParent(AppPaths.Root)!.FullName));
    }
}
