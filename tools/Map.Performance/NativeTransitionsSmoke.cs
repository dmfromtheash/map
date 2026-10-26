using Avalonia.Controls;
using Map.Core.Documents;

namespace Map.Performance;

public sealed partial class Stage19Application
{
    private static readonly string[] TransitionsRequired =
    [
        "first-end-by-click", "second-end-by-click", "ends-checked", "missing-end-found", "resources-disposed",
    ];

    /// <summary>
    /// R29 through Notes → «Переходы»: «Вход в пещеру» placed by a click, its second end by another
    /// click; «Проверить концы» is clean; a shaft left without its second end is reported. Captured.
    /// </summary>
    private async Task TransitionsOnly()
    {
        var page = "#FF8FB0BA";
        var island = CliffLand("Остров", 512, 384, 460, 320);
        var document = new MapDocument(DocumentId.New(), "Мир", DateTimeOffset.UnixEpoch, new CanvasDefinition(1024, 768, "px", page),
            [new BaseColorLayer(LayerId.New(), "Море", true, false, 1, page), island, new ObjectLayer(LayerId.New(), "Объекты", true, false, 1, [])]);
        _view.Canvas.DisposeRenderCaches(); OpenEditor(document); await Settle();
        _editor.Session.Viewport.Set(0, 0, 1);
        _view.OpenNotes(); await Settle();
        Find<ComboBox>("NewTransitionKind").SelectedIndex = (int)TransitionKind.CaveEntrance;
        await Click("NewTransitionButton");
        Point(420, 360); await Task.Delay(400); await Settle();
        var placed = _editor.Transitions.SingleOrDefault();
        _report.Checks["first-end-by-click"] = placed is { Kind: TransitionKind.CaveEntrance } && Math.Abs(placed.From.X - 420) < 2 && placed.To is null;

        await Click("TransitionTargetHereButton");
        Point(700, 470); await Task.Delay(400); await Settle();
        _report.Checks["second-end-by-click"] = _editor.Transitions.Single().To is { MapId: null } to && Math.Abs(to.X - 700) < 2;
        await Click("CheckTransitionsButton"); await Task.Delay(300); await Settle();
        _report.Checks["ends-checked"] = Find<StackPanel>("TransitionIssues").Children.OfType<TextBlock>().Any(t => t.Text == "Все концы на месте.");

        Find<ComboBox>("NewTransitionKind").SelectedIndex = (int)TransitionKind.Shaft;
        await Click("NewTransitionButton");
        Point(300, 300); await Task.Delay(400); await Settle();
        await Click("CheckTransitionsButton"); await Task.Delay(300); await Settle();
        _report.Checks["missing-end-found"] = Find<StackPanel>("TransitionIssues").Children.OfType<Button>().Any();
        await Frame();
        _report.Screenshot = Path.ChangeExtension(Options.OutputPath, ".png");
        SaveVisual(_report.Screenshot);
        await Task.Delay(800); await Frame(); await Settle();
    }
}
