using Map.Core.Documents;
using Map.Core.Editing;
using Map.Export;
using Map.Presentation.Services;

namespace Map.Presentation.ViewModels;

/// <summary>
/// R32: the pointcrawl - «Место» arms a click that puts a numbered place on the map, «Путь» arms
/// two clicks on places; a place may be a card of the world (and lead to its map in the atlas);
/// ways carry hours, danger, hidden and one-way; «Раскладка» spreads the places; the PDF key is
/// written for the master or for the players. Every change is one undo step.
/// </summary>
public sealed partial class EditorViewModel
{
    private enum PointcrawlPick { None, Place, LinkFrom, LinkTo }

    private PointcrawlPick _pointcrawlPick;
    private Guid? _pointcrawlLinkFrom;

    public MapPointcrawl Pointcrawl => Session.Document.Pointcrawl;
    public Guid? SelectedPointcrawlNodeId { get; set; }
    public Guid? SelectedPointcrawlEdgeId { get; set; }
    public PointcrawlNode? SelectedPointcrawlNode => Pointcrawl.Nodes.FirstOrDefault(n => n.Id == SelectedPointcrawlNodeId);
    public PointcrawlEdge? SelectedPointcrawlEdge => Pointcrawl.Edges.FirstOrDefault(e => e.Id == SelectedPointcrawlEdgeId);
    public bool PointcrawlPickArmed => _pointcrawlPick != PointcrawlPick.None;

