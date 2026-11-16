using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Map.Core.Documents;
using Map.Core.Editing;
using Map.Presentation.ViewModels;

namespace Map.Performance;

/// <summary>One user function of the coverage list and what measured it in this run.</summary>
internal sealed class BuildCoverageRow
{
    public required string Function { get; init; }
    public required string Family { get; init; }
    /// <summary>The edit rows (phase/edit name) that exercise it, or empty when it is deliberately not covered.</summary>
    public required string[] Edits { get; init; }
    public string? NotCoveredReason { get; init; }
    /// <summary>covered | refused (every command was refused) | missing (no row) | not covered: reason.</summary>
    public string Status { get; set; } = "";
    public string CoveredBy { get; set; } = "";
    public int Count { get; set; }
    public double WorkP95 { get; set; }
    public double WorkMax { get; set; }
}

/// <summary>
/// Owner 2026-11-16: «нужно тестить ВСЕ функции». The list of user functions the build ladder must exercise, by the
/// families of docs/perf/perf-functions.md §1 and the coordinator's list. Every function names the edit rows that cover
/// it; a function may be left uncovered only with a written reason. The coverage check fails the run when
///   - a listed function has no successful row (it crashed, was refused every time, or its phase was skipped);
///   - a family "### 1.N" of docs/perf/perf-functions.md has no function in this list;
///   - a value of EditorTool, StampPlacementMode, LandShapeKind or DecorationKind has no function here
/// - so a new tool, mode or family cannot stay untested without the run turning red.
/// </summary>
public sealed partial class ZoomAssetsApplication
{
    private static (string Id, string Family, string[] Edits, string? Reason)[] CoverageList() =>
    [
        // 1.1 rivers, lakes, coasts
        ("river: to the sea (auto)", "1.1", ["rivers/river-to-sea"], null),
        ("river: drawn by nodes", "1.1", ["rivers/river-nodes"], null),
        ("river: tributary into a river", "1.1", ["rivers/river-tributary"], null),
        ("river: node width", "1.1", ["rivers/river-node-width"], null),
        ("river: node feature (waterfall)", "1.1", ["rivers/river-node-feature"], null),
        ("river: style (width scale, water colour)", "1.1", ["rivers/river-style"], null),
        ("river: delete", "1.1", ["rivers/river-delete"], null),
        ("lake", "1.1", ["rivers/lake"], null),
        ("sea: manner", "1.3", ["continents/sea-manner"], null),
        ("sea: strength and scale", "1.3", ["rivers/sea-strength"], null),
        // 1.2 roads
        ("road: auto A->B", "1.2", ["roads/road-auto"], null),
        ("road: drawn by nodes", "1.2", ["roads/road-nodes"], null),
        ("road: width / style", "1.2", ["roads/road-style"], null),
        ("road: crossing / bridge", "1.2", ["roads/road-crossing"], null),
        ("road: duplicate and delete", "1.2", ["roads/road-duplicate", "roads/road-delete"], null),
        // 1.3 land and terrain
        ("tool:Land (freehand coast)", "1.3", ["continents/land-coast-stroke"], null),
        ("land: subtract (bays)", "1.3", ["continents/land-bay-stroke"], null),
        ("land shape:Circle", "1.3", ["continents/land-island-stroke"], null),
        ("land shape:Ellipse", "1.3", ["continents/land-ellipse"], null),
        ("land shape:Polygon", "1.3", ["continents/land-polygon-cape"], null),
        ("land shape:Rectangle", "1.3", ["continents/land-rectangle"], null),
        ("land shape:GridBlock", "1.3", ["continents/land-gridblock"], null),
        ("land shape:BoundedFill", "1.3", ["continents/land-boundedfill"], null),
        ("land: material paint", "1.3", ["continents/land-material-stroke"], null),
        ("land: material erase", "1.3", ["continents/land-material-erase"], null),
        ("land: edge style", "1.3", ["continents/land-style"], null),
        ("land: second level (higher tier)", "1.3", ["relief/land-level"], null),
        ("land: relief raise", "1.3", ["relief/relief-raise-stroke"], null),
        ("land: relief lower", "1.3", ["relief/relief-lower-stroke"], null),
        ("land: cliffs (cliff mask)", "1.3", ["relief/cliff-stroke"], null),
        // 1.4 layers
        ("layer: add object layer", "1.4", ["editing/layer-add"], null),
        ("layer: hide / show", "1.4", ["editing/layer-visibility"], null),
        ("layer: lock / unlock", "1.4", ["editing/layer-lock"], null),
        ("layer: opacity", "1.4", ["editing/layer-opacity"], null),
        ("layer: reorder", "1.4", ["editing/layer-reorder"], null),
        ("layer: duplicate", "1.4", ["editing/layer-duplicate"], null),
        ("layer: filter layer", "1.4", ["look/filter-layer"], null),
        ("tool:Mask (layer mask stroke)", "1.4", ["editing/mask-stroke"], null),
        ("tool:Brush (brush layer stroke)", "1.4", ["editing/brush-stroke"], null),
        // 1.5 styles, light, fog, decorations
        ("style preset (I/P/A)", "1.5", ["look/style-preset"], null),
        ("lighting: time of day", "1.5", ["look/light-time"], null),
        ("lighting: light source", "1.5", ["look/light-source"], null),
        ("fog of war brush", "1.5", ["look/fog-stroke"], null),
        ("weather", "1.5", [], "not covered: the editor has no weather; only hex travel (1.7) knows weather"),
        ("area beauty (soft edge, clumps, shadow)", "1.5", ["regions/area-beauty"], null),
        ("decoration:ScaleBar", "1.5", ["regions/region/decoration-ScaleBar"], null),
        ("decoration:Compass", "1.5", ["regions/region/decoration-Compass"], null),
        ("decoration:Cartouche", "1.5", ["regions/region/decoration-Cartouche"], null),
        ("decoration:Legend", "1.5", ["regions/region/decoration-Legend"], null),
        // 1.6 text
        ("tool:Text (drag a text box)", "1.6", ["regions/text-tool"], null),
        ("text: label presets", "1.6", ["towns/town/label", "regions/region/country-label", "regions/region/sea-label"], null),
        ("text: font family", "1.6", ["regions/text-font"], null),
        ("text: along a river", "1.6", ["regions/region/river-label-on-path"], null),
        ("text: along a road", "1.6", ["roads/road-label-on-path"], null),
        ("text: region label of an area", "1.6", ["regions/region/area-label"], null),
        ("tool:Notes (map note)", "1.6", ["editing/note"], null),
        // 1.7 hexes
        ("tool:Hex (hex grid + paint)", "1.7", ["hex/hex-layer", "hex/hex-paint"], null),
        ("hex: generator", "1.7", ["hex/hex-generate"], null),
        // 1.8 / 1.9 paths and areas
        ("placement:Path", "1.8", ["relief/hills/path"], null),
        ("path: spacing / seed", "1.8", ["editing/path-spacing"], null),
        ("placement:FillArea", "1.9", ["relief/forest/area"], null),
        ("area: rules", "1.9", ["regions/region/area-with-rules"], null),
        ("area: pattern (Peaks)", "1.9", ["regions/area-pattern"], null),
        ("area: density brush", "1.9", ["regions/area-density-stroke"], null),
        ("area: protection brush", "1.9", ["regions/area-protect-stroke"], null),
        ("area: bake", "1.9", ["regions/area-bake"], null),
        // 1.10 / 1.11 generation
        ("generation: continent", "1.10", ["generation/continent"], null),
        ("generation: generation area", "1.10", ["generation/area-generation"], null),
        ("generation: city", "1.11", ["generation/city"], null),
        // 1.12 stamps
        ("placement:One", "1.12", ["towns/town/landmark", "towns/interior/furniture"], null),
        ("placement:Series", "1.12", ["relief/mountains/series"], null),
        ("placement:Area (scatter)", "1.12", ["relief/nature/scatter", "fill/fill/scatter"], null),
        ("stamp: erase with Alt", "1.12", ["editing/stamp-erase"], null),
        ("stamp: appearance (hue, flip, shadow)", "1.12", ["editing/stamp-appearance"], null),
        // 1.13 selection and transforms
        ("tool:Select (click)", "1.13", ["editing/select-click"], null),
        ("select: additive multi-selection", "1.13", ["editing/select-add"], null),
        ("select: marquee", "1.13", ["editing/select-marquee"], null),
        ("select: all by filter", "1.13", ["editing/select-by-filter"], null),
        ("move (drag)", "1.13", ["editing/move-drag"], null),
        ("rotate", "1.13", ["editing/rotate"], null),
        ("scale", "1.13", ["editing/scale"], null),
        ("copy + paste", "1.13", ["editing/copy", "editing/paste"], null),
        ("group / ungroup", "1.13", ["editing/group", "editing/ungroup"], null),
        ("order (bring forward)", "1.13", ["editing/reorder"], null),
        ("move to another layer", "1.13", ["editing/move-to-layer"], null),
        ("delete", "1.13", ["editing/delete"], null),
        ("tool:Line", "1.13", ["editing/line-tool"], null),
        ("tool:Shape", "1.13", ["editing/shape-tool"], null),
        ("tool:Measure", "1.13", ["regions/measure"], null),
        // 1.14 history
        ("undo / redo of each family", "1.14", ["history/undo", "history/redo"], null),
        // 1.15 files
        ("save (atomic, Save As)", "1.15", ["files/save"], null),
        ("autosave / recovery snapshot", "1.15", ["files/autosave"], null),
        ("version checkpoint", "1.15", ["files/checkpoint"], null),
        ("reopen (new process)", "1.15", ["reopen/open"], "covered by the reopen process (MAP_BUILD_OPEN), not by the build process"),
        // 1.16 export
        ("export: image plan / preview", "1.16", ["files/export-preview"], null),
        ("export: full PNG / tiles / PDF / print", "1.16", [], "not covered: writes a full-size file (minutes, gigabytes at 131 072); only the plan/preview is measured"),
        // 1.17 catalog
        ("catalog: refresh and search", "1.17", ["editing/library-search"], null),
        ("catalog: favourite / import", "1.17", [], "not covered: would write the owner's mounted library catalog"),
        // 1.18 misc
        ("canvas resize workflow", "1.18", [], "not covered: preview runs synchronously per field change and replaces the whole map; a separate profile (native-editor-integrity) covers it"),
        ("timeline / compare / player window", "1.18", [], "not covered: separate windows and documents, profiles native-timeline / native-compare / native-player-package"),
        ("rooms, corridor, doors", "rooms", ["towns/interior/corridor", "towns/interior/room", "towns/interior/door"], null),
    ];

