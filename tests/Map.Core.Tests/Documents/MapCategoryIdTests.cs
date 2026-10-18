using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Core.Tests.Documents;

public class MapCategoryIdTests
{
    private static MapDocument Document(MapCategoryId category = default) => new(
        DocumentId.New(), "map", DateTimeOffset.UnixEpoch,
        new CanvasDefinition(1024, 768, "px", "#FFFFFFFF"),
        [
            new BaseColorLayer(LayerId.New(), "background", true, false, 1.0, "#FFFFFFFF"),
            new ObjectLayer(LayerId.New(), "objects", true, false, 1.0, []),
        ],
        category: category);

    [Fact]
    public void The_default_id_is_the_standard_constructor()
    {
        MapCategoryId uninitialised = default;

        Assert.Equal("standard", uninitialised.Value);
        Assert.True(uninitialised.IsStandard);
        Assert.Equal(MapCategoryId.Standard, uninitialised);
        Assert.Equal(MapCategoryId.Standard.GetHashCode(), uninitialised.GetHashCode());
        Assert.Equal("standard", MapCategoryId.Standard.ToString());
    }

    [Theory]
    [InlineData("standard", true)]
    [InlineData("battle-rooms", true)]
    [InlineData("a1", true)]
    [InlineData("", false)]
    [InlineData("-leading", false)]
    [InlineData("trailing-", false)]
    [InlineData("Upper", false)]
    [InlineData("with space", false)]
    [InlineData("кириллица", false)]
    public void Only_lower_case_tokens_are_valid(string value, bool valid)
    {
        Assert.Equal(valid, MapCategoryId.IsValid(value));
        if (valid) Assert.Equal(value, MapCategoryId.Parse(value).Value);
        else Assert.Throws<ArgumentException>(() => new MapCategoryId(value));
    }

    [Fact]
    public void A_token_longer_than_the_limit_is_refused()
    {
        Assert.True(MapCategoryId.IsValid(new string('a', MapCategoryId.MaxLength)));
        Assert.False(MapCategoryId.IsValid(new string('a', MapCategoryId.MaxLength + 1)));
    }

    [Fact]
    public void A_document_without_a_category_is_standard()
    {
        Assert.True(Document().Category.IsStandard);
    }

    [Fact]
    public void Edits_versions_resize_and_crop_keep_the_category()
    {
        var other = new MapCategoryId("other-workshop");
        var document = Document(other);
        Assert.Equal(other, document.Category);

        var edited = document.WithLayers(document.Layers);
        Assert.Equal(other, edited.Category);

        var snapshot = MapCheckpointSnapshot.Capture(document);
        Assert.Equal(other, snapshot.Category);
        Assert.Equal(other, snapshot.Materialize(document.Id).Category);

        var plan = CanvasTransformService.PlanResize(document, 2048, 1536, CanvasAnchor.TopLeft);
        Assert.Equal(other, CanvasTransformService.ApplyResize(document, plan).Category);
    }
}
