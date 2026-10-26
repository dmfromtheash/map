using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Map.App.Controls;

namespace Map.App.Views;

/// <summary>
/// R27: «Туман» in the Notes tool - the fog of war of a painted map. On/off, the brush that
/// reveals or hides what the players have seen, its size, open or hide everything, the veil the
/// master sees, and the players' window.
/// </summary>
public sealed partial class EditorView
{
    private readonly List<Action> _fogSyncs = [];

    private Control BuildFogPage()
    {
        var enabled = Ui.PropertyToggle("FogEnabledToggle", "Туман войны");
        ToolTip.SetTip(enabled, "Игроки видят только открытое: в окне игрока и в экспорте «для игроков».");
        enabled.IsCheckedChanged += (_, _) => { if (!_syncingFog) { _editor.SetFogEnabled(enabled.IsChecked == true); SyncFog(); } };
        var brush = Ui.PropertyToggle("FogBrushToggle", "Кисть тумана");
        ToolTip.SetTip(brush, "Проведите по карте, чтобы открыть или скрыть. Отменяется Ctrl+Z одним шагом на мазок.");
        brush.IsCheckedChanged += (_, _) => { if (!_syncingFog) { _editor.FogBrush = brush.IsChecked == true; SyncFog(); } };
        var (modes, modeButtons) = Ui.Segmented("FogMode", [("Reveal", "Открыть", null), ("Hide", "Скрыть", null)], key =>
        {
            _editor.FogReveal = key == "Reveal";
            SyncFog();
        });
        var radius = new Slider { Name = "FogRadiusSlider", Minimum = 16, Maximum = 1000, SmallChange = 8, LargeChange = 50 };
        var radiusText = new TextBlock { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Width = 56 };
        radius.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _syncingFog) return;
            _editor.FogBrushRadius = radius.Value;
            radiusText.Text = $"{_editor.FogBrushRadius:0} px";
        };
        var revealAll = Ui.TextButton("FogRevealAllButton", "Открыть всё", () => { _editor.RevealAllFog(); SyncFog(); }, "compact");
        var hideAll = Ui.TextButton("FogHideAllButton", "Скрыть всё", () => { _editor.HideAllFog(); SyncFog(); }, "compact");
        var export = Ui.PropertyToggle("FogExportPlayersToggle", "Экспорт картинки — как видят игроки");
        ToolTip.SetTip(export, "Файл → Экспорт: туман непрозрачен, подписи и заметки под туманом не попадают в файл.");
        export.IsCheckedChanged += (_, _) => { if (!_syncingFog) _editor.ExportFogPlayers = export.IsChecked == true; };
        var veil = Ui.PropertyToggle("FogVeilToggle", "Показывать туман мне");
        ToolTip.SetTip(veil, "Ведущий видит туман полупрозрачной пеленой; игрокам он непрозрачен.");
        veil.IsCheckedChanged += (_, _) => { if (!_syncingFog) _editor.FogVeilShown = veil.IsChecked == true; };
        var player = Ui.CommandButton("FogPlayerViewButton", "Окно игрока…", MapIcons.HexPlayer, _editor.OpenPlayerView, "accent");
        ToolTip.SetTip(player, "Отдельное окно с картой, как её видят игроки: туман, только открытые подписи и заметки «Видно игрокам». Можно вынести на второй экран.");
        var state = new TextBlock { Name = "FogStateText", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        var tools = new StackPanel
        {
            Name = "FogTools", Spacing = 6,
            Children =
            {
                brush, modes,
                new DockPanel { Children = { radiusText.Docked(Dock.Right), radius } },
                new Ui.ChoiceGrid { MaxColumns = 2, Children = { revealAll, hideAll } },
                veil, export,
            },
        };

        void Sync()
        {
            _syncingFog = true;
            try
            {
                enabled.IsChecked = _editor.Fog.Enabled;
                tools.IsVisible = _editor.Fog.Enabled;
                brush.IsChecked = _editor.FogBrush;
                foreach (var button in modeButtons)
                    Ui.SetClass(button, "active", button.Name == (_editor.FogReveal ? "FogModeReveal" : "FogModeHide"));
                radius.Value = _editor.FogBrushRadius;
                radiusText.Text = $"{_editor.FogBrushRadius:0} px";
                veil.IsChecked = _editor.FogVeilShown;
                export.IsChecked = _editor.ExportFogPlayers;
                state.Text = _editor.Fog.Enabled
                    ? $"Игроки видят {_editor.FogRevealedShare:P0} карты."
                    : "Туман выключен: игроки видят всю карту.";
            }
            finally { _syncingFog = false; }
        }
        _fogSyncs.Add(Sync);
        Sync();
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Hint("Что уже видели игроки. Откройте кистью пройденное — остальное скрыто туманом в окне игрока и в экспорте для игроков."),
                enabled, state, tools, player,
            },
        };
    }

    private bool _syncingFog;

    private void SyncFog()
    {
        foreach (var sync in _fogSyncs) sync();
    }
}
