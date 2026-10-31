using System.Collections.Immutable;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>What the next click on the map is for (R24).</summary>
public enum GeographyPick { None, RoadFrom, RoadTo, RiverFrom }

/// <summary>
/// R24: «Проложить дорогу A→B» and «Река отсюда в море» take points from the map and add an
/// ordinary road or river (one undo step); «Проверка географии» lists what looks wrong, and an
/// object can be marked «намеренно» so the check leaves it alone.
/// </summary>
public sealed partial class EditorViewModel
{
    private VectorPoint? _roadFrom;

    public GeographyPick GeographyPick { get; private set; }

    public void ArmAutoRoad()
    {
        GeographyPick = GeographyPick == GeographyPick.None ? GeographyPick.RoadFrom : GeographyPick.None;
        _roadFrom = null;
        StatusDetail = GeographyPick == GeographyPick.None ? "" : "Щёлкните, откуда начать дорогу (A).";
        Raise(nameof(GeographyPick));
    }

    public void ArmRiverToSea()
    {
        GeographyPick = GeographyPick == GeographyPick.RiverFrom ? GeographyPick.None : GeographyPick.RiverFrom;
        StatusDetail = GeographyPick == GeographyPick.None ? "" : "Щёлкните, где начинается река: она потечёт вниз к морю.";
        Raise(nameof(GeographyPick));
    }

    /// <summary>A click while a pick is armed; true when it was taken.</summary>
    public bool PressGeographyPick(double worldX, double worldY)
    {
        switch (GeographyPick)
        {
            case GeographyPick.RoadFrom:
                _roadFrom = new VectorPoint(worldX, worldY);
                GeographyPick = GeographyPick.RoadTo;
                StatusDetail = "Теперь щёлкните, куда вести дорогу (B).";
                Raise(nameof(GeographyPick));
                return true;
            case GeographyPick.RoadTo when _roadFrom is { } from:
                GeographyPick = GeographyPick.None; Raise(nameof(GeographyPick));
                AutoRoad(from, new VectorPoint(worldX, worldY));
                return true;
            case GeographyPick.RiverFrom:
                GeographyPick = GeographyPick.None; Raise(nameof(GeographyPick));
                RiverToSea(new VectorPoint(worldX, worldY));
                return true;
            default:
                return false;
        }
    }

    public bool AutoRoad(VectorPoint from, VectorPoint to)
    {
        if (ActiveObjectLayer() is not { } layer) { StatusDetail = "Нет доступного слоя объектов для дороги."; return false; }
        if (Geography.RouteRoad(Session.Document, from, to) is not { Count: >= 2 } points)
        { StatusDetail = "Пути по суше между этими точками нет: море или обрыв между ярусами. Отметьте точки на одной земле."; return false; }
        var level = new TerrainProbe(Session.Document).At(from.X, from.Y).Land?.Level ?? 0;
        var road = RoadEditor.NewRoad(points, 3, RoadStyle.Painted(), "Дорога") with { Level = level };
        Session.Execute(new RoadCommand(layer.Id, road.Id, road, "Проложить дорогу"));
        StatusDetail = $"Дорога проложена по суше: {points.Count} узлов. Её можно править как обычную.";
        AfterEdit(); return true;
    }

    public bool RiverToSea(VectorPoint from)
    {
        if (ActiveObjectLayer() is not { } layer) { StatusDetail = "Нет доступного слоя объектов для реки."; return false; }
        if (Geography.RouteRiver(Session.Document, from) is not { Count: >= 3 } points)
        { StatusDetail = "Отсюда река не находит моря поблизости: щёлкните на суше ближе к берегу."; return false; }
        var level = new TerrainProbe(Session.Document).At(from.X, from.Y).Land?.Level ?? 0;
        var channel = RiverEditor.NewChannel(points, 4);
        // A river widens towards its mouth.
        channel = channel with { Nodes = [.. channel.Nodes.Select((n, i) => n with { Width = Math.Round(2 + (10.0 * i / Math.Max(1, channel.Nodes.Length - 1)), 1) })] };
        var river = new River
        {
            Name = EntityNameGenerator.PlaceName(Session.Document, LabelPreset.River, NextGestureSeed()),
            Channels = [channel], Style = RiverStyle.Natural(), Level = level,
        };
        Session.Execute(new RiverCommand(layer.Id, river.Id, river, "Река отсюда в море"));
        StatusDetail = $"Река «{river.Name}» стекает к морю: {points.Count} узлов.";
        AfterEdit(); return true;
    }

    /// <summary>Plan 2.2: towns and villages placed by hand from the catalog are checked like generated ones.</summary>
    public ImmutableArray<GeographyIssue> CheckGeography() => Geography.Inspect(Session.Document, settlement: IsSettlement);

    /// <summary>Marks the object of a finding «намеренно» (one undo step); the check no longer reports it.</summary>
    public bool MarkIntentional(GeographyIssue issue)
    {
        foreach (var layer in Session.Document.Layers.OfType<ObjectLayer>())
        {
            if (layer.Rivers.FirstOrDefault(r => r.Id == issue.ObjectId) is { } river)
            {
                Session.Execute(new RiverCommand(layer.Id, river.Id, river with { GeographyIntentional = true }, "Намеренно"));
                AfterEdit(); return true;
            }
            if (layer.Roads.FirstOrDefault(r => r.Id == issue.ObjectId) is { } road)
            {
                Session.Execute(new RoadCommand(layer.Id, road.Id, road with { GeographyIntentional = true }, "Намеренно"));
                AfterEdit(); return true;
            }
            if (layer.Objects.FirstOrDefault(o => o.Id.Value == issue.ObjectId) is { } item)
            {
                Session.Execute(new ReplaceObjectsCommand([item], [item with { GeographyIntentional = true }], "Намеренно"));
                AfterEdit(); return true;
            }
        }
        return false;
    }

    /// <summary>Centres the view on a finding.</summary>
    public void ShowGeographyIssue(GeographyIssue issue)
    {
        var viewport = Session.Viewport;
        viewport.Set(issue.X - (viewport.ViewWidth / 2 / viewport.Scale), issue.Y - (viewport.ViewHeight / 2 / viewport.Scale), viewport.Scale);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }
}
