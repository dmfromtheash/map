using System.Collections.Immutable;
using Map.Core.Documents;

namespace Map.Core.Editing;

internal static class PlacementAreaGenerationProtection
{
    // Called before an area's materialized delta is written. Manual detach/delete remains explicit.
    internal static PlacementArea Preserve(PlacementArea? before, PlacementArea after, List<SceneObject> replacements,
        List<SceneObject> additions, HashSet<ObjectId> removals)
    {
        if (before?.GenerationProtection is not { Count: > 0 } protection) return after;
        var protectedMembers = before.Members.Where(m => protection.Contains(m.Slot)).ToArray();
        var protectedIds = protectedMembers.Select(m => m.ObjectId).ToHashSet();
        replacements.RemoveAll(o => protectedIds.Contains(o.Id));
        additions.RemoveAll(o => protectedIds.Contains(o.Id)); removals.ExceptWith(protectedIds);
        var members = after.Members.Where(m => !protection.Contains(m.Slot)).Concat(protectedMembers).ToArray();
        return after with { GenerationProtection = protection,
            Cells = members.GroupBy(m => m.Slot.Cell).ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray()),
            MemberCount = members.Length, OccupiedSlots = after.OccupiedSlots.Except(protection),
            RuleSlots = after.RuleSlots?.Except(protection) };
    }
}
