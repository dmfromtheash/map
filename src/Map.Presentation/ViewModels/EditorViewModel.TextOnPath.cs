using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// Labels along rivers, roads and hex paths, label presets and the «Имя» button (R07, S2/H15/S13).
/// Binding, sliding and every look change go through the ordinary text edit, so each is one Undo
/// step; the editing session lays bound labels out again after every edit of their line.
/// </summary>
public sealed partial class EditorViewModel
{
    private int _placeNameClicks;

    public TextPathBinding? SelectedTextPath => Session.SelectedObject?.Text?.Path;

    public static string TextPathTargetTitle(TextPathTarget target) => target switch
    {
        TextPathTarget.River => "реке",
        TextPathTarget.Road => "дороге",
        _ => "пути",
    };

    public static string TextPathSideTitle(TextPathSide side) => side switch
    {
        TextPathSide.On => "По линии",
        TextPathSide.Below => "Ниже",
        _ => "Выше",
    };

    /// <summary>Binds the selected text to the nearest river channel, road branch or hex path.</summary>
    public bool BindSelectedTextToNearestPath()
    {
        if (EditableSelection() is not { Text: { } text } label) { StatusDetail = "Выберите подпись."; return false; }
        var centre = TextOnPath.BaselineCentre(label);
        var reach = Math.Max(600, Math.Max(label.Width, label.Height) * 2);
        if (TextOnPath.BindNearest(Session.Document, centre.X, centre.Y, reach) is not { } binding)
        {
            StatusDetail = "Рядом нет реки, дороги или гекс-пути — подвиньте подпись ближе.";
            return false;
        }

        EditTextPath("Подпись вдоль пути", commit: true, _ => binding);
        StatusDetail = "Подпись идёт по " + TextPathTargetTitle(binding.Target) + ".";
        return Session.SelectedObject?.Text?.Path is not null;
    }

    /// <summary>Frees the selected label from its line; it stays where it is as ordinary text.</summary>
    public void UnbindSelectedTextPath()
    {
        EditText("Отвязать подпись", commit: true, text => text.Path is null ? text : text with { Path = null });
        StatusDetail = "Подпись больше не следует за линией.";
    }

    public void SetTextPathPosition(double position, bool commit = true) =>
        EditTextPath("Место подписи на пути", commit, path => path with { Position = Math.Round(Finite(position, path.Position, 0, 1), 4) });

    public void SetTextPathSide(TextPathSide side) =>
        EditTextPath("Сторона подписи", commit: true, path => path with { Side = side });

    public void SetTextPathGap(double gap, bool commit = true) =>
        EditTextPath("Отступ подписи", commit, path => path with { Gap = Finite(gap, path.Gap, -200, 400) });

    public void SetTextPathKeepUpright(bool upright) =>
        EditTextPath("Подпись не вверх ногами", commit: true, path => path with { KeepUpright = upright });

    /// <summary>Turns the reading direction; with «не вверх ногами» on, this also switches it off.</summary>
    public void FlipTextPathDirection()
    {
        var reversed = Session.SelectedObject is { } label && TextOnPath.ReadsAgainstLine(Session.Document, label);
        EditTextPath("Перевернуть подпись", commit: true, path => path.KeepUpright
            ? path with { KeepUpright = false, Reverse = !reversed }
            : path with { Reverse = !path.Reverse });
    }

    public void ApplyLabelPreset(LabelPreset preset)
    {
        EditText("Пресет подписи: " + LabelPresets.Title(preset), commit: true, text => LabelPresets.Apply(text, preset));
        StatusDetail = "Пресет «" + LabelPresets.Title(preset) + "».";
    }

    /// <summary>Puts a fresh place name of the given kind into the selected label (and its object name).</summary>
    public string? GenerateLabelName(LabelPreset kind)
    {
        if (EditableSelection() is not { Text: not null } label) { StatusDetail = "Выберите подпись."; return null; }
        var seed = (int)(StableSeed(label.Id.Value) + (uint)_placeNameClicks++ * 7919u);
        var name = EntityNameGenerator.PlaceName(Session.Document, kind, seed);
        var before = label;
        var named = new SceneObject(before.Id, before.Kind, EntityNameGenerator.Text(Session.Document, name),
            before.X, before.Y, before.Width, before.Height, before.RotationDegrees,
            before.FillColor, before.StrokeColor, before.StrokeWidth, before.Visible, before.Locked)
        {
            Sublayer = before.Sublayer, GroupId = before.GroupId, GroupName = before.GroupName,
            FillGradient = before.FillGradient, ObjectOpacity = before.ObjectOpacity,
            Text = before.Text! with { Content = name },
        }.KeepingMeaningOf(before);
        ReplaceSelected(before, named, "Имя подписи");
        StatusDetail = "Имя: " + name + ".";
        return name;
    }

