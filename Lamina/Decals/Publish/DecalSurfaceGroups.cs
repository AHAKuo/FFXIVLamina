namespace Lamina.Decals.Publish;

/// <summary> Groups a design's decals by the skin surface they bake into. Pure, so it is unit tested without Dalamud. </summary>
public static class DecalSurfaceGroups
{
    /// <summary> Surfaces in enum order, each with its decals in design order (earlier decals are painted first, later ones land on top). </summary>
    public static IReadOnlyList<(DecalSurface Surface, IReadOnlyList<DecalSpec> Decals)> Group(IReadOnlyList<DecalSpec> decals)
    {
        var groups = new SortedDictionary<DecalSurface, List<DecalSpec>>();
        foreach (var decal in decals)
        {
            if (!groups.TryGetValue(decal.Placement.Surface, out var list))
                groups[decal.Placement.Surface] = list = new List<DecalSpec>();
            list.Add(decal);
        }

        return groups.Select(kv => (kv.Key, (IReadOnlyList<DecalSpec>)kv.Value)).ToList();
    }

    /// <summary>
    /// Groups decals by the skin mesh they bake into: the mesh of their surface whose diffuse the placement was made on,
    /// else the surface's first mesh (older placements, or a body that no longer draws that texture). Decals of a surface
    /// nothing draws are reported through <paramref name="unplaceable"/>. Design order is kept inside each group.
    /// </summary>
    public static IReadOnlyList<(SkinMesh Mesh, IReadOnlyList<DecalSpec> Decals)> GroupByMesh(IReadOnlyList<DecalSpec> decals,
        IReadOnlyList<SkinMesh> meshes, List<DecalSpec> unplaceable)
    {
        var order  = new List<SkinMesh>();
        var groups = new Dictionary<SkinMesh, List<DecalSpec>>();
        foreach (var decal in decals)
        {
            var placement = decal.Placement;
            var mesh = (placement.DiffuseGamePath is { Length: > 0 } diffuse
                           ? meshes.FirstOrDefault(m => m.Surface == placement.Surface && string.Equals(m.DiffuseGamePath, diffuse, StringComparison.OrdinalIgnoreCase))
                           : null)
                    ?? meshes.FirstOrDefault(m => m.Surface == placement.Surface);
            if (mesh is null)
            {
                unplaceable.Add(decal);
                continue;
            }

            if (!groups.TryGetValue(mesh, out var list))
            {
                groups[mesh] = list = new List<DecalSpec>();
                order.Add(mesh);
            }

            list.Add(decal);
        }

        return order.Select(m => (m, (IReadOnlyList<DecalSpec>)groups[m])).ToList();
    }
}
