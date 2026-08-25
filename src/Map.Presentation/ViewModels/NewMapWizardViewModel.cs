using System.Globalization;
using Map.Core.Documents;

namespace Map.Presentation.ViewModels;

/// <summary>
/// The minimal create wizard: title, dimensions, background, blank preset. Validation refuses
/// nonsense before a document ever exists.
/// </summary>
public sealed class NewMapWizardViewModel : ViewModelBase
{
    public const int MinDimension = 16;
    public const int MaxDimension = 16384;

    private string _title = "Новая карта";
    private string _widthText = "1920";
    private string _heightText = "1080";
    private string _background = "#FF1E2430";
    private string _validationError = "";

    /// <summary>Own neutral palette; final visual language is a later, owner-approved decision.</summary>
    public IReadOnlyList<string> BackgroundChoices { get; } =
        ["#FF1E2430", "#FFF2E9DC", "#FF223322", "#FFFFFFFF"];

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string WidthText
    {
        get => _widthText;
        set => SetField(ref _widthText, value);
    }

    public string HeightText
    {
        get => _heightText;
        set => SetField(ref _heightText, value);
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

    /// <summary>Swaps width and height - the wizard's orientation toggle.</summary>
    public void SwapOrientation() => (WidthText, HeightText) = (HeightText, WidthText);

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            ValidationError = "Название не может быть пустым.";
            return false;
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

        TryParseDimension(WidthText, out var width);
        TryParseDimension(HeightText, out var height);

        return new MapDocument(
            DocumentId.New(),
            Title.Trim(),
            createdUtc,
            new CanvasDefinition(width, height, "px", Background),
            [new Layer(LayerId.New(), "Объекты", visible: true, locked: false, opacity: 1.0, [])]);
    }

    private static bool TryParseDimension(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && value is >= MinDimension and <= MaxDimension;
}
