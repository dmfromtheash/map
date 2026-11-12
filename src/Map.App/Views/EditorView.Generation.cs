using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Map.App.Controls;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.App.Views;

/// <summary>
/// The generation panels as tiles with icons (owner, 2026-11-12: variant 7 «Плитки-значки» of
/// https://claude.ai/artifact/2JRg4noKtdLyhPGW4N5Xki): steps and protection are tiles - an icon of what the item does over
/// a short name; the calculation is one ring with what it does now; «Применить», «Отмена», «Пересчитать» are three tiles.
/// Every tile opens its explanation card.
/// </summary>
public sealed partial class EditorView
{
    /// <summary>A tile: the icon over a short name and, when given, a faint second line («шаг 3»).</summary>
    private static Button GenerationTile(string name, string text, string icon, Action action, string? caption = null, string? accessible = null)
    {
        var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2), Child = MapIcons.Grid24(icon, 22) });
        content.Children.Add(new TextBlock { Text = text, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (caption is not null)
            content.Children.Add(new TextBlock { Text = caption, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center }.Classed("faint"));
        var button = new Button
        {
            Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(2, 7, 2, 5), MinHeight = 50,
        }.Named(name).Classed("compact");
        AutomationProperties.SetName(button, accessible ?? (caption is null ? text : $"{text}, {caption}"));
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A tile that stays lit while its mode is on (the protection brush, «Снимать», «Только выделенные»).</summary>
    private static Button GenerationToggleTile(string name, string text, string icon, Action action, string accessible)
    {
        var tile = GenerationTile(name, text, icon, action, accessible: accessible);
        tile.Classes.Add("segment");
        return tile;
    }

    /// <summary>«Зерно», its box filling the row, the dice and what else the row holds on the right.</summary>
    private static Control SeedRow(TextBox seed, params Control[] right)
    {
        var row = new DockPanel { LastChildFill = true };
        row.Children.Add(new TextBlock { Text = "Зерно", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }.Classed("faint").Docked(Dock.Left));
        for (var k = right.Length - 1; k >= 0; k--)
        {
            right[k].Margin = new Thickness(6, 0, 0, 0);
            right[k].HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(right[k].Docked(Dock.Right));
        }
        seed.MinWidth = 56; seed.Width = double.NaN;
        row.Children.Add(seed);
        return row;
    }

    private static string HexStepShort(HexGenerationStep step) => step switch
    {
        HexGenerationStep.Land => "Суша", HexGenerationStep.Relief => "Рельеф", HexGenerationStep.Climate => "Климат",
        HexGenerationStep.Rivers => "Реки", HexGenerationStep.Settlements => "Поселения", HexGenerationStep.Roads => "Дороги", _ => "Государства",
    };

    private static string HexStepIcon(HexGenerationStep step) => step switch
    {
        HexGenerationStep.Land => MapIcons.GenLand, HexGenerationStep.Relief => MapIcons.GenRelief, HexGenerationStep.Climate => MapIcons.GenClimate,
        HexGenerationStep.Rivers => MapIcons.GenRivers, HexGenerationStep.Settlements => MapIcons.GenSettlements, HexGenerationStep.Roads => MapIcons.GenRoads,
        _ => MapIcons.GenStates,
    };

    private static (PanelDemoKind Kind, string Text) HexStepCard(HexGenerationStep step) => step switch
    {
        HexGenerationStep.Land => (PanelDemoKind.GenHexLand, "Суша там, где поле высот выше доли суши; у берега мели, в низинах озёра."),
        HexGenerationStep.Relief => (PanelDemoKind.GenHexRelief, "На возвышенностях суши — холмы, горы и пики цепями хребтов."),
        HexGenerationStep.Climate => (PanelDemoKind.GenHexClimate, "Биом по широте, высоте и влажности: снег, тайга, лес, джунгли, болото, пустыня."),
        HexGenerationStep.Rivers => (PanelDemoKind.GenHexRivers, "Сток собирается вниз по склонам; крупные потоки — реки до моря или озера."),
        HexGenerationStep.Settlements => (PanelDemoKind.GenHexSettlements, "Города, деревни, замки, порты и рудники на пригодной земле; поля вокруг городов."),
        HexGenerationStep.Roads => (PanelDemoKind.GenHexRoads, "Дороги соединяют поселения самым лёгким путём, через реки — мосты."),
        _ => (PanelDemoKind.GenHexStates, "Каждая столица забирает землю, до которой путь от неё короче; государство — область."),
    };

    /// <summary>
    /// Opening a generator's section starts its cards' calculation in the background: the continent's ten generator runs
    /// take seconds, and the first card should not wait for them.
    /// </summary>
    private T WarmGenerationCards<T>(T category, PanelDemoKind kind) where T : Expander
    {
        var warmed = false;
        category.PropertyChanged += (_, e) =>
        {
            if (warmed || e.Property != Expander.IsExpandedProperty || !category.IsExpanded) return;
            warmed = true;
            if (_editor.GenerationDemo(kind, out string _) is { } work) UiTasks.Forget(this, () => System.Threading.Tasks.Task.Run(() => { work(); }));
        };
        return category;
    }

    private static Ui.ChoiceGrid TileGrid(int columns, double minimum, params Control[] tiles)
    {
        var grid = new Ui.ChoiceGrid { MaxColumns = columns, MinimumItemWidth = minimum, Gap = 5 };
        foreach (var tile in tiles) grid.Children.Add(tile);
        return grid;
    }

    /// <summary>Tiles in a fixed number of equal columns: short names that may wrap to two lines.</summary>
    private static Avalonia.Controls.Primitives.UniformGrid FixedTiles(int columns, params Control[] tiles)
    {
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = columns, ColumnSpacing = 5, RowSpacing = 5 };
        foreach (var tile in tiles) grid.Children.Add(tile);
        return grid;
    }

    /// <summary>The main tile of a row («Мир целиком», «Применить»): lit like a chosen segment, in the panel's own font.</summary>
    private static Button Lit(Button tile)
    {
        tile.Classes.Add("segment"); Ui.SetClass(tile, "active", true);
        return tile;
    }

    /// <summary>The protection tiles and the calculation's state of the hex world (<paramref name="hex"/>) or of an area.</summary>
    private Control BuildGenerationControls(bool hex = true)
    {
        var panel = new StackPanel { Name = hex ? "GenerationHexControls" : "GenerationAreaControls", Spacing = 8, Tag = "Reveal:HexTabWorld" };
        var count = new TextBlock { Name = "GenerationProtectedCount" }.Classed("faint", "role-caption");
        Button brush = null!, erase = null!, selection = null!;
        void SyncTiles()
        {
            Ui.SetClass(brush, "active", hex ? _editor.HexMode == HexToolMode.Protect : _editor.AreaProtectionBrushActive);
            Ui.SetClass(erase, "active", _editor.GenerationProtectionErase);
            if (hex) Ui.SetClass(selection, "active", _editor.GenerationSelectionOnly);
            count.Text = hex ? $"Защита · {_editor.HexProtectedCount} гекс." : "Защита";
        }
        brush = Explain(GenerationToggleTile("GenerationProtectBrushButton", "Кисть", MapIcons.GenProtectBrush, () =>
        {
            if (hex) _editor.HexMode = _editor.HexMode == HexToolMode.Protect ? HexToolMode.Select : HexToolMode.Protect;
            else _editor.SetAreaProtectionBrush(!_editor.AreaProtectionBrushActive);
            SyncTiles();
        }, "Кисть «Защитить»"), hex ? PanelDemoKind.GenHexProtectBrush : PanelDemoKind.GenAreaProtectBrush, "Кисть «Защитить»",
            hex ? "Закрашенные гексы и пути через них генератор не меняет при следующих расчётах."
                : "Закрашенные кистью ассеты и место под ними пересчёт области не трогает.");
        erase = Explain(GenerationToggleTile("GenerationProtectionEraseToggle", "Снимать", MapIcons.GenProtectErase,
            () => { _editor.GenerationProtectionErase = !_editor.GenerationProtectionErase; SyncTiles(); }, "Кисть снимает защиту"),
            hex ? PanelDemoKind.GenHexProtectErase : PanelDemoKind.GenAreaProtectErase, "Кисть снимает защиту",
            hex ? "С этой отметкой кисть убирает защиту с гексов, по которым проходит."
                : "С этой отметкой кисть убирает защиту с ассетов, по которым проходит.");
        Control tiles;
        if (hex)
        {
            selection = Explain(GenerationToggleTile("GenerationSelectionOnlyToggle", "Только выдел.", MapIcons.GenSelectionOnly,
                () => { _editor.GenerationSelectionOnly = !_editor.GenerationSelectionOnly; SyncTiles(); }, "Только выделенные гексы"),
                PanelDemoKind.GenHexSelectionOnly, "Только выделенные гексы", "Шаг меняет только выделенные гексы; остальная карта остаётся как была.");
            tiles = FixedTiles(5, brush, erase,
                Explain(GenerationTile("GenerationProtectSelectionButton", "+ выдел.", MapIcons.GenProtectSelection, () => _editor.ProtectHexSelection(true),
                    accessible: "Защитить выделение"), PanelDemoKind.GenHexProtectSelection, "Защитить выделение",
                    "Выделенные гексы становятся защищёнными: расчёт их не меняет."),
                Explain(GenerationTile("GenerationUnprotectSelectionButton", "− выдел.", MapIcons.GenUnprotectSelection, () => _editor.ProtectHexSelection(false),
                    accessible: "Снять защиту выделения"), PanelDemoKind.GenHexUnprotectSelection, "Снять защиту выделения",
                    "Защита снимается только с выделенных гексов; остальная остаётся."),
                selection);
        }
        else
        {
            panel.Tag = "Reveal:EditableAreaPicker";
            tiles = FixedTiles(3, brush, erase,
                Explain(GenerationTile("GenerationProtectBrushStopButton", "Закончить", MapIcons.GenProtectStop, () => { _editor.SetAreaProtectionBrush(false); SyncTiles(); },
                    accessible: "Закончить защиту"), PanelDemoKind.GenAreaProtectStop, "Закончить защиту",
                    "Кисть защиты выключается; щелчок по карте снова выделяет и ставит, как раньше."));
        }
        _editor.PropertyChanged += (_, _) => SyncTiles();
        SyncTiles();
        panel.Children.Add(count);
        panel.Children.Add(tiles);
        if (!hex)
        {
            var start = Ui.CommandButton("GenerationAreaStartButton", "Рассчитать выбранную область", MapIcons.GenArea, _editor.StartAreaGeneration, "accent");
            start.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.Children.Add(Explain(start, PanelDemoKind.GenAreaStart, "Рассчитать выбранную область",
                "Область заполняется заново по своей плотности, рисунку и ограничениям; сначала предпросмотр."));
        }
        panel.Children.Add(BuildGenerationState("Generation"));
        return panel;
    }

    /// <summary>
    /// The calculation's ring - how far it is, what it makes, whether the map changed meanwhile - and the three action tiles.
    /// </summary>
    private Control BuildGenerationState(string prefix)
    {
        var ring = new GenerationRing { Name = prefix + "ProgressRing", Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Name = prefix + "Status", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap }.Classed("faint", "role-caption");
        var card = new Border
        {
            Name = prefix + "StateCard", Padding = new Thickness(10), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            Child = new DockPanel
            {
                Children =
                {
                    ring.Docked(Dock.Left),
                    new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } },
                },
            },
        };
        var apply = Explain(GenerationTile(prefix + "ApplyButton", "Применить", MapIcons.GenApply, () => _editor.ApplyGeneration()),
            PanelDemoKind.GenApply, "Применить", "Предпросмотр становится картой. Одна отмена (Ctrl+Z) возвращает карту до применения.");
        Lit(apply);
        var cancel = Explain(GenerationTile(prefix + "CancelButton", "Отмена", MapIcons.GenCancel, _editor.CancelGeneration, accessible: "Отмена расчёта / превью"),
            PanelDemoKind.GenCancel, "Отмена расчёта / превью", "Расчёт останавливается, предпросмотр исчезает; карта не меняется.");
        var recalc = Explain(GenerationTile(prefix + "RecalculateButton", "Пересчитать", MapIcons.GenRecalculate, _editor.RecalculateGeneration),
            PanelDemoKind.GenRecalculate, "Пересчитать", "Карту правили во время расчёта: расчёт заново по карте как она есть сейчас.");
        void Sync()
        {
            var busy = _editor.GenerationBusy; var preview = _editor.GenerationHasPreview; var conflict = _editor.GenerationConflict;
            var what = _editor.GenerationWhat;
            ring.Progress = busy ? _editor.GenerationProgress : preview && !conflict ? 1 : 0;
            ring.Warn = conflict;
            title.Text = conflict ? "Карта изменилась" : busy ? "Идёт расчёт" : preview ? "Готов предпросмотр" : "Расчёта нет";
            detail.Text = conflict ? "Результат не применён."
                : busy ? $"{what} · карту можно править"
                : preview ? $"{what} · карта ещё не изменена" : "Выберите шаг выше.";
            Ui.SetClass(title, "warn", conflict);
            card.Background = Application.Current?.FindResource(conflict ? "MapWarnDimBrush" : "MapControlBrush") as IBrush;
            card.BorderBrush = Application.Current?.FindResource(conflict ? "MapWarnBorderBrush" : "MapBorderBrush") as IBrush;
            apply.IsEnabled = preview;
            cancel.IsEnabled = busy || preview;
            Ui.SetClass(recalc, "segment", conflict); Ui.SetClass(recalc, "active", conflict);
        }
        _editor.PropertyChanged += (_, _) => Sync();
        // The worker reports progress from its own thread: the panel reads it while the calculation runs.
        var timer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) => { if (_editor.GenerationBusy) Sync(); });
        timer.Start();
        DetachedFromVisualTree += (_, _) => timer.Stop();
        card.AttachedToVisualTree += (_, _) => Sync();
        Sync();
        return new StackPanel { Spacing = 6, Children = { card, FixedTiles(3, apply, cancel, recalc) } };
    }

    /// <summary>The progress ring of the calculation: a gold arc, the percent inside, «!» when the map changed meanwhile.</summary>
    private sealed class GenerationRing : Control
    {
        private double _progress;
        private bool _warn;
        private static readonly IPen Track = new Pen(new SolidColorBrush(Color.Parse("#2A2019")), 5);
        private static readonly IPen Arc = new Pen(new SolidColorBrush(Color.Parse("#D4AE62")), 5, lineCap: PenLineCap.Round);
        private static readonly IPen WarnArc = new Pen(new SolidColorBrush(Color.Parse("#E3B565")), 5);
        private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E9DCC4"));
        private static readonly IBrush WarnInk = new SolidColorBrush(Color.Parse("#E3B565"));

        public double Progress { get => _progress; set { if (Math.Abs(_progress - value) > .001) { _progress = Math.Clamp(value, 0, 1); InvalidateVisual(); } } }
        public bool Warn { get => _warn; set { if (_warn != value) { _warn = value; InvalidateVisual(); } } }

        public override void Render(DrawingContext context)
        {
            var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
            var r = Math.Min(Bounds.Width, Bounds.Height) / 2 - 3;
            context.DrawEllipse(null, _warn ? WarnArc : Track, c, r, r);
            if (!_warn && _progress > 0)
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var sweep = Math.Min(_progress, .9999) * Math.PI * 2;
                    g.BeginFigure(new Point(c.X, c.Y - r), false);
                    g.ArcTo(new Point(c.X + r * Math.Sin(sweep), c.Y - r * Math.Cos(sweep)), new Size(r, r), 0, sweep > Math.PI, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, Arc, geometry);
            }
            var text = new FormattedText(_warn ? "!" : $"{_progress * 100:0}%", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), _warn ? 16 : 11, _warn ? WarnInk : Ink);
            context.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
        }
    }
}
