using System.Collections.Immutable;
using System.Text.Json;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Rendering;
using SkiaSharp;

namespace Map.Performance;

/// <summary>
/// R20 (`--profile generation-cards-sheet`): the animations of the explanation cards proposed for the generation panels
/// (owner, 2026-11-12: «анимированные показы того, что делают функции в них, как в других наших подобных панелях»), made
/// before any code of the cards exists, for the page of panel variants. Each card is ten frames of the real thing: the
/// hex world generator (<see cref="ControlledHexGeneration"/>), the continent generator (<see cref="ContinentGenerator"/>)
/// and the area generator (<see cref="ControlledAreaGeneration"/> with the owner's mountains), drawn by the document
/// renderer; the pointer, protection, selection and one note are laid over them as the asset panel's cards lay theirs.
/// Writes <c>&lt;output&gt;.card-&lt;key&gt;.webp</c> (the ten frames side by side) and <c>&lt;output&gt;.cards.json</c>.
/// </summary>
internal static class GenerationCardsSheet
{
    private const int Frames = 10, PictureWidth = 600, PictureHeight = 260;

    private sealed record Card(string Panel, string Key, string Title, string Text, string File);

    private sealed class Marks
    {
        public List<HexCoord> Protected { get; } = [];
        public List<HexCoord> Selected { get; } = [];
        public (double X, double Y)? Cursor { get; set; }
        public bool Click { get; set; }
        public double BrushRadius { get; set; }
        public string? Note { get; set; }
        public bool Preview { get; set; }
        public double Progress { get; set; } = -1;
        /// <summary>Boxes in world units: protected objects of an area.</summary>
        public List<WorldBounds> Frames { get; } = [];
        /// <summary>The next state laid over the frame with this opacity (a step whose change is pixels, not hexes).</summary>
        public MapDocument? FadeTo { get; set; }
        public double Fade { get; set; }
        /// <summary>The outline of a placement area, world units.</summary>
        public IReadOnlyList<(double X, double Y)>? Outline { get; set; }
    }

    private sealed class Images(Dictionary<string, SKImage> images) : IStampImageSource
    {
        public SKImage? TryGetImage(string sha256) => images.GetValueOrDefault(sha256);
    }

