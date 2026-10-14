using Lamina.Models;

namespace Lamina.Persistence;

/// <summary> Deep copies through a JSON round trip, optionally reassigning every Id so copies never alias originals. </summary>
public static class Cloner
{
    /// <summary> Exact deep copy (same Ids). </summary>
    public static T Clone<T>(T value) where T : class
        => JsonSettings.Deserialize<T>(JsonSettings.Serialize(value));

    /// <summary> Deep copy of a profile with fresh Ids on the profile, its groups, passes, condition nodes and layers. </summary>
    public static Profile CloneWithFreshIds(Profile profile)
    {
        var copy = Clone(profile);
        copy.Id = Guid.NewGuid();
        foreach (var group in copy.PassGroups)
            Refresh(group);
        return copy;
    }

    /// <summary> Deep copy of a group with fresh Ids on the group, its passes, condition nodes and layers. </summary>
    public static PassGroup CloneWithFreshIds(PassGroup group)
    {
        var copy = Clone(group);
        Refresh(copy);
        return copy;
    }

    /// <summary> Deep copy of a pass with fresh Ids on the pass, its condition nodes and layers. </summary>
    public static Pass CloneWithFreshIds(Pass pass)
    {
        var copy = Clone(pass);
        Refresh(copy);
        return copy;
    }

    /// <summary> Deep copy of a layer with a fresh Id. </summary>
    public static Layer CloneWithFreshIds(Layer layer)
    {
        var copy = Clone(layer);
        copy.Id = Guid.NewGuid();
        return copy;
    }

    /// <summary> Deep copy of a condition node (and children) with fresh Ids. </summary>
    public static ConditionNode CloneWithFreshIds(ConditionNode node)
    {
        var copy = Clone(node);
        Refresh(copy);
        return copy;
    }

    /// <summary> Reassigns Ids in place on a group and everything nested. </summary>
    public static void Refresh(PassGroup group)
    {
        group.Id = Guid.NewGuid();
        if (group.Conditions.Root is { } root)
            Refresh(root);
        foreach (var pass in group.Passes)
            Refresh(pass);
    }

    /// <summary> Reassigns Ids in place on a pass and everything nested. </summary>
    public static void Refresh(Pass pass)
    {
        pass.Id = Guid.NewGuid();
        if (pass.Conditions.Root is { } root)
            Refresh(root);
        foreach (var layer in pass.Layers)
            layer.Id = Guid.NewGuid();
    }

    /// <summary> Reassigns Ids in place on a condition node and its children. Unknown conditions keep their raw JSON as is. </summary>
    public static void Refresh(ConditionNode node)
    {
        node.Id = Guid.NewGuid();
        if (node is ConditionGroup group)
            foreach (var child in group.Children)
                Refresh(child);
    }

    /// <summary> Every Id reachable from a profile, for tests and duplicate detection. </summary>
    public static IEnumerable<Guid> AllIds(Profile profile)
    {
        yield return profile.Id;
        foreach (var group in profile.PassGroups)
            foreach (var id in AllIds(group))
                yield return id;
    }

    /// <summary> Every Id reachable from a group. </summary>
    public static IEnumerable<Guid> AllIds(PassGroup group)
    {
        yield return group.Id;
        foreach (var id in AllIds(group.Conditions))
            yield return id;
        foreach (var pass in group.Passes)
            foreach (var id in AllIds(pass))
                yield return id;
    }

    /// <summary> Every Id reachable from a pass. </summary>
    public static IEnumerable<Guid> AllIds(Pass pass)
    {
        yield return pass.Id;
        foreach (var id in AllIds(pass.Conditions))
            yield return id;
        foreach (var layer in pass.Layers)
            yield return layer.Id;
    }

    private static IEnumerable<Guid> AllIds(ConditionStack stack)
    {
        if (stack.Root is null)
            yield break;
        var pending = new Stack<ConditionNode>();
        pending.Push(stack.Root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node.Id;
            if (node is ConditionGroup group)
                foreach (var child in group.Children)
                    pending.Push(child);
        }
    }
}
