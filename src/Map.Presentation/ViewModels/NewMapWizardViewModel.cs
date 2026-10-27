using System.Globalization;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The minimal create wizard: category, title, dimensions, background, blank preset. Validation
/// refuses nonsense before a document ever exists.
/// </summary>
public sealed class NewMapWizardViewModel : ViewModelBase
{
    public const int MinDimension = CanvasDimensionPolicy.Minimum;
    public const int MaxDimension = CanvasDimensionPolicy.Maximum;

    private MapCategoryId _category = MapCategoryId.Standard;

    private string _title = "Новая карта";
    private string _widthText = "1920";
    private string _heightText = "1080";
    private string _background = "#FF1E2430";
    private string _validationError = "";
    private HexOrientation _hexOrientation = HexOrientation.Flat;
    private string _hexColumnsText = "30";
    private string _hexRowsText = "20";
    private string _hexSizeText = "64";

    /// <summary>Largest board side the create dialog offers; the format allows more.</summary>
    public const int MaxHexDimension = 1024;

    public const int MinHexSize = 24;

    public const int MaxHexSize = 256;

    /// <summary>Own neutral palette; final visual language is a later, owner-approved decision.</summary>
    public IReadOnlyList<string> BackgroundChoices { get; } =
        ["#FF1E2430", "#FFF2E9DC", "#FF223322", "#FFFFFFFF"];

    /// <summary>
    /// The workshop the new map belongs to (step 1 of the dialog). It is written into the document
    /// once and never changes afterwards.
    /// </summary>
    public MapCategoryId Category
    {
        get => _category;
        set { if (SetField(ref _category, value)) Revalidate(); }
    }

    /// <summary>True when the chosen category builds its map from hexes; step 2 then asks for the board.</summary>
    public bool IsHexCategory => MapCategories.IsHex(Category);

    /// <summary>Flat top (columns) or pointy top (rows) for a hex map.</summary>
    public HexOrientation HexOrientation
    {
        get => _hexOrientation;
        set { if (SetField(ref _hexOrientation, value)) Revalidate(); }
    }

    /// <summary>Hexes across a hex map.</summary>
    public string HexColumnsText
    {
        get => _hexColumnsText;
        set { if (SetField(ref _hexColumnsText, value)) Revalidate(); }
    }

    /// <summary>Hexes down a hex map.</summary>
    public string HexRowsText
    {
        get => _hexRowsText;
        set { if (SetField(ref _hexRowsText, value)) Revalidate(); }
    }

    /// <summary>Hex size (centre to corner) in canvas units.</summary>
    public string HexSizeText
    {
        get => _hexSizeText;
        set { if (SetField(ref _hexSizeText, value)) Revalidate(); }
    }

    /// <summary>The canvas a hex board of the current settings needs, or null while a field is invalid.</summary>
    public (int Width, int Height)? HexCanvasSize =>
        TryParseHexBoard(out var columns, out var rows, out var size)
            ? HexBoard.CanvasSizeFor(HexOrientation, size, columns, rows)
            : null;

    public string Title
    {
        get => _title;
        set { if (SetField(ref _title, value)) Revalidate(); }
    }

    public string WidthText
    {
        get => _widthText;
        set { if (SetField(ref _widthText, value)) Revalidate(); }
    }

    public string HeightText
    {
        get => _heightText;
        set { if (SetField(ref _heightText, value)) Revalidate(); }
    }

    public string Background
    {
        get => _background;
        set => SetField(ref _background, value);
    }

    public string ValidationError
    {
        get => _validationError;
        private set => SetField(ref _validationError, value);
    }

    /// <summary>
    /// Once an error is shown, every change of a field checks again, so the message goes away as
    /// soon as the fields are right (it used to stay until the next press of Create).
    /// </summary>
    private void Revalidate()
    {
        if (_validationError.Length > 0) Validate();
    }

    /// <summary>Swaps width and height - the wizard's orientation toggle.</summary>
    public void SwapOrientation() => (WidthText, HeightText) = (HeightText, WidthText);

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            ValidationError = "Название не может быть пустым.";
            return false;
        }

        if (IsHexCategory)
        {
            if (!TryParseHexBoard(out _, out _, out _))
            {
                ValidationError = $"Колонки и ряды — целые числа от 1 до {MaxHexDimension}, "
                    + $"размер гекса — от {MinHexSize} до {MaxHexSize}.";
                return false;
            }

            if (HexCanvasSize is not { } canvas || !CanvasDimensionPolicy.IsValid(canvas.Width) || !CanvasDimensionPolicy.IsValid(canvas.Height))
            {
                ValidationError = "Поле гексов такого размера не помещается на холст. Уменьшите число гексов или их размер.";
                return false;
            }

            ValidationError = "";
            return true;
        }

        if (!TryParseDimension(WidthText, out _) || !TryParseDimension(HeightText, out _))
        {
            ValidationError =
                $"Ширина и высота должны быть целыми числами от {MinDimension} до {MaxDimension}.";
            return false;
        }

        ValidationError = "";
        return true;
    }

    /// <summary>Builds the blank document, or null when validation fails.</summary>
    public MapDocument? Create(DateTimeOffset createdUtc)
    {
        if (!Validate())
        {
            return null;
        }

        if (IsHexCategory)
        {
            TryParseHexBoard(out var columns, out var rows, out var size);
            return HexMapFactory.Create(Title.Trim(), createdUtc, Category, HexOrientation, size, columns, rows, Background);
        }

        TryParseDimension(WidthText, out var width);
        TryParseDimension(HeightText, out var height);

        // R34: a city map starts generated - streets, blocks, houses, a wall - square on the shorter side.
        if (MapCategories.IsCity(Category))
            return CityBuilder.NewCityMap(Title.Trim(), Math.Min(width, height), new CitySettings { Name = Title.Trim(), Seed = StableSeed(Title.Trim()) }, Category, createdUtc);

        return new MapDocument(
            DocumentId.New(),
            Title.Trim(),
            createdUtc,
            new CanvasDefinition(width, height, "px", Background),
            [
                // DEC-069: a new map's sea is «Рябь штрихом»; it shows once land is painted.
                new BaseColorLayer(
                    LayerId.New(), "Цвет фона", visible: true, locked: false, opacity: 1.0, Background) { Sea = SeaStyle.NewMapDefault },
                new ObjectLayer(
                    LayerId.New(), "Объекты", visible: true, locked: false, opacity: 1.0, []),
            ],
            category: Category);
    }

    /// <summary>The same title gives the same city on every run (string hashes of .NET change between runs).</summary>
    private static int StableSeed(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        return (int)(hash & 0x7FFF);
    }

    private bool TryParseHexBoard(out int columns, out int rows, out int size)
    {
        var ok = int.TryParse(HexColumnsText, NumberStyles.None, CultureInfo.InvariantCulture, out columns)
            & int.TryParse(HexRowsText, NumberStyles.None, CultureInfo.InvariantCulture, out rows)
            & int.TryParse(HexSizeText, NumberStyles.None, CultureInfo.InvariantCulture, out size);
        return ok && columns is >= 1 and <= MaxHexDimension && rows is >= 1 and <= MaxHexDimension
            && size is >= MinHexSize and <= MaxHexSize;
    }

    private static bool TryParseDimension(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && CanvasDimensionPolicy.IsValid(value);
}