    public static AreaBeautySheetReport Run(PerformanceOptions options)
    {
        var report = new AreaBeautySheetReport();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        var cards = new List<Card>();
        HexCards(options, cards);
        ContinentCards(options, cards);
        AreaCards(options, cards);
        File.WriteAllText(Path.ChangeExtension(options.OutputPath, ".cards.json"),
            JsonSerializer.Serialize(cards, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        report.Images.AddRange(cards.Select(c => c.File));
        report.Notes["cards"] = cards.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        report.GatePassed = cards.Count > 0 && cards.All(c => File.Exists(c.File));
        return report;
    }

    // ------------------------------------------------------------------ hex world

    private static void HexCards(PerformanceOptions options, List<Card> cards)
    {
        // 40 × 15 flat hexes of 40: about the card's own proportions (300 × 130), and land enough for two states.
        var document = HexMapFactory.Create("Генератор мира", DateTimeOffset.UnixEpoch, Map.Presentation.Services.MapCategories.Hex.Id, HexOrientation.Flat, 40, 40, 15,
            CurrentLook.PageColor);
        var empty = document.Layers.OfType<HexLayer>().Single();
        var settings = new HexGeneratorSettings { Seed = 41, RiverModel = HexGeneratorSettings.FlowRivers, LandShare = .55, SettlementsPerThousand = 50, RiversPerThousand = 40 };
        // The card's board is small: the first seed whose world shows every step (rivers, towns, two states and more).
        foreach (var model in new[] { HexGeneratorSettings.FlowRivers, HexGeneratorSettings.DownhillRivers })
        {
            var found = Enumerable.Range(1, 1500).Select(seed => settings with { Seed = seed, RiverModel = model, Relief = .3 }).FirstOrDefault(s =>
            {
                var w = RunAll(empty, s);
                return w.Paths.Count(p => p.Kind == HexPathKind.River) >= 2 && w.Board.Cells.Count(c => c.Value.Feature is not null) >= 5
                    && w.Regions.Count >= 2 && w.Paths.Count(p => p.Kind == HexPathKind.Road) >= 2;
            });
            if (found is not null) { settings = found; break; }
        }
        Console.WriteLine($"  hex seed {settings.Seed}, rivers model {settings.RiverModel}");
        HexLayer Step(HexLayer layer, HexGenerationStep step, HexGeneratorSettings s, ImmutableHashSet<HexCoord>? only = null) =>
            ControlledHexGeneration.Run(layer, new HexGenerationRecipe { Settings = s, Steps = [step], Selection = only ?? [] });
        var steps = Enum.GetValues<HexGenerationStep>();
        var states = new List<HexLayer> { empty };
        foreach (var step in steps)
        {
            states.Add(Step(states[^1], step, settings));
            Console.WriteLine($"  hex {step}: cells {states[^1].Board.CellCount}, paths {string.Join(", ", states[^1].Paths.GroupBy(p => p.Kind).Select(g => $"{g.Key} {g.Count()}"))}, " +
                $"features {states[^1].Board.Cells.Count(c => c.Value.Feature is not null)}, regions {states[^1].Regions.Count}");
        }
        var world = states[^1];
        var view = new WorldBounds(0, 0, document.Canvas.Width, document.Canvas.Height);
        MapDocument With(HexLayer layer) => document with { Layers = [.. document.Layers.Select(l => l is HexLayer ? layer : l)] };
        var layout = empty.Board.Layout;

        void Save(string key, string title, string text, Func<int, (HexLayer Layer, Marks Marks)> frame) =>
            cards.Add(SaveCard(options, "hex", key, title, text, i => { var (l, m) = frame(i); return (With(l), view, m, layout); }));

        // 1-7: each step on the world as the steps before it left it, its changes appearing hex by hex.
        for (var k = 0; k < steps.Length; k++)
        {
            var before = states[k]; var after = states[k + 1];
            var (title, text) = steps[k] switch
            {
                HexGenerationStep.Land => ("Суша и моря", "Суша там, где поле высот выше доли суши; у берега мели, в низинах озёра."),
                HexGenerationStep.Relief => ("Холмы и горы", "На возвышенностях суши — холмы, горы и пики цепями хребтов."),
                HexGenerationStep.Climate => ("Климат и леса", "Биом по широте, высоте и влажности: снег, тайга, лес, джунгли, болото, пустыня."),
                HexGenerationStep.Rivers => ("Реки", "Сток собирается вниз по склонам; крупные потоки — реки до моря или озера."),
                HexGenerationStep.Settlements => ("Поселения", "Города, деревни, замки, порты и рудники на пригодной земле; поля вокруг городов."),
                HexGenerationStep.Roads => ("Дороги", "Дороги соединяют поселения самым лёгким путём, через реки — мосты."),
                _ => ("Государства", "Каждая столица забирает землю, до которой путь от неё короче; государство — область."),
            };
            Save("step-" + steps[k], $"{k + 1}. {title}", text, i => (Between(before, after, Grow(i)), new Marks { Note = i == 0 ? "до шага" : i >= 8 ? "шаг готов" : null }));
        }

        // «Создать мир целиком»: the seven steps one after another.
        Save("all", "Создать мир целиком", "Семь шагов по порядку с одним зерном: один предпросмотр, «Применить» — одна отмена.", i =>
        {
            var k = Math.Min(steps.Length, i * steps.Length / 8);
            return (states[k], new Marks { Note = k == 0 ? "пустая доска" : $"{k}. {EditorName(steps[k - 1])}" });
        });

        // «Перебросить шаг»: the last step again with the next seed; the steps before stay.
        var reliefOther = Step(states[1], HexGenerationStep.Relief, settings with { Seed = settings.Seed + 1 });
        var reliefThird = Step(states[1], HexGenerationStep.Relief, settings with { Seed = settings.Seed + 2 });
        Save("reroll", "Перебросить шаг", "Последний шаг заново со следующим зерном; шаги до него не меняются.", i =>
            i < 4 ? (states[2], new Marks { Note = $"зерно {settings.Seed}" }) : i < 7 ? (reliefOther, new Marks { Note = $"зерно {settings.Seed + 1}" })
                : (reliefThird, new Marks { Note = $"зерно {settings.Seed + 2}" }));

        // «Случайное зерно»: the same settings, another world.
        var worlds = new[] { world, RunAll(empty, settings with { Seed = 7 }), RunAll(empty, settings with { Seed = 1203 }) };
        Save("seed", "Случайное зерно", "Другое зерно — другой мир при тех же настройках; то же зерно — тот же мир.", i =>
            (worlds[Math.Min(2, i / 4)], new Marks { Note = $"зерно {new[] { settings.Seed, 7, 1203 }[Math.Min(2, i / 4)]}" }));

        // Protection: the brush paints a patch, «Создать мир целиком» with another seed then leaves it as it was.
        var patch = Patch(world, 9, 5, 3);
        var protectedWorld = world with { GenerationProtection = [.. patch] };
        var regenerated = RunControlled(protectedWorld, settings with { Seed = 7 });
        Save("protect-brush", "Кисть «Защитить»", "Закрашенные гексы и пути через них генератор не меняет при следующих расчётах.", i =>
        {
            var m = new Marks();
            var painted = i < 6 ? patch.Take((int)Math.Ceiling(patch.Count * Math.Min(1, (i + 1) / 5.0))).ToList() : patch;
            m.Protected.AddRange(painted);
            if (i < 6) { m.Cursor = layout.Center(painted[^1]); m.Click = true; m.BrushRadius = 40; }
            m.Note = i < 6 ? "защита кистью" : "пересчёт: защищённое осталось";
            return (i < 6 ? protectedWorld : regenerated, m);
        });
        Save("protect-erase", "Кисть снимает защиту", "С этой отметкой кисть убирает защиту с гексов, по которым проходит.", i =>
        {
            var m = new Marks();
            var left = patch.Skip((int)Math.Round(patch.Count * Math.Clamp((i - 1) / 7.0, 0, 1))).ToList();
            m.Protected.AddRange(left);
            if (i is >= 1 and <= 8 && left.Count > 0) { m.Cursor = layout.Center(left[0]); m.Click = true; m.BrushRadius = 40; }
            m.Note = left.Count == 0 ? "защиты нет" : null;
            return (protectedWorld, m);
        });
        var selection = Patch(world, 22, 6, 3);
        Save("protect-selection", "Защитить выделение", "Выделенные гексы становятся защищёнными: расчёт их не меняет.", i =>
        {
            var m = new Marks();
            if (i < 4) m.Selected.AddRange(selection); else m.Protected.AddRange(selection);
            if (i is >= 2 and < 4) m.Note = "щелчок по кнопке"; else if (i >= 4) m.Note = $"защищено {selection.Count} гекс.";
            return (world, m);
        });
        Save("unprotect-selection", "Снять защиту выделения", "Защита снимается только с выделенных гексов; остальная остаётся.", i =>
        {
            var m = new Marks();
            m.Protected.AddRange(patch);
            if (i < 4) { m.Protected.AddRange(selection); m.Selected.AddRange(selection); }
            m.Note = i < 4 ? $"защищено {patch.Count + selection.Count}" : $"защищено {patch.Count}";
            return (world, m);
        });

        // «Только выделенные гексы»: the step changes the selection only.
        var only = Patch(states[1], 16, 6, 4).ToImmutableHashSet();
        var reliefOnly = Step(states[1], HexGenerationStep.Relief, settings, only);
        Save("selection-only", "Только выделенные гексы", "Шаг меняет только выделенные гексы; остальная карта остаётся как была.", i =>
        {
            var m = new Marks(); m.Selected.AddRange(only);
            m.Note = i == 0 ? "выделено" : i >= 8 ? "горы только в выделенном" : null;
            return (Between(states[1], reliefOnly, Grow(i)), m);
        });

        // The calculation: preview, apply, cancel, recalculate.
        var before5 = states[4]; var after5 = states[5];
        Save("apply", "Применить", "Предпросмотр становится картой. Одна отмена (Ctrl+Z) возвращает карту до применения.", i =>
        {
            var m = new Marks { Preview = i < 6, Note = i < 6 ? "предпросмотр: карта не изменена" : "применено · Ctrl+Z вернёт" };
            if (i is >= 4 and < 6) { m.Cursor = (PictureWidthWorld(document) * .9, document.Canvas.Height * .9); m.Click = i == 5; }
            return (after5, m);
        });
        Save("cancel", "Отмена расчёта / превью", "Расчёт останавливается, предпросмотр исчезает; карта не меняется.", i =>
        {
            var busy = i < 4;
            var m = new Marks { Preview = i < 6, Progress = busy ? i / 4.0 * .6 : -1, Note = busy ? "расчёт в фоне" : i < 6 ? "предпросмотр" : "отменено · карта как была" };
            return (busy ? Between(before5, after5, i / 6.0) : i < 6 ? after5 : before5, m);
        });
        Save("recalculate", "Пересчитать", "Карту правили во время расчёта: расчёт заново по карте как она есть сейчас.", i =>
        {
            var m = new Marks();
            var edited = before5 with { GenerationProtection = [.. patch] };
            if (i < 3) { m.Progress = i / 3.0 * .5; m.Note = "расчёт в фоне"; m.Protected.AddRange(patch.Take((i + 1) * patch.Count / 3)); m.Cursor = layout.Center(patch[Math.Min(patch.Count - 1, (i + 1) * patch.Count / 3 - 1)]); m.Click = true; m.BrushRadius = 40; return (before5, m); }
            if (i < 5) { m.Note = "карта изменилась — не применено"; m.Protected.AddRange(patch); return (before5, m); }
            m.Protected.AddRange(patch); m.Preview = true; m.Progress = i < 8 ? (i - 5) / 3.0 : -1;
            m.Note = i < 8 ? "пересчёт по текущей карте" : "новый предпросмотр";
            var recalculated = ControlledHexGeneration.Run(edited, new HexGenerationRecipe { Settings = settings, Steps = [HexGenerationStep.Settlements] });
            return (i < 8 ? Between(before5, recalculated, (i - 5) / 3.0) : recalculated, m);
        });

        static double PictureWidthWorld(MapDocument d) => d.Canvas.Width;
    }

    // ------------------------------------------------------------------ continent

    private static void ContinentCards(PerformanceOptions options, List<Card> cards)
    {
        const int width = 1600, height = 1000;
        var land = WaterVariantsSheet.Land("Материк", 0, [(800, 500, 640, 290, .12, 5), (330, 330, 150, 90, .14, 4)], [(1010, 700, 120, 70, .1, 3)]);
        var start = new MapDocument(DocumentId.New(), "Генератор материка", DateTimeOffset.UnixEpoch,
            new CanvasDefinition(width, height, "px", CurrentLook.PageColor), [CurrentLook.Page(), land]);
        var settings = new ContinentSettings { Seed = 23, RiversPerMillion = 10, TownsPerMillion = 10 };
        var steps = Enum.GetValues<ContinentStep>();
        var states = new List<MapDocument> { start };
        foreach (var step in steps) states.Add(ContinentGenerator.Run(states[^1], land.Id, step, settings));
        var view = new WorldBounds(0, 154, width, 846);
        void Save(string key, string title, string text, Func<int, (MapDocument Document, Marks Marks)> frame) =>
            cards.Add(SaveCard(options, "continent", key, title, text, i => { var (d, m) = frame(i); return (d, view, m, null); }));

        for (var k = 0; k < steps.Length; k++)
        {
            var before = states[k]; var after = states[k + 1];
            var (title, text) = steps[k] switch
            {
                ContinentStep.Heights => ("Высоты", "Высота суши растёт от берега вглубь, с шумом и цепями хребтов; нарисованные хребты учитываются."),
                ContinentStep.Erosion => ("Эрозия", "Склоны оседают, вода прорезает долины."),
                ContinentStep.Climate => ("Климат", "Снег, тундра, лес, степь, пустыня и болото по широте, высоте и влажности."),
                ContinentStep.Rivers => ("Реки", "Реки по склонам к морю — обычные реки, их можно править."),
                _ => ("Поселения и дороги", "Поселения на ровной земле у воды, дороги между ними по самому лёгкому пути."),
            };
            var name = Map.Presentation.ViewModels.EditorViewModel.ContinentStepName(steps[k]);
            Save("step-" + steps[k], $"{k + 1}. {name}", text, i => (before,
                new Marks { FadeTo = after, Fade = Grow(i), Note = i == 0 ? "до шага" : i >= 8 ? "шаг готов" : null }));
        }
        Save("all", "Весь материк", "Пять шагов по порядку из нарисованной суши: один предпросмотр, «Применить» — одна отмена.", i =>
        {
            var k = Math.Min(steps.Length, i * steps.Length / 7);
            return (states[k], new Marks { Note = k == 0 ? "нарисованная суша" : $"{k}. {Map.Presentation.ViewModels.EditorViewModel.ContinentStepName(steps[k - 1])}" });
        });
        var other = states[0];
        foreach (var step in steps) other = ContinentGenerator.Run(other, land.Id, step, settings with { Seed = 9001 });
        Save("seed", "Случайное зерно", "Другое зерно — другие горы, реки и поселения на той же суше.", i =>
            i < 5 ? (states[^1], new Marks { Note = "зерно 23" }) : (states[^1], new Marks { FadeTo = other, Fade = Math.Clamp((i - 4) / 3.0, 0, 1), Note = "зерно 9001" }));
    }

    // ------------------------------------------------------------------ areas

    private static void AreaCards(PerformanceOptions options, List<Card> cards)
    {
        const int width = 1000, height = 433;
        var images = new Dictionary<string, SKImage>(StringComparer.OrdinalIgnoreCase);
        var assets = new List<MapAssetReference>();
        var variants = new List<StampVariantSource>();
        for (var n = 1; n <= 6; n++)
        {
            var asset = OwnerMountainFixtures.Embedded(n);
            using var bitmap = SKBitmap.Decode(asset.EmbeddedContent!);
            images[asset.Sha256] = SKImage.FromBitmap(bitmap);
            assets.Add(asset); variants.Add(new StampVariantSource(asset.Sha256, bitmap.Width, bitmap.Height));
        }
        var stamps = new Images(images);
        var layer = new ObjectLayer(LayerId.New(), "Области", true, false, 1, []);
        (double X, double Y)[] corners = [(60, 50), (940, 40), (950, 400), (50, 395)];
        var area = PlacementAreaPlanner.InitializeComposition(new PlacementArea
        {
            LayoutVersion = 4, Density = 45, Seed = 5,
            Nodes = [.. corners.Select(p => new PlacementPathNode(Guid.NewGuid(), p.X, p.Y))],
            Variants = [.. variants], Style = new(variants[0].Sha256), Request = new() { BaseWidth = 70 },
        });
        var start = new MapDocument(DocumentId.New(), "Область", DateTimeOffset.UnixEpoch, new(width, height, "px", CurrentLook.PageColor),
            [CurrentLook.Page("Фон"), CurrentLook.LandEverywhere(width, height), layer], [.. assets]);
        start = new PlacementAreaCommand(layer.Id, area.Id, area, "Fixture").Apply(start);
        var view = new WorldBounds(0, 0, width, height);
        var outline = corners.ToList();
        void Save(string key, string title, string text, Func<int, (MapDocument Document, Marks Marks)> frame) =>
            cards.Add(SaveCard(options, "area", key, title, text, i => { var (d, m) = frame(i); m.Outline ??= outline; return (d, view, m, null); }, stamps));

        PlacementArea Area(MapDocument d) => d.Layers.OfType<ObjectLayer>().Single().PlacementAreas.Single();
        IReadOnlyList<SceneObject> Objects(MapDocument d) => d.Layers.OfType<ObjectLayer>().Single().Objects;
        WorldBounds Box(SceneObject o) => new(o.X, o.Y, o.X + o.Width, o.Y + o.Height);
        MapDocument Generate(MapDocument d, PlacementArea parameters) =>
            ControlledAreaGeneration.Create(d, layer.Id, area.Id, CancellationToken.None, parameters: parameters).Apply(d);

        // The brush passes left to right through the middle; the members it touches become protected.
        var placed = Area(start).Members.Select(m => (m.Slot, Object: Objects(start).Single(o => o.Id == m.ObjectId))).ToList();
        (double X, double Y) Brush(double t) => (180 + 330 * t, 200 + 40 * Math.Sin(t * Math.PI * 2));
        bool Under((double X, double Y) b, SceneObject o) => Math.Abs(o.X + o.Width / 2 - b.X) < 70 && Math.Abs(o.Y + o.Height * .6 - b.Y) < 70;
        List<(PlacementAreaSlot Slot, SceneObject Object)> Touched(double upTo) =>
            [.. placed.Where(p => Enumerable.Range(0, 21).Select(k => Brush(upTo * k / 20)).Any(b => Under(b, p.Object)))];
        var allTouched = Touched(1);
        // As the editor's brush leaves it: the touched members and the space under the stroke (density 0 there).
        const int cell = 8, cols = width / cell + 1, rows = height / cell + 1;
        var values = Enumerable.Repeat(PlacementAreaDensityMask.Neutral, cols * rows).ToArray();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                if (Enumerable.Range(0, 41).Select(k => Brush(k / 40.0)).Any(b => double.Hypot((c + .5) * cell - b.X, (r + .5) * cell - b.Y) <= 70))
                    values[r * cols + c] = 0;
        var protectedArea = Area(start) with { GenerationProtection = [.. allTouched.Select(p => p.Slot)],
            GenerationProtectionMask = PlacementAreaDensityBrush.Finish(0, 0, cell, cols, rows, values) };
        var protectedDoc = start.WithLayer(layer.Id, l => ((ObjectLayer)l) with { PlacementAreas = [protectedArea] });
        // A new seed with protected members fails in Core (found 2026-11-12, «Другая раскладка»); density is a real recalculation.
        var regenerated = Generate(protectedDoc, protectedArea with { Density = 80 });

        Save("protect-brush", "Кисть «Защитить»", "Закрашенные кистью ассеты и место под ними пересчёт области не трогает.", i =>
        {
            var m = new Marks();
            if (i < 6)
            {
                var t = (i + 1) / 6.0; var b = Brush(t);
                m.Frames.AddRange(Touched(t).Select(p => Box(p.Object))); m.Cursor = b; m.BrushRadius = 45; m.Note = "защита кистью";
                return (start, m);
            }
            m.Frames.AddRange(allTouched.Select(p => Box(p.Object)));
            m.Note = "пересчёт: защищённые на месте";
            if (i == 6) { m.FadeTo = regenerated; m.Fade = .5; return (protectedDoc, m); }
            return (regenerated, m);
        });
        Save("protect-erase", "Кисть снимает защиту", "С этой отметкой кисть убирает защиту с ассетов, по которым проходит.", i =>
        {
            var m = new Marks();
            var t = Math.Clamp((i - 1) / 7.0, 0, 1);
            var cleared = Touched(t).Select(p => p.Slot).ToHashSet();
            m.Frames.AddRange(allTouched.Where(p => i == 0 || !cleared.Contains(p.Slot)).Select(p => Box(p.Object)));
            if (i is >= 1 and <= 8) { m.Cursor = Brush(t); m.BrushRadius = 45; }
            m.Note = i >= 9 ? "защиты нет" : null;
            return (protectedDoc, m);
        });
        Save("protect-stop", "Закончить защиту", "Кисть защиты выключается; щелчок по карте снова выделяет и ставит, как раньше.", i =>
        {
            var m = new Marks(); m.Frames.AddRange(allTouched.Select(p => Box(p.Object)));
            if (i < 5) { m.Cursor = Brush(.5 + i * .05); m.BrushRadius = 45; m.Note = "кисть защиты"; }
            else { m.Cursor = (760, 230); m.Click = i is 6 or 7; m.Note = "кисть выключена"; }
            return (protectedDoc, m);
        });
        var denser = Generate(start, Area(start) with { Density = 80 });
        Save("area-start", "Рассчитать выбранную область", "Область заполняется заново по своей плотности, рисунку и ограничениям; сначала предпросмотр.", i =>
        {
            var m = new Marks { Progress = i is >= 1 and < 5 ? i / 5.0 : -1 };
            if (i < 5) { m.Note = i == 0 ? "плотность 45 → 80" : "расчёт в фоне"; return (start, m); }
            m.Preview = true; m.FadeTo = denser; m.Fade = Math.Clamp((i - 4) / 3.0, 0, 1); m.Note = "предпросмотр";
            return (start, m);
        });
        foreach (var image in images.Values) image.Dispose();
    }

    private static string EditorName(HexGenerationStep step) => Map.Presentation.ViewModels.EditorViewModel.HexGenerationStepName(step);

    private static HexLayer RunAll(HexLayer layer, HexGeneratorSettings settings) =>
        ControlledHexGeneration.Run(layer, new HexGenerationRecipe { Settings = settings, Steps = [.. Enum.GetValues<HexGenerationStep>()] });

    private static HexLayer RunControlled(HexLayer layer, HexGeneratorSettings settings) =>
        ControlledHexGeneration.Run(layer, new HexGenerationRecipe { Settings = settings, Steps = [.. Enum.GetValues<HexGenerationStep>()] });

    /// <summary>0 for the first frame, 1 from the ninth: the step's changes appear between them.</summary>
    private static double Grow(int frame) => Math.Clamp((frame - 0.5) / 7.5, 0, 1);

    /// <summary>A round patch of hexes around an offset position, in the order a brush would cover them.</summary>
    private static List<HexCoord> Patch(HexLayer layer, int column, int row, int radius)
    {
        var centre = layer.Board.Layout.FromOffset(new HexOffset(column, row));
        return [.. Enumerable.Range(-radius, radius * 2 + 1).SelectMany(dq => Enumerable.Range(-radius, radius * 2 + 1).Select(dr => new HexCoord(centre.Q + dq, centre.R + dr)))
            .Where(h => HexCoord.Distance(h, centre) <= radius && layer.Board.Contains(h))
            .OrderBy(h => layer.Board.Layout.Center(h).X).ThenBy(h => layer.Board.Layout.Center(h).Y)];
    }

    /// <summary>
    /// The board part way from <paramref name="before"/> to <paramref name="after"/>: changed hexes in a fixed scattered
    /// order (the generator works the whole board at once), new paths drawn from their start.
    /// </summary>
    private static HexLayer Between(HexLayer before, HexLayer after, double t)
    {
        if (t <= 0) return before;
        if (t >= 1) return after;
        var coords = before.Board.Cells.Select(c => c.Key).Union(after.Board.Cells.Select(c => c.Key)).ToList();
        var changes = coords.Where(h => !Equals(before.Board[h], after.Board[h]))
            .OrderBy(h => HexEditing.Noise(h, 977)).ToList();
        var take = (int)Math.Round(changes.Count * t);
        var board = before.Board.With(changes.Take(take).Select(h => new KeyValuePair<HexCoord, HexCell?>(h, after.Board[h])));
        var old = before.Paths.Select(p => p.Id).ToHashSet();
        var paths = after.Paths.Select(p =>
        {
            if (old.Contains(p.Id)) return p;
            var n = p.Style == HexRouteStyle.Edges ? p.Vertices.Length : p.Hexes.Length;
            var keep = Math.Max(0, (int)Math.Round(n * t));
            if (keep < 2) return null;
            return p.Style == HexRouteStyle.Edges ? p with { Vertices = p.Vertices[..keep] } : p with { Hexes = p.Hexes[..keep] };
        }).OfType<HexPath>().ToImmutableList();
        return before with { Board = board, Paths = paths, Regions = t >= .5 ? after.Regions : before.Regions };
    }

    // ------------------------------------------------------------------ drawing

    private static Card SaveCard(PerformanceOptions options, string panel, string key, string title, string text,
        Func<int, (MapDocument Document, WorldBounds View, Marks Marks, HexLayout? Layout)> frame, IStampImageSource? stamps = null)
    {
        using var strip = SKSurface.Create(new SKImageInfo(PictureWidth * Frames, PictureHeight));
        strip.Canvas.Clear(SKColor.Parse("#1A140E"));
        for (var i = 0; i < Frames; i++)
        {
            var (document, view, marks, layout) = frame(i);
            var scale = PictureWidth / (view.Right - view.Left);
            var c = strip.Canvas;
            c.Save();
            c.ClipRect(new SKRect(i * PictureWidth, 0, (i + 1) * PictureWidth, PictureHeight));
            c.Translate(i * PictureWidth, 0);
            c.Save();
            c.Scale((float)scale);
            c.Translate((float)-view.Left, (float)-view.Top);
            SceneRenderer.Render(c, document, new RenderOptions { IsExport = true, VisibleWorld = view, ViewScale = scale, Stamps = stamps });
            if (marks.FadeTo is { } next && marks.Fade > 0)
            {
                using var alpha = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255 * Math.Clamp(marks.Fade, 0, 1))) };
                c.SaveLayer(alpha);
                SceneRenderer.Render(c, next, new RenderOptions { IsExport = true, VisibleWorld = view, ViewScale = scale, Stamps = stamps });
                c.Restore();
            }
            c.Restore();
            DrawMarks(c, marks, view, scale, layout);
            c.Restore();
        }
        var path = Path.ChangeExtension(options.OutputPath, $".card-{panel}-{key}.webp");
        using var image = strip.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Webp, 82);
        using (var file = File.Create(path)) data.SaveTo(file);
        Console.WriteLine($"  card {panel}/{key}: {new FileInfo(path).Length / 1024} KB");
        return new Card(panel, key, title, text, path);
    }

    private static void DrawMarks(SKCanvas c, Marks m, WorldBounds view, double scale, HexLayout? layout)
    {
        SKPoint P(double x, double y) => new((float)((x - view.Left) * scale), (float)((y - view.Top) * scale));
        using var gold = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#E7C982"), Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f,
            PathEffect = SKPathEffect.CreateDash([7, 5], 0) };
        using var goldFill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#40E7C982") };
        using var light = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F6E7C4"), Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f };
        using var lightFill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#30F6E7C4") };
        if (layout is not null)
        {
            SKPath Hex(HexCoord h)
            {
                var p = new SKPath();
                for (var k = 0; k < 6; k++) { var (x, y) = layout.Corner(h, k); if (k == 0) p.MoveTo(P(x, y)); else p.LineTo(P(x, y)); }
                p.Close(); return p;
            }
            foreach (var h in m.Selected) { using var p = Hex(h); c.DrawPath(p, lightFill); c.DrawPath(p, light); }
            foreach (var h in m.Protected)
            {
                using var p = Hex(h); c.DrawPath(p, goldFill);
                // A small lock-like stroke: protected hexes read as marked, not as painted ground.
                c.DrawPath(p, gold);
            }
        }
        if (m.Outline is { Count: > 2 } outline)
        {
            using var line = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#E6F0D79A"), Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f,
                PathEffect = SKPathEffect.CreateDash([8, 6], 0) };
            using var path = new SKPath();
            path.MoveTo(P(outline[0].X, outline[0].Y));
            foreach (var (x, y) in outline.Skip(1)) path.LineTo(P(x, y));
            path.Close(); c.DrawPath(path, line);
        }
        foreach (var r in m.Frames)
        {
            var box = new SKRect(P(r.Left, r.Top).X - 3, P(r.Left, r.Top).Y - 3, P(r.Right, r.Bottom).X + 3, P(r.Right, r.Bottom).Y + 3);
            c.DrawRect(box, goldFill); c.DrawRect(box, gold);
        }
        if (m.Preview)
        {
            using var frame = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#CCE7C982"), Style = SKPaintStyle.Stroke, StrokeWidth = 4,
                PathEffect = SKPathEffect.CreateDash([12, 8], 0) };
            c.DrawRect(new SKRect(3, 3, PictureWidth - 3, PictureHeight - 3), frame);
        }
        if (m.Progress >= 0)
        {
            using var back = new SKPaint { Color = SKColor.Parse("#AA1A140E") };
            using var bar = new SKPaint { Color = SKColor.Parse("#D4AE62") };
            c.DrawRect(new SKRect(0, PictureHeight - 8, PictureWidth, PictureHeight), back);
            c.DrawRect(new SKRect(0, PictureHeight - 8, (float)(PictureWidth * m.Progress), PictureHeight), bar);
        }
        if (m.Cursor is { } cursor)
        {
            var at = P(cursor.X, cursor.Y);
            if (m.BrushRadius > 0)
            {
                using var brush = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#E6F6E7C4"), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                c.DrawCircle(at, (float)(m.BrushRadius * scale * 1.6), brush);
            }
            else if (m.Click)
            {
                using var ring = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#CCE7C982"), Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
                c.DrawCircle(at, 18, ring);
            }
            using var arrow = new SKPath();
            arrow.MoveTo(at);
            foreach (var (x, y) in new[] { (0.0, 26.0), (7.0, 20.0), (13.0, 32.0), (17.0, 30.0), (11.0, 18.0), (20.0, 18.0) }) arrow.LineTo(at.X + (float)x, at.Y + (float)y);
            arrow.Close();
            using var fill = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#1A140F") };
            using var edge = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F6E7C4"), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            c.DrawPath(arrow, fill); c.DrawPath(arrow, edge);
        }
        if (m.Note is { Length: > 0 } note)
        {
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 22);
            using var ink = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#F0D79A") };
            using var back = new SKPaint { IsAntialias = true, Color = SKColor.Parse("#DD1A140E") };
            var width = font.MeasureText(note);
            var box = new SKRect(PictureWidth - width - 24, PictureHeight - 48, PictureWidth - 8, PictureHeight - 14);
            c.DrawRoundRect(box, 6, 6, back);
            c.DrawText(note, box.Left + 8, box.Bottom - 9, font, ink);
        }
    }
}
