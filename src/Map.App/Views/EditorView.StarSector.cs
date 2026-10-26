using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;

namespace Map.App.Views;

/// <summary>
/// R33: «Звёздный сектор» - dress a hex map as space, edit the system of a hex by its number
/// («0101»), list the systems, and load or save the sector data (T5 table, older «.sec»).
/// </summary>
public sealed partial class EditorView
{
    private Window? _sectorWindow;
    private Control? _sectorContent;
    private readonly List<Action> _sectorSyncs = [];

    private void ShowStarSector()
    {
        _sectorWindow = ShowAuxiliaryWindow(_sectorWindow, "Звёздный сектор", BuildSectorContent(), 660, 820, "StarSectorWindow");
        SyncSector();
    }

    private Control BuildSectorContent()
    {
        if (_sectorContent is not null) return _sectorContent;
        var make = Ui.TextButton("SectorMakeButton", "Оформить как сектор", () => { _editor.MakeStarSector(); SyncSector(); }, "compact");
        ToolTip.SetTip(make, "Тёмное поле космоса, гекс — один парсек. Одна отмена.");
        var number = new TextBox { Name = "SectorHexBox", Width = 70, PlaceholderText = "0101", MaxLength = 4 };
        var name = new TextBox { Name = "SectorNameBox", PlaceholderText = "Название системы" };
        var uwp = new TextBox { Name = "SectorUwpBox", Width = 120, PlaceholderText = "A788899-C", MaxLength = 9 };
        var bases = new TextBox { Name = "SectorBasesBox", Width = 60, PlaceholderText = "базы", MaxLength = 8 };
        var remarks = new TextBox { Name = "SectorRemarksBox", PlaceholderText = "Коды торговли (Ag Ni Ri…)" };
        var zone = new ComboBox { Name = "SectorZoneBox", Width = 140, ItemsSource = new[] { "Зелёная", "Жёлтая", "Красная" }, SelectedIndex = 0 };
        var pbg = new TextBox { Name = "SectorPbgBox", Width = 60, PlaceholderText = "PBG", MaxLength = 3 };
        var allegiance = new TextBox { Name = "SectorAllegianceBox", Width = 70, PlaceholderText = "союз", MaxLength = 8 };
        var stars = new TextBox { Name = "SectorStarsBox", PlaceholderText = "Звёзды (G2 V M0 V)" };
        var notes = new TextBox { Name = "SectorNotesBox", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, PlaceholderText = "Заметки ведущего" };
        void Load()
        {
            var system = _editor.SystemAt(number.Text ?? "");
            name.Text = system?.Name ?? ""; uwp.Text = system?.Uwp ?? ""; bases.Text = system?.Bases ?? ""; remarks.Text = system?.Remarks ?? "";
            zone.SelectedIndex = (int)(system?.Zone ?? TravelZone.Green); pbg.Text = system?.Pbg ?? ""; allegiance.Text = system?.Allegiance ?? "";
            stars.Text = system?.Stars ?? ""; notes.Text = system?.Notes ?? "";
        }
        var open = Ui.TextButton("SectorOpenButton", "Открыть гекс", Load, "compact");
        var save = Ui.TextButton("SectorSaveButton", "Сохранить систему", () =>
        {
            _editor.SetSystem(number.Text ?? "", new StarSystem
            {
                Name = (name.Text ?? "").Trim(), Uwp = (uwp.Text ?? "").Trim().ToUpperInvariant(), Bases = (bases.Text ?? "").Trim(),
                Remarks = (remarks.Text ?? "").Trim(), Zone = (TravelZone)Math.Max(0, zone.SelectedIndex), Pbg = (pbg.Text ?? "").Trim(),
                Allegiance = (allegiance.Text ?? "").Trim(), Stars = (stars.Text ?? "").Trim(), Notes = notes.Text ?? "",
            });
            SyncSector();
        }, "accent", "compact");
        var remove = Ui.TextButton("SectorRemoveButton", "Удалить систему", () => { _editor.SetSystem(number.Text ?? "", null); SyncSector(); }, "destructive", "compact");
        var list = new StackPanel { Name = "SectorSystems", Spacing = 2 };
        var import = Ui.AsyncButton("SectorImportButton", "Загрузить данные…", async () => { await _editor.PickAndImportSectorAsync(); SyncSector(); }, "compact");
        ToolTip.SetTip(import, "Таблица T5 (табуляция, с заголовком Hex…) или старый столбцовый .sec.");
        var export = Ui.AsyncButton("SectorExportButton", "Сохранить данные…", async () => await _editor.PickAndExportSectorAsync(), "compact");
        var state = new TextBlock { Name = "SectorStateText", TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");

        void Sync()
        {
            state.Text = _editor.SectorLayer is null ? "Это не гекс-карта. Сектор делается из гекс-карты (32×40, плоские гексы)." : $"Систем: {_editor.SectorSystems.Count}.";
            list.Children.Clear();
            foreach (var (hex, system) in _editor.SectorSystems.Take(400))
            {
                var captured = hex;
                list.Children.Add(Ui.ListRow("SectorSystem_" + hex, MapIcons.LineTarget, $"{hex} {system.Name}", system.Uwp
                    + (system.Zone == TravelZone.Red ? " · красная" : system.Zone == TravelZone.Amber ? " · жёлтая" : ""), [],
                    () => { number.Text = captured; Load(); }, system.Name));
            }
        }
        _sectorSyncs.Add(Sync);
        _sectorContent = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 10, 16, 18), Spacing = 10,
                Children =
                {
                    Ui.Hint("Гекс-карта как звёздный сектор: системы по номерам гексов (0101 — левый верхний), UWP, базы, коды, зона, PBG, союз, звёзды. Маршруты и союзы — путями и регионами гексов."),
                    make, state,
                    Ui.Subgroup("Система", new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Гекс", VerticalAlignment = VerticalAlignment.Center }, number, open } },
                            name,
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { uwp, bases, pbg, allegiance } },
                            remarks, zone, stars, notes,
                            new Ui.ChoiceGrid { MaxColumns = 2, Children = { save, remove } },
                        },
                    }),
                    Ui.Subgroup("Системы сектора", list),
                    Ui.Subgroup("Данные", new Ui.ChoiceGrid { MaxColumns = 2, Children = { import, export } }),
                    Ui.Hint("Генератор систем по открытым таблицам Cepheus/Traveller — после подтверждения лицензии таблиц."),
                },
            },
        };
        Sync();
        return _sectorContent;
    }

    private void SyncSector()
    {
        foreach (var sync in _sectorSyncs) sync();
    }
}