    private void CheckCoverage()
    {
        var rows = new List<BuildCoverageRow>();
        var edits = _report.BuildPhases!.SelectMany(p => p.Edits.Select(e => (Key: p.Name + "/" + e.Name, Edit: e))).ToArray();
        foreach (var (id, family, names, reason) in CoverageList())
        {
            var row = new BuildCoverageRow { Function = id, Family = family, Edits = names, NotCoveredReason = reason };
            var hits = edits.Where(e => names.Contains(e.Key)).ToArray();
            row.Count = hits.Sum(h => h.Edit.Count);
            row.CoveredBy = string.Join(", ", hits.Select(h => h.Key).Distinct());
            var work = hits.SelectMany(h => h.Edit.WorkMs).Order().ToArray();
            row.WorkP95 = Percentile(work, .95); row.WorkMax = work.Length == 0 ? 0 : work[^1];
            if (names.Length == 0 || id.StartsWith("reopen", StringComparison.Ordinal)) row.Status = reason ?? "not covered";
            else if (hits.Length == 0) row.Status = "missing";
            else if (hits.All(h => h.Edit.Refused >= h.Edit.Count)) row.Status = "refused";
            else row.Status = "covered";
            rows.Add(row);
            if (names.Length > 0 && !id.StartsWith("reopen", StringComparison.Ordinal))
                _report.Checks["coverage/" + id] = row.Status == "covered";
        }
        _report.BuildCoverage = rows;
        // Every value of the user-facing enums has a function in the list.
        var ids = rows.Select(r => r.Function).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in Enum.GetNames<EditorTool>())
            _report.Checks["coverage-list/tool:" + tool] = ids.Any(i => i.StartsWith("tool:" + tool + " ", StringComparison.Ordinal) || i == "tool:" + tool);
        foreach (var mode in Enum.GetNames<StampPlacementMode>())
            _report.Checks["coverage-list/placement:" + mode] = ids.Any(i => i == "placement:" + mode || i.StartsWith("placement:" + mode + " ", StringComparison.Ordinal));
        foreach (var shape in Enum.GetNames<LandShapeKind>())
            _report.Checks["coverage-list/land shape:" + shape] = ids.Contains("land shape:" + shape);
        foreach (var kind in Enum.GetNames<DecorationKind>())
            _report.Checks["coverage-list/decoration:" + kind] = ids.Contains("decoration:" + kind);
        // Every family of docs/perf/perf-functions.md has a function.
        if (FindRepoFile("docs/perf/perf-functions.md") is { } doc)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(doc), @"^### (1\.\d+) ", RegexOptions.Multiline))
                _report.Checks["coverage-list/family " + m.Groups[1].Value] = rows.Any(r => r.Family == m.Groups[1].Value);
            _report.Environment["coverage-families-from"] = doc;
        }
        else _report.Checks["coverage-list/perf-functions.md found"] = false;
        var covered = rows.Count(r => r.Status == "covered");
        var listed = rows.Count;
        var deliberate = rows.Count(r => r.Edits.Length == 0 || r.Function.StartsWith("reopen", StringComparison.Ordinal));
        _report.Environment["coverage"] = $"{covered} covered / {listed} listed ({deliberate} not covered with a reason, {listed - covered - deliberate} missing or refused)";
        var text = new StringBuilder();
        text.AppendLine("function                                   family  status      count  work p95 ms  max ms  covered by");
        foreach (var r in rows)
            text.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-42} {1,-6}  {2,-10} {3,6} {4,12:0.0} {5,7:0.0}  {6}",
                r.Function.Length > 42 ? r.Function[..42] : r.Function, r.Family,
                r.Status.StartsWith("not covered", StringComparison.Ordinal) ? "not cov." : r.Status, r.Count, r.WorkP95, r.WorkMax,
                r.Status.StartsWith("not covered", StringComparison.Ordinal) ? r.Status : r.CoveredBy));
        Console.Write(text);
        Console.WriteLine("coverage: " + _report.Environment["coverage"]);
        _report.Environment["coverage-table"] = text.ToString();
    }

    private static string? FindRepoFile(string relative)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, relative);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }
}
