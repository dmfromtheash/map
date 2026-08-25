namespace Map.Spike.Core.Spatial;

/// <summary>
/// Evidence that a query did not walk the whole scene. Acceptance criterion 5 is judged on
/// <see cref="CandidatesExamined"/> being far below <see cref="SceneObjectCount"/>.
/// </summary>
public struct SpatialQueryStats
{
    public int SceneObjectCount;
    public int CellsVisited;
    public int CandidatesExamined;
    public int PreciseTests;

    public override readonly string ToString() =>
        $"scene={SceneObjectCount} cells={CellsVisited} candidates={CandidatesExamined} precise={PreciseTests}";
}
