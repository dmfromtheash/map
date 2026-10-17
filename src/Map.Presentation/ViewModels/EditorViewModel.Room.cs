using Map.Assets;
using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>
/// «Комната» for dungeon maps, built on the existing shape: floor = shape fill, wall = shape
/// stroke (colour or repeated asset, «стена блокирует свет»), doors = shape portals, caps and
/// rounded corners = <see cref="RoomStyle"/>. Every change is one Undo step.
/// </summary>
public sealed partial class EditorViewModel
{
    /// <summary>Own palette of a new room: a light stone floor and a dark wall.</summary>
    public const string RoomFloorColor = "#FFC9BFA8";
    public const string RoomWallColor = "#FF3A342E";
    public const double RoomWallWidth = 14;
    public const double RoomDoorWidth = 48;

    /// <summary>The selected room, or null when the selection is not a room.</summary>
    public RoomStyle? SelectedRoom => Session.SelectedObject?.Room;

    /// <summary>True when the selection is a shape that can become a room.</summary>
    public bool CanMakeSelectedRoom => Session.SelectedObject is { Shape: not null, Room: null, Locked: false };

    /// <summary>
    /// A new rectangular room in the middle of the view: floor, wall that blocks light, rounded
    /// corners. It is selected so its properties are at hand.
    /// </summary>
    public bool CreateRoom()
    {
        var (layer, createLayer) = EnsureObjectLayer();
        if (layer is null)
        {
            StatusDetail = NoUsableObjectLayer;
            return false;
        }
        var (cx, cy) = Session.Viewport.ViewWidth > 0
            ? Session.Viewport.ScreenToWorld(Session.Viewport.ViewWidth / 2, Session.Viewport.ViewHeight / 2)
            : (Session.Document.Canvas.Width / 2.0, Session.Document.Canvas.Height / 2.0);
        var centre = new VectorPoint(cx, cy);
        var width = Math.Clamp(Session.Document.Canvas.Width / 4.0, 96, 512);
        var height = Math.Round(width * 0.7);
        var room = VectorGeometry.CreateShape(ObjectId.New(), NextRoomName(), ShapePrimitiveKind.Polygon,
                new WorldBounds(Math.Round(centre.X - (width / 2)), Math.Round(centre.Y - (height / 2)),
                    Math.Round(centre.X + (width / 2)), Math.Round(centre.Y + (height / 2))),
                new ShapeFillStyle { Color = RoomFloorColor, Opacity = 1 },
                new VectorStrokeStyle
                {
                    Color = RoomWallColor, Width = RoomWallWidth, BlocksLight = true,
                    EndCap = VectorCap.Round, NodeCap = VectorCap.Butt,
                    ShadowOffsetX = 3, ShadowOffsetY = 3, ShadowBlur = 6, ShadowOpacity = 0.35,
                })
            with { Room = new RoomStyle { CornerRadius = RoomWallWidth } };
        ExecuteWithOptionalLayer(new AddObjectCommand(layer.Id, room), createLayer, "Add room");
        Session.SelectedObjectId = room.Id;
        _activeLayerId = layer.Id;
        AfterEdit();
        StatusDetail = $"Добавлена «{room.Name}». Пол, стену, шапки и двери можно настроить в панели «Комната».";
        return true;
    }

    /// <summary>Turns the selected shape into a room: its fill is the floor, its stroke the wall.</summary>
    public bool MakeSelectedRoom()
    {
        if (EditableAppearanceSelection() is not { Shape: { } shape, Room: null } before)
        {
            StatusDetail = "Выберите фигуру, чтобы сделать её комнатой.";
            return false;
        }
        var stroke = shape.Stroke with
        {
            BlocksLight = true,
            Width = Math.Max(shape.Stroke.Width, 6),
        };
        var after = before with
        {
            Shape = new ShapeStyle(shape.Regions, shape.Fill, stroke, shape.Portals),
            Room = new RoomStyle { CornerRadius = stroke.Width },
        };
        ReplaceSelected(before, after, "Make room");
        StatusDetail = "Фигура стала комнатой: заливка — пол, обводка — стена, проходы — двери.";
        return true;
    }

