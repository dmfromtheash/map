using Map.Core.Documents;
using Map.Core.Editing;

namespace Map.Presentation.ViewModels;

/// <summary>R12: waterfall, rapids and ford on the selected river node, and the «Дельта» macro.</summary>
public sealed partial class EditorViewModel
{
    private string _riverPreviewName = "Добавить извилины";
    private int _riverDeltaArms = 3;
    private double _riverDeltaLengthPercent = 25;
    private double _riverDeltaSpread = 70;

    /// <summary>What the water does at the selected node; null when no flowing node is selected.</summary>
    public RiverNodeFeature? SelectedRiverNodeFeature =>
        SelectedRiverChannel is { Closed: false } c && _riverNode >= 0 && _riverNode < c.Nodes.Length ? c.Nodes[_riverNode].Feature : null;

    public void SetSelectedRiverNodeFeature(RiverNodeFeature feature)
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { Closed: false } channel
            || _riverNode < 0 || _riverNode >= channel.Nodes.Length) return;
        var name = feature switch
        {
            RiverNodeFeature.Waterfall => "Водопад в узле реки",
            RiverNodeFeature.Rapids => "Пороги в узле реки",
            RiverNodeFeature.Ford => "Брод в узле реки",
            _ => "Убрать особенность узла реки",
        };
        CommitRiver(RiverEditor.SetNodeFeature(river, channel.Id, _riverNode, feature), name);
    }

    /// <summary>Arms of the delta, the channel itself included (2..7).</summary>
    public int RiverDeltaArms
    {
        get => _riverDeltaArms;
        set => SetField(ref _riverDeltaArms, Math.Clamp(value, RiverEditor.MinimumDeltaArms, RiverEditor.MaximumDeltaArms));
    }

    /// <summary>How far up the channel the arms part, in percent of its length.</summary>
    public double RiverDeltaLengthPercent
    {
        get => _riverDeltaLengthPercent;
        set { if (double.IsFinite(value)) SetField(ref _riverDeltaLengthPercent, Math.Clamp(value, 5, 90)); }
    }

    /// <summary>The angle the arms fan over, in degrees.</summary>
    public double RiverDeltaSpread
    {
        get => _riverDeltaSpread;
        set { if (double.IsFinite(value)) SetField(ref _riverDeltaSpread, Math.Clamp(value, 10, 150)); }
    }

    /// <summary>A delta needs a flowing channel whose mouth is free (it ends in the sea, a lake shore or the map edge).</summary>
    public bool CanBuildDelta => SelectedRiverChannel is { Closed: false, Mouth: null };

    /// <summary>
    /// Shows the delta on the canvas without touching the map, like the meanders: «Применить» makes
    /// it one undoable change, Esc drops it.
    /// </summary>
    public void PreviewDeltaOnSelectedChannel()
    {
        if (SelectedRiver is not { } river || SelectedRiverChannel is not { Closed: false } channel) return;
        if (RiverLayerOf(river.Id) is not { Visible: true, Locked: false } layer) { StatusDetail = "Слой реки скрыт или заблокирован."; return; }
        CancelRiverGesture();
        var length = channel.Nodes.Zip(channel.Nodes.Skip(1), (a, b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2))).Sum();
        River next;
        try { next = RiverEditor.Delta(river, channel.Id, _riverDeltaArms, length * _riverDeltaLengthPercent / 100, _riverDeltaSpread, NextGestureSeed()); }
        catch (InvalidOperationException ex) { StatusDetail = ex.Message; return; }
        _riverOriginal = river; _riverDraft = next; _riverLayer = layer.Id; _riverPreviewing = true; _riverPreviewName = "Дельта";
        StatusDetail = $"Предпросмотр дельты: {_riverDeltaArms} рукава(ов). «Применить предпросмотр» — принять, Esc — отказаться.";
        RaiseRiverState();
    }
}
