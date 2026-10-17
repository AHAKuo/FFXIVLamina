using Lamina.Ipc;
using Penumbra.Api.Enums;

namespace Lamina.Runtime.Engine;

/// <summary>
/// Removal of the temporary mod settings one target wrote. Several targets may write into the same Penumbra collection (by
/// default every target shares Lamina's own), so nothing here ever clears a whole collection: each store only takes back the
/// mod directories it recorded in <see cref="AppliedStateStore.ModDirectories"/>.
/// </summary>
public static class TemporaryModSettings
{
    /// <summary>
    /// Removes every mod setting the store recorded from the collection. Directories Penumbra confirms gone (or never had) are
    /// forgotten; a refusal is reported in <paramref name="problems"/> and the directory stays recorded for a later attempt.
    /// </summary>
    /// <returns> True when at least one setting was removed. </returns>
    public static bool RemoveWritten(IPenumbraBridge penumbra, Guid collection, AppliedStateStore store, ICollection<string> problems)
    {
        var removed = false;
        foreach (var dir in store.ModDirectories.ToList())
        {
            var code = penumbra.RemoveTemporaryModSettings(collection, dir);
            switch (code)
            {
                case PenumbraApiEc.Success:
                    removed = true;
                    store.ModDirectories.Remove(dir);
                    break;
                case PenumbraApiEc.NothingChanged:
                case PenumbraApiEc.ModMissing:
                case PenumbraApiEc.CollectionMissing:
                    store.ModDirectories.Remove(dir);
                    break;
                default:
                    problems.Add($"RemoveTemporaryModSettings {dir}: {code}");
                    break;
            }
        }

        return removed;
    }
}