    /// <summary>Back to an ordinary shape; floor, wall and doors stay as they are.</summary>
    public bool ClearSelectedRoom()
    {
        if (EditableAppearanceSelection() is not { Room: not null } before) return false;
        ReplaceSelected(before, before with { Room = null }, "Remove room");
        return true;
    }

    /// <summary>Edits the caps and corner rounding of the selected room (one Undo step).</summary>
    public bool SetSelectedRoom(Func<RoomStyle, RoomStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (EditableAppearanceSelection() is not { Room: { } room } before) return false;
        var updated = change(room) with
        {
            CornerRadius = Math.Clamp(change(room).CornerRadius, 0, RoomStyle.MaxCornerRadius),
        };
        updated = updated with
        {
            CapScale = Math.Clamp(updated.CapScale, 0.25, RoomStyle.MaxCapScale),
            CapAngleDegrees = Math.Clamp(updated.CapAngleDegrees, -360, 360),
        };
        if (updated == room || !updated.IsValid) return false;
        ReplaceSelected(before, before with { Room = updated }, "Edit room");
        return true;
    }

    /// <summary>Wall thickness of the selected room, in map units.</summary>
    public bool SetSelectedRoomWallWidth(double width)
    {
        if (EditableAppearanceSelection() is not { Shape: { } shape, Room: not null } before) return false;
        var clamped = Math.Clamp(width, 1, 256);
        if (Math.Abs(shape.Stroke.Width - clamped) < 1e-9) return false;
        ReplaceSelected(before, before with
        {
            Shape = new ShapeStyle(shape.Regions, shape.Fill, shape.Stroke with { Width = clamped }, shape.Portals),
        }, "Edit room wall");
        return true;
    }

    /// <summary>The chosen library asset becomes the cap of every corner (embedded in the map).</summary>
    public bool UseAssetAsRoomCaps(AssetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_assetResolver is null || EditableAppearanceSelection() is not { Room: { } room } before)
        {
            StatusDetail = "Выберите комнату и доступный ассет.";
            return false;
        }
        var reference = ResolveAssetForDocument(record, out _, out var refusal);
        if (reference is null)
        {
            StatusDetail = refusal ?? "Файл ассета отсутствует или не совпадает с его SHA-256.";
            return false;
        }
        var after = before with { Room = room with { CapAssetSha256 = record.Sha256 } };
        Session.Execute(new VectorAssetStyleCommand(before, after, reference, "Use asset as room caps"));
        AfterEdit();
        StatusDetail = $"Ассет «{record.DisplayName}» стоит шапкой на каждом углу комнаты.";
        return true;
    }

    /// <summary>The asset chosen in the asset panel, for the room's «из выбранного ассета» buttons.</summary>
    public AssetRecord? CurrentAssetRecord =>
        CurrentVariant is { } variant ? _assetCatalog?.Find(variant.Sha256) : null;

    /// <summary>A door in the middle of the selected room's longest straight wall.</summary>
    public bool AddRoomDoor()
    {
        if (EditableAppearanceSelection() is not { Shape: { } shape, Room: not null } before || shape.Regions.Count == 0) return false;
        var outline = VectorGeometry.RegionOutline(before, shape.Regions[0]);
        var lengths = outline.Zip(outline.Skip(1), (a, b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)))).ToArray();
        var total = lengths.Sum();
        if (total <= 0) return false;
        var longest = Array.IndexOf(lengths, lengths.Max());
        var position = (lengths.Take(longest).Sum() + (lengths[longest] / 2)) / total;
        var width = Math.Min(RoomDoorWidth, lengths[longest] * 0.8);
        ReplaceSelected(before, VectorObjectEditor.AddPortal(before, Math.Clamp(position, 0, 1), width), "Add room door");
        StatusDetail = "Дверь поставлена посередине самой длинной стены; её можно сдвинуть в «Проходах».";
        return true;
    }

    /// <summary>Lets the panel explain why a room button did nothing.</summary>
    public void StatusDetailForView(string message) => StatusDetail = message;

    private string NextRoomName()
    {
        var used = Session.Document.Layers.OfType<ObjectLayer>().SelectMany(layer => layer.Objects)
            .Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        for (var index = 1; ; index++)
        {
            var name = $"Комната {index}";
            if (!used.Contains(name)) return name;
        }
    }
}
