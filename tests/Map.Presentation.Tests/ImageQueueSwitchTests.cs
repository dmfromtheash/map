using Map.Assets;
using Map.Presentation.Services;

namespace Map.Presentation.Tests;

public sealed class ImageQueueSwitchTests
{
    [Fact]
    public async Task Defaults_follow_the_AB_environment_switches()
    {
        var root = Path.Combine(Path.GetTempPath(), "map-queue-switch-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var catalog = new AssetCatalog(root);
            using var source = new CatalogStampImageSource(catalog, () => null!, () => null);
            Assert.Equal(Environment.GetEnvironmentVariable("MAP_PROTECT_NEEDED_MIPS") != "0", source.ProtectNeededMips);
            Assert.Equal(Environment.GetEnvironmentVariable("MAP_KEEP_RECENT_MIPS") != "0", source.KeepRecentMips);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
