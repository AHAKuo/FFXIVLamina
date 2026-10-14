using Dalamud.Plugin.Services;
using Glamourer.Api.Enums;
using Lamina.Ipc;
using Item = Lumina.Excel.Sheets.Item;
using Vitrine;

namespace Lamina.Runtime.Engine;

/// <summary> Lamina's side of the Vitrine boundary: data directory, collection id, the reapply hook and item try-ons. </summary>
public sealed class VitrineHost : IVitrineHost
{
    private Func<Guid?> _collectionId   = () => null;
    private Action      _requestReapply = () => { };
    private Func<bool>  _glamourerHeld  = () => false;
    private Action      _reapplyNow     = () => { };

    private IGlamourerBridge? _glamourer;
    private IDataManager?     _data;
    private IFramework?       _framework;
    private IClientState?     _clientState;
    private IObjectTable?     _objects;

    /// <param name="dataDirectory"> Where Vitrine writes its cache and designs. </param>
    public VitrineHost(string dataDirectory)
        => DataDirectory = dataDirectory;

    /// <inheritdoc/>
    public string DataDirectory { get; }

    /// <inheritdoc/>
    public Guid? LaminaCollectionId
        => _collectionId();

    /// <inheritdoc/>
    public int TemporaryKey
        => IpcConstants.PenumbraKey;

    /// <inheritdoc/>
    public string TemporarySource
        => IpcConstants.PenumbraSource;

    /// <summary>
    /// Vitrine writes temporary settings with Lamina's key (design previews, baselines), so the collection no longer matches
    /// what Lamina last wrote: the mod section is forgotten and the next evaluation rewrites it.
    /// </summary>
    public void RequestReapply()
        => _requestReapply();

    /// <inheritdoc/>
    public bool CanTryOn
        => _glamourer is { Available: true } && LocalPlayerIndex() is not null;

    /// <inheritdoc/>
    public string TryOnItem(ulong itemId)
    {
        if (_glamourer is null || _data is null)
            return "Try-on is not wired up.";
        if (!_glamourer.Available)
            return "Glamourer is not loaded.";
        if (LocalPlayerIndex() is not { } index)
            return "No character to equip on.";

        var slot = SlotOf(itemId, out var name);
        if (slot == ApiEquipSlot.Unknown)
            return $"{name} is not an equippable item.";

        var code = _glamourer.SetItem(index, slot, itemId, Array.Empty<byte>());
        return code is GlamourerApiEc.Success or GlamourerApiEc.NothingDone
            ? $"Equipped {name} ({slot}). Revert try-on puts things back."
            : $"Glamourer returned {code} for {name}.";
    }

    /// <summary>
    /// Try-ons are temporary Glamourer changes. When Lamina holds a Glamourer state, a forced reapply re-asserts it;
    /// otherwise the character goes back to whatever Glamourer automation would show.
    /// </summary>
    public string RevertTryOn()
    {
        if (_glamourer is null)
            return "Try-on is not wired up.";
        if (LocalPlayerIndex() is not { } index)
            return "No character to revert.";

        if (_glamourerHeld())
        {
            _reapplyNow();
            return "Lamina design re-applied.";
        }

        var code = _glamourer.UnlockAndRevertToAutomation(index);
        return code is GlamourerApiEc.Success or GlamourerApiEc.NothingDone
            ? "Reverted to Glamourer automation."
            : $"Glamourer returned {code}.";
    }

    /// <summary> Connects the host to the engine and the services try-ons need (Vitrine is constructed before the engine). </summary>
    public void Attach(LaminaEngine engine, IGlamourerBridge glamourer, IDataManager data, IFramework framework, IClientState clientState,
        IObjectTable objects)
    {
        _collectionId   = () => engine.CollectionId;
        _requestReapply = engine.InvalidateMods;
        _glamourerHeld  = () => engine.AppliedState.GlamourerApplied;
        _reapplyNow     = engine.RequestReapply;
        _glamourer      = glamourer;
        _data           = data;
        _framework      = framework;
        _clientState    = clientState;
        _objects        = objects;
    }

    /// <summary> The object table only answers on the framework thread; the UI draws there, so this is safe from the window. </summary>
    private int? LocalPlayerIndex()
    {
        if (_framework is not { IsInFrameworkUpdateThread: true } || _clientState is not { IsLoggedIn: true } || _objects is null)
            return null;
        return _objects.LocalPlayer is { } player ? player.ObjectIndex : null;
    }

    /// <summary> Maps an item's EquipSlotCategory to the Glamourer slot; Unknown for non-equipment (waist, soul crystals, non-items). </summary>
    private ApiEquipSlot SlotOf(ulong itemId, out string name)
    {
        name = $"item {itemId}";
        if (_data is null || itemId > uint.MaxValue)
            return ApiEquipSlot.Unknown;

        var row = _data.GetExcelSheet<Item>()?.GetRowOrDefault((uint)itemId);
        if (row is not { } item)
            return ApiEquipSlot.Unknown;

        var itemName = item.Name.ExtractText();
        if (itemName.Length > 0)
            name = itemName;

        var category = item.EquipSlotCategory.ValueNullable;
        if (category is not { } c)
            return ApiEquipSlot.Unknown;

        if (c.MainHand != 0) return ApiEquipSlot.MainHand;
        if (c.OffHand != 0)  return ApiEquipSlot.OffHand;
        if (c.Head != 0)     return ApiEquipSlot.Head;
        if (c.Body != 0)     return ApiEquipSlot.Body;
        if (c.Gloves != 0)   return ApiEquipSlot.Hands;
        if (c.Legs != 0)     return ApiEquipSlot.Legs;
        if (c.Feet != 0)     return ApiEquipSlot.Feet;
        if (c.Ears != 0)     return ApiEquipSlot.Ears;
        if (c.Neck != 0)     return ApiEquipSlot.Neck;
        if (c.Wrists != 0)   return ApiEquipSlot.Wrists;
        if (c.FingerR != 0)  return ApiEquipSlot.RFinger;
        if (c.FingerL != 0)  return ApiEquipSlot.LFinger;
        return ApiEquipSlot.Unknown;
    }
}
