using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// R28: «Согласование» - for the open map, what its child maps would take from it (rivers and
/// roads, edited-by-hand copies left alone), then «Записать в подкарты»; for a hex child,
/// «Гексы из рисунка» from the painted parent.
/// </summary>
public sealed partial class EditorView
{
    private Window? _syncWindow;
    private Control? _syncContent;

    private void ShowScaleSync()
    {
        _syncWindow = ShowAuxiliaryWindow(_syncWindow, "Согласование масштабов", BuildScaleSyncContent(), 640, 600, "ScaleSyncWindow");
    }

    private Control BuildScaleSyncContent()
    {
        if (_syncContent is not null) return _syncContent;
        IReadOnlyList<ChildSync> plans = [];
        var list = new StackPanel { Name = "ScaleSyncList", Spacing = 6 };
        Button? apply = null;
        apply = Ui.AsyncButton("ScaleSyncApplyButton", "Записать в подкарты", async () =>
        {
            await _editor.ApplyChildSyncAsync(plans);
            plans = await _editor.PlanChildSyncAsync(report: false);
            Fill();
        }, "accent", "compact");
        void Fill()
        {
            list.Children.Clear();
            if (plans.Count == 0)
                list.Children.Add(new TextBlock { Text = "Нажмите «Проверить подкарты». Подкарты создаются в «Мир → Атлас» из области карты.", TextWrapping = TextWrapping.Wrap }.Classed("faint"));
            foreach (var plan in plans)
            {
                var detail = plan.Plan.KeptByHand.Count == 0 ? "" : " · вручную: " + string.Join(", ", plan.Plan.KeptByHand.Take(4));
                list.Children.Add(new Border
                {
                    Padding = new Thickness(8),
                    Child = new StackPanel { Spacing = 2, Children =
                    {
                        new TextBlock { Text = plan.Title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = plan.Plan.Summary() + detail, TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption"),
                    } },
                }.Classed("group"));
            }
            if (apply is not null) apply.IsEnabled = plans.Any(p => p.Plan.Changes > 0);
        }
        var check = Ui.AsyncButton("ScaleSyncCheckButton", "Проверить подкарты", async () => { plans = await _editor.PlanChildSyncAsync(); Fill(); }, "compact");
        var hexes = Ui.AsyncButton("HexesFromArtButton", "Гексы из рисунка родителя", async () => await _editor.HexesFromParentArtAsync(), "compact");
        ToolTip.SetTip(hexes, "Для гекс-подкарты: каждый гекс берёт местность, нарисованную под ним в родительской карте. Одна отмена.");
        Fill();
        _syncContent = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 10, 16, 18), Spacing = 10,
                Children =
                {
                    Ui.Hint("Реки и дороги этой карты переносятся в её подкарты: обрезаются по краю области, масштабируются и помечаются как пришедшие сверху. Изменили родителя — проверьте и запишите снова; правленное в подкарте вручную не трогается."),
                    new Ui.ChoiceGrid { MaxColumns = 2, Children = { check, apply } },
                    list,
                    Ui.Subgroup("Гекс-подкарта", hexes),
                },
            },
        };
        return _syncContent;
    }
}