    private void SetPointcrawl(MapPointcrawl after, string name)
    {
        try { Session.Execute(new SetPointcrawlCommand(Pointcrawl, after, name)); }
        catch (ArgumentException ex) { StatusDetail = ex.Message; return; }
        Raise(nameof(Pointcrawl));
        AfterEdit();
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public void StartAddPointcrawlPlace()
    {
        _pointcrawlPick = PointcrawlPick.Place;
        StatusDetail = "Щёлкните на карте место пойнткроула.";
        Raise(nameof(PointcrawlPickArmed));
    }

    public void StartLinkPointcrawl()
    {
        if (Pointcrawl.Nodes.Length < 2) { StatusDetail = "Нужно хотя бы два места."; return; }
        _pointcrawlPick = PointcrawlPick.LinkFrom;
        _pointcrawlLinkFrom = null;
        StatusDetail = "Щёлкните место, откуда идёт путь.";
        Raise(nameof(PointcrawlPickArmed));
    }

    public void CancelPointcrawlPick()
    {
        if (_pointcrawlPick == PointcrawlPick.None) return;
        _pointcrawlPick = PointcrawlPick.None; _pointcrawlLinkFrom = null;
        StatusDetail = "";
        Raise(nameof(PointcrawlPickArmed));
    }

    /// <summary>The place under a click: the nearest within about 24 screen pixels.</summary>
    public PointcrawlNode? PointcrawlNodeAt(double x, double y)
    {
        var reach = 24 / Math.Max(1e-6, Session.Viewport.Scale);
        return Pointcrawl.Nodes.Where(n => double.Hypot(n.X - x, n.Y - y) <= reach).MinBy(n => double.Hypot(n.X - x, n.Y - y));
    }

    public bool PressPointcrawlPick(double x, double y)
    {
        switch (_pointcrawlPick)
        {
            case PointcrawlPick.Place:
            {
                _pointcrawlPick = PointcrawlPick.None;
                Raise(nameof(PointcrawlPickArmed));
                var node = new PointcrawlNode(Guid.NewGuid(), $"Место {Pointcrawl.Nodes.Length + 1}", Math.Round(x, 1), Math.Round(y, 1));
                SetPointcrawl(Pointcrawl with { Nodes = Pointcrawl.Nodes.Add(node) }, "Место пойнткроула");
                SelectedPointcrawlNodeId = node.Id; SelectedPointcrawlEdgeId = null;
                StatusDetail = $"Место {Pointcrawl.NumberOf(node.Id)} поставлено. Назовите его и свяжите путями.";
                return true;
            }
            case PointcrawlPick.LinkFrom:
            {
                if (PointcrawlNodeAt(x, y) is not { } from) { StatusDetail = "Щёлкните ближе к месту."; return true; }
                _pointcrawlLinkFrom = from.Id;
                _pointcrawlPick = PointcrawlPick.LinkTo;
                StatusDetail = $"Путь от «{from.Name}». Щёлкните место, куда он ведёт.";
                return true;
            }
            case PointcrawlPick.LinkTo:
            {
                if (PointcrawlNodeAt(x, y) is not { } to || to.Id == _pointcrawlLinkFrom) { StatusDetail = "Щёлкните другое место."; return true; }
                var fromId = _pointcrawlLinkFrom!.Value;
                _pointcrawlPick = PointcrawlPick.None; _pointcrawlLinkFrom = null;
                Raise(nameof(PointcrawlPickArmed));
                if (Pointcrawl.Edges.Any(e => (e.From == fromId && e.To == to.Id) || (e.From == to.Id && e.To == fromId)))
                { StatusDetail = "Эти места уже связаны."; return true; }
                var edge = new PointcrawlEdge(Guid.NewGuid(), fromId, to.Id);
                SetPointcrawl(Pointcrawl with { Edges = Pointcrawl.Edges.Add(edge) }, "Путь пойнткроула");
                SelectedPointcrawlEdgeId = edge.Id; SelectedPointcrawlNodeId = null;
                StatusDetail = "Путь проложен: задайте время, опасность и флаги.";
                return true;
            }
            default:
                return false;
        }
    }

    public void EditPointcrawlNode(Guid id, string name, string note, bool visited, Guid? entityId)
    {
        if (Pointcrawl.Nodes.FirstOrDefault(n => n.Id == id) is not { } before || string.IsNullOrWhiteSpace(name)) return;
        var after = before with { Name = name.Trim().Length > 120 ? name.Trim()[..120] : name.Trim(), Note = note.Length > 4000 ? note[..4000] : note, Visited = visited, EntityId = entityId };
        if (after == before) return;
        SetPointcrawl(Pointcrawl with { Nodes = Pointcrawl.Nodes.Replace(before, after) }, "Изменить место");
    }

    public void SetPointcrawlVisited(Guid id, bool visited)
    {
        if (Pointcrawl.Nodes.FirstOrDefault(n => n.Id == id) is not { } before || before.Visited == visited) return;
        SetPointcrawl(Pointcrawl with { Nodes = Pointcrawl.Nodes.Replace(before, before with { Visited = visited }) }, visited ? "Место посещено" : "Место не посещено");
    }

    public void DeletePointcrawlNode(Guid id)
    {
        if (Pointcrawl.Nodes.FirstOrDefault(n => n.Id == id) is not { } node) return;
        SetPointcrawl(Pointcrawl with { Nodes = Pointcrawl.Nodes.Remove(node), Edges = [.. Pointcrawl.Edges.Where(e => e.From != id && e.To != id)] }, "Удалить место");
        if (SelectedPointcrawlNodeId == id) SelectedPointcrawlNodeId = null;
    }

    public void EditPointcrawlEdge(Guid id, string name, double hours, int danger, bool hidden, bool oneWay)
    {
        if (Pointcrawl.Edges.FirstOrDefault(e => e.Id == id) is not { } before || !double.IsFinite(hours)) return;
        var after = before with
        {
            Name = name.Trim().Length > 120 ? name.Trim()[..120] : name.Trim(),
            Hours = Math.Clamp(Math.Round(hours, 1), 0, 100_000), Danger = Math.Clamp(danger, 0, 5), Hidden = hidden, OneWay = oneWay,
        };
        if (after == before) return;
        SetPointcrawl(Pointcrawl with { Edges = Pointcrawl.Edges.Replace(before, after) }, "Изменить путь");
    }

    public void DeletePointcrawlEdge(Guid id)
    {
        if (Pointcrawl.Edges.FirstOrDefault(e => e.Id == id) is not { } edge) return;
        SetPointcrawl(Pointcrawl with { Edges = Pointcrawl.Edges.Remove(edge) }, "Удалить путь");
        if (SelectedPointcrawlEdgeId == id) SelectedPointcrawlEdgeId = null;
    }

    public void SetPointcrawlOptions(bool shown, bool fogByVisit)
    {
        if (Pointcrawl.Shown == shown && Pointcrawl.FogByVisit == fogByVisit) return;
        SetPointcrawl(Pointcrawl with { Shown = shown, FogByVisit = fogByVisit }, "Настройки пойнткроула");
    }

    public void ArrangePointcrawl()
    {
        if (Pointcrawl.Nodes.Length < 2) { StatusDetail = "Раскладывать нечего: меньше двух мест."; return; }
        SetPointcrawl(PointcrawlLayout.Arrange(Pointcrawl, Session.Document.Canvas.Width, Session.Document.Canvas.Height), "Раскладка пойнткроула");
        StatusDetail = "Места разложены: связанные ближе, остальные раздвинуты. Ctrl+Z вернёт как было.";
    }

    /// <summary>A place bound to a card: the card's place on another map of the atlas opens, or the view goes to the place.</summary>
    public void OpenPointcrawlPlace(Guid id)
    {
        if (Pointcrawl.Nodes.FirstOrDefault(n => n.Id == id) is not { } node) return;
        if (node.EntityId is { } entity && Entities.FirstOrDefault(e => e.Id == entity) is { } card
            && card.Bindings.FirstOrDefault(b => b.MapId is { } m && m != Session.Document.Id.Value) is { MapId: { } map })
        { OpenAtlasMap(map); return; }
        var viewport = Session.Viewport;
        viewport.Set(node.X - (viewport.ViewWidth / 2 / viewport.Scale), node.Y - (viewport.ViewHeight / 2 / viewport.Scale), viewport.Scale);
        InvalidateScene(EditorChangeReason.CanvasInvalidation);
    }

    public async Task<int> ExportPointcrawlKeyAsync(string path, bool players)
    {
        var document = Session.Document;
        try
        {
            return await Task.Run(() =>
            {
                using var stamps = new ExportStampImageSource(document, DocumentPath, _assetCatalog);
                return PointcrawlKey.Write(path, document, players, stamps);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusDetail = "Не удалось сохранить ключ: " + ex.Message;
            _log.Error("POINTCRAWL_KEY_FAILED", ex);
            return 0;
        }
    }

    public async Task<int> PickAndExportPointcrawlKeyAsync(bool players)
    {
        if (await _filePicker.PickSaveAsync(SafeExportName(Session.Document.Title) + (players ? " - пойнткроул для игроков.pdf" : " - пойнткроул.pdf")) is not { } path) return 0;
        var pages = await ExportPointcrawlKeyAsync(path, players);
        if (pages > 0) StatusDetail = $"Ключ сохранён: {pages} стр.";
        return pages;
    }
}