    /// <summary>
    /// Adds a label that follows the selected river channel, road branch or hex path, named after it
    /// and dressed with the matching preset.
    /// </summary>
    public bool AddLabelAlongSelectedPath(TextPathTarget? only = null)
    {
        TextPathBinding binding; string content; LabelPreset preset;
        if (only is null or TextPathTarget.River && SelectedRiver is { } river && SelectedRiverChannel is { Closed: false } channel)
        {
            binding = new TextPathBinding { Target = TextPathTarget.River, TargetId = river.Id, PartId = channel.Id, Side = TextPathSide.Above };
            content = river.Name; preset = LabelPreset.River;
        }
        else if (only is null or TextPathTarget.Road && SelectedRoad is { } road && (SelectedRoadBranch ?? road.Branches.FirstOrDefault(b => !b.Closed)) is { Closed: false } branch)
        {
            binding = new TextPathBinding { Target = TextPathTarget.Road, TargetId = road.Id, PartId = branch.Id, Side = TextPathSide.Above };
            content = road.Name; preset = LabelPreset.Village;
        }
        else if (only is null or TextPathTarget.HexPath && SelectedHexPath is { } path)
        {
            binding = new TextPathBinding { Target = TextPathTarget.HexPath, TargetId = path.Id.Value, Side = TextPathSide.Above };
            content = string.IsNullOrWhiteSpace(path.Name) ? "Тракт" : path.Name; preset = LabelPreset.Village;
        }
        else
        {
            StatusDetail = "Выберите реку, дорогу или гекс-путь.";
            return false;
        }

        if (LabelLayer() is not { } layer) { StatusDetail = "Нет доступного слоя объектов для подписи."; return false; }
        var style = LabelPresets.Apply(new TextStyle { Content = content, Alignment = TextAlignment.Center }, preset) with { Path = binding };
        var draft = new SceneObject(ObjectId.New(), SceneObjectKind.Text, EntityNameGenerator.Text(Session.Document, content),
            0, 0, 1, 1, 0, "#FF20242B", "#FFFFFFFF", 0, true, false) { Text = style };
        var label = TextOnPath.Layout(Session.Document, draft);
        Session.Execute(new AddObjectCommand(layer.Id, label));
        if (Session.Document.FindLayer(layer.Id) is not ObjectLayer added || added.Objects.All(o => o.Id != label.Id)) return false;
        // The new label is what the user edits next: the select tool shows its text panel
        // (presets, «Имя», «Вдоль пути») instead of the river or road panel.
        ActiveTool = EditorTool.Select;
        Session.SelectedObjectId = label.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
        StatusDetail = "Подпись «" + content + "» идёт по " + TextPathTargetTitle(binding.Target) + ".";
        return true;
    }

    /// <summary>Hex maps keep labels on their «Подписи» layer; elsewhere the active object layer.</summary>
    private ObjectLayer? LabelLayer() =>
        Session.Document.Layers.OfType<ObjectLayer>().FirstOrDefault(l =>
            l.Name == HexMapFactory.LabelsLayerName && l is { Visible: true, Locked: false } && Session.Document.Layers.OfType<HexLayer>().Any())
        ?? ActiveObjectLayer()
        ?? Session.Document.Layers.OfType<ObjectLayer>().LastOrDefault(l => l is { Visible: true, Locked: false });

    private void EditTextPath(string name, bool commit, Func<TextPathBinding, TextPathBinding> change) =>
        EditAppearance(name, commit, before =>
        {
            if (before.Text is not { } text) return before;
            var current = text.Path ?? new TextPathBinding();
            var next = change(current);
            if (text.Path is not null && next.Equals(current)) return before;
            return TextOnPath.Layout(Session.Document, CopyObject(before) with { Text = text with { Path = next } });
        });

    private static uint StableSeed(Guid id)
    {
        var hash = 2166136261u;
        foreach (var b in id.ToByteArray()) hash = (hash ^ b) * 16777619u;
        return hash;
    }
}
