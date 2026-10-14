using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Vitrine.Catalog;
using Vitrine.Designs;
using Vitrine.Photos;

namespace Vitrine.UI;

/// <summary> The Vitrine window (/vit): Mods · Collections · Settings · About. </summary>
public sealed class VitrineWindow : Window, IDisposable
{
    private const float DetailsWidth = 380f;

    private static readonly uint Accent      = Rgba(0x5E, 0xD6, 0xC5, 0xFF);
    private static readonly uint AccentDim   = Rgba(0x5E, 0xD6, 0xC5, 0x40);
    private static readonly uint ChipOnBg    = Rgba(0x1F, 0x4A, 0x45, 0xFF);
    private static readonly uint ChipOffBg   = Rgba(0x33, 0x33, 0x38, 0xFF);
    private static readonly uint ChipText    = Rgba(0xE6, 0xE6, 0xE6, 0xFF);
    private static readonly uint MutedText   = Rgba(0xA0, 0xA0, 0xA8, 0xFF);
    private static readonly uint WarnText    = Rgba(0xE8, 0xB4, 0x5A, 0xFF);
    private static readonly uint CardBg      = Rgba(0x1C, 0x1C, 0x20, 0xFF);
    private static readonly uint CardBgHover = Rgba(0x24, 0x24, 0x2A, 0xFF);

    private readonly VitrineModule _module;
    private readonly ITextureProvider _textures;

    // Mods tab state
    private string _search = string.Empty;
    private VitrineCategory? _categoryFilter;
    private StateFilter _stateFilter = StateFilter.Any;
    private string? _selectedMod;
    private string _statusMessage = string.Empty;
    private DateTime _statusUntil = DateTime.MinValue;
    private string _newVariantName = string.Empty;
    private Guid? _editingVariant;
    private readonly Dictionary<string, Baseline> _baselineDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, StoredVariantDraft> _variantDrafts = new();
    private List<ModInformation> _visible = new();
    private int _visibleVersion = -1;
    private int _catalogVersion;
    private string _lastFilterKey = string.Empty;

    // Collections tab state
    private CollectionsView? _collections;

    // Collection facts read through IPC, refreshed by the module (never per frame).
    private (Guid Id, string Name, bool IndividualSet)? _playerCollection;
    private (Guid Id, string Name)? _defaultCollection;

    public VitrineWindow(VitrineModule module, ITextureProvider textures)
        : base("Vitrine###VitrineMainWindow")
    {
        _module   = module;
        _textures = textures;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size          = new Vector2(1180, 760);
        SizeCondition = ImGuiCond.FirstUseEver;
        module.CatalogChanged += () => _catalogVersion++;
    }

    /// <summary> Called by the module after per-collection states were refreshed. </summary>
    public void OnCollectionStatesRefreshed()
    {
        _visibleVersion = -1;
        RefreshCollectionFacts();
        if (IsOpen)
            _collections = CollectionsView.Build(_module);
    }

    /// <summary> Re-reads the two collection facts the header and details panel show. Two IPC calls; the module runs this at 1 Hz while open. </summary>
    public void RefreshCollectionFacts()
    {
        if (!_module.Reader.Available)
        {
            _playerCollection  = null;
            _defaultCollection = null;
            return;
        }

        _playerCollection  = _module.Reader.GetCollectionForObject(0);
        _defaultCollection = _module.Reader.GetSpecialCollection(ApiCollectionType.Default);
    }

    public override void OnOpen()
    {
        _module.RequestCollectionRefresh();
        RefreshCollectionFacts();
        _collections = CollectionsView.Build(_module);
    }

    public override void Draw()
    {
        DrawHeader();
        using var tabs = ImRaii.TabBar("##vitrineTabs");
        if (!tabs)
            return;

        using (var tab = ImRaii.TabItem("Mods"))
        {
            if (tab)
                DrawModsTab();
        }

        using (var tab = ImRaii.TabItem("Collections"))
        {
            if (tab)
                DrawCollectionsTab();
        }

        using (var tab = ImRaii.TabItem("Settings"))
        {
            if (tab)
                DrawSettingsTab();
        }

        using (var tab = ImRaii.TabItem("About"))
        {
            if (tab)
                DrawAboutTab();
        }
    }

    // ---------------------------------------------------------------------------------------------------- header

    private void DrawHeader()
    {
        ImGui.TextColored(Color(Accent), "Vitrine");
        ImGui.SameLine();
        ImGui.TextColored(Color(MutedText), "Lamina's mod view");

        var status = HeaderStatus();
        var width  = ImGui.CalcTextSize(status.Text).X;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - width - 8 * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(Color(status.Ok ? Accent : WarnText), status.Text);
        ImGui.Separator();
    }

    private (string Text, bool Ok) HeaderStatus()
    {
        if (!_module.Available)
            return ("● Penumbra not available", false);
        if (_module.Host.LaminaCollectionId is null)
            return ($"● Collection \"{_module.Host.TemporarySource}\" not found in Penumbra", false);

        if (_playerCollection is { } p && p.Id == _module.Host.LaminaCollectionId)
            return ("● Lamina collection assigned to Yourself", true);
        return ("● Your character is not assigned to the Lamina collection (assign it in Penumbra to let Lamina set mods)", false);
    }

    // ---------------------------------------------------------------------------------------------------- mods tab

    private void DrawModsTab()
    {
        var scale = ImGuiHelpers.GlobalScale;

        // Filter row.
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 330 * scale);
        ImGui.InputTextWithHint("##vitSearch", "Search mods, authors, affected items...", ref _search, 256);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170 * scale);
        using (var combo = ImRaii.Combo("##vitSort", $"Sort: {SortLabel(_module.Settings.Sort)}"))
        {
            if (combo)
                foreach (var sort in Enum.GetValues<ModSort>())
                    if (ImGui.Selectable(SortLabel(sort), sort == _module.Settings.Sort))
                    {
                        _module.Settings.Sort = sort;
                        _module.SaveSettings();
                    }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140 * scale);
        using (var combo = ImRaii.Combo("##vitState", $"State: {StateLabel(_stateFilter)}"))
        {
            if (combo)
                foreach (var state in Enum.GetValues<StateFilter>())
                    if (ImGui.Selectable(StateLabel(state), state == _stateFilter))
                        _stateFilter = state;
        }

        // Category chips.
        DrawChip("All", _categoryFilter is null, () => _categoryFilter = null);
        foreach (var category in PresentCategories())
        {
            ImGui.SameLine();
            var c = category;
            DrawChip(ModClassifier.Label(c), _categoryFilter == c, () => _categoryFilter = c);
        }

        if (_module.Catalog.IsScanning)
        {
            var (done, total) = _module.Catalog.ScanProgress;
            ImGui.SameLine();
            ImGui.TextColored(Color(MutedText), $"Scanning {done}/{total}…");
        }

        ImGui.Spacing();

        RefreshVisibleIfNeeded();

        var showDetails = _selectedMod is not null && _module.Catalog.Get(_selectedMod) is not null;
        var gridWidth   = showDetails ? ImGui.GetContentRegionAvail().X - DetailsWidth * scale - ImGui.GetStyle().ItemSpacing.X : 0;

        using (var grid = ImRaii.Child("##vitGrid", new Vector2(gridWidth, 0), false))
        {
            if (grid)
                DrawCardGrid();
        }

        if (showDetails)
        {
            ImGui.SameLine();
            using var details = ImRaii.Child("##vitDetails", new Vector2(DetailsWidth * scale, 0), true);
            // The grid may have changed the selection this frame; resolve it again instead of trusting showDetails.
            if (!details)
                return;
            var selectedInfo = _selectedMod is null ? null : _module.Catalog.Get(_selectedMod);
            if (selectedInfo is not null)
                DrawDetails(selectedInfo);
        }
    }

    private IEnumerable<VitrineCategory> PresentCategories()
        => _module.Catalog.Mods.Select(m => m.Type).Distinct().OrderBy(c => c);

    private void RefreshVisibleIfNeeded()
    {
        var key = $"{_search}|{_categoryFilter}|{_stateFilter}|{_module.Settings.Sort}|{_module.Settings.ShowUnsorted}";
        if (_visibleVersion == _catalogVersion && key == _lastFilterKey)
            return;

        _visibleVersion = _catalogVersion;
        _lastFilterKey  = key;
        var lamina = _module.Host.LaminaCollectionId;
        IEnumerable<ModInformation> query = _module.Catalog.Mods;

        if (!_module.Settings.ShowUnsorted)
            query = query.Where(m => m.AffectedItems.Count > 0);
        if (_categoryFilter is { } cat)
            query = query.Where(m => m.Type == cat);
        if (_stateFilter != StateFilter.Any)
            query = query.Where(m => IsOnInLamina(m, lamina) == (_stateFilter == StateFilter.OnInLamina));
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var s = _search.Trim();
            query = query.Where(m => m.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                                  || m.Author.Contains(s, StringComparison.OrdinalIgnoreCase)
                                  || m.ModDirectory.Contains(s, StringComparison.OrdinalIgnoreCase)
                                  || m.Tags.Any(t => t.Contains(s, StringComparison.OrdinalIgnoreCase))
                                  || m.AffectedItems.Any(i => i.Name.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }

        query = _module.Settings.Sort switch
        {
            ModSort.Author          => query.OrderBy(m => m.Author, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            ModSort.Category        => query.OrderBy(m => m.Type).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            ModSort.RecentlyScanned => query.OrderByDescending(m => _module.Catalog.ScannedAt(m.ModDirectory)).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
            _                       => query.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        };

        _visible = query.ToList();
    }

    private static bool IsOnInLamina(ModInformation mod, Guid? lamina)
        => lamina is { } id && mod.PerCollection.TryGetValue(id, out var state) && state.Enabled;

    private void DrawCardGrid()
    {
        var scale     = ImGuiHelpers.GlobalScale;
        var spacing   = ImGui.GetStyle().ItemSpacing;
        var cardWidth = _module.Settings.CardWidth * scale;
        var available = ImGui.GetContentRegionAvail().X;
        var columns   = Math.Max(1, (int)Math.Floor((available + spacing.X) / (cardWidth + spacing.X)));
        var imageH    = cardWidth * 0.52f;
        var cardH     = imageH + ImGui.GetTextLineHeightWithSpacing() * 3 + ImGui.GetStyle().WindowPadding.Y * 2 + 6 * scale;

        if (_visible.Count == 0)
        {
            ImGui.TextColored(Color(MutedText), _module.Available
                ? (_module.Catalog.Mods.Count == 0 ? "No mods catalogued yet. Use Settings → Rescan." : "Nothing matches the current filters.")
                : "Penumbra is not available.");
            return;
        }

        // Only draw rows that are on screen: ImGuiListClipper over rows.
        var rows = (_visible.Count + columns - 1) / columns;
        var rowH = cardH + spacing.Y;
        var scrollY = ImGui.GetScrollY();
        var viewH   = ImGui.GetWindowHeight();
        var firstRow = Math.Max(0, (int)Math.Floor(scrollY / rowH) - 1);
        var lastRow  = Math.Min(rows - 1, (int)Math.Ceiling((scrollY + viewH) / rowH) + 1);

        if (firstRow > 0)
            ImGui.Dummy(new Vector2(0, firstRow * rowH - spacing.Y));

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var index = row * columns + col;
                if (index >= _visible.Count)
                    break;
                if (col > 0)
                    ImGui.SameLine();
                DrawCard(_visible[index], cardWidth, cardH, imageH);
            }
        }

        var remaining = rows - 1 - lastRow;
        if (remaining > 0)
            ImGui.Dummy(new Vector2(0, remaining * rowH - spacing.Y));
    }

    private void DrawCard(ModInformation mod, float width, float height, float imageH)
    {
        var scale    = ImGuiHelpers.GlobalScale;
        var selected = string.Equals(mod.ModDirectory, _selectedMod, StringComparison.OrdinalIgnoreCase);
        var lamina   = _module.Host.LaminaCollectionId;
        var onLamina = IsOnInLamina(mod, lamina);

        using var id = ImRaii.PushId(mod.ModDirectory);
        var origin = ImGui.GetCursorScreenPos();
        var draw   = ImGui.GetWindowDrawList();

        // Card background + border.
        var hovered = ImGui.IsMouseHoveringRect(origin, origin + new Vector2(width, height));
        draw.AddRectFilled(origin, origin + new Vector2(width, height), hovered ? CardBgHover : CardBg, 8 * scale);
        if (selected)
            draw.AddRect(origin, origin + new Vector2(width, height), Accent, 8 * scale, ImDrawFlags.None, 1.5f * scale);

        // Image area.
        var pad = 8 * scale;
        var imgMin = origin + new Vector2(pad, pad);
        var imgMax = origin + new Vector2(width - pad, pad + imageH);
        var (wrap, photo) = ResolvePhoto(mod);
        if (wrap is not null)
        {
            var uv = CoverUv(wrap.Width, wrap.Height, imgMax.X - imgMin.X, imgMax.Y - imgMin.Y);
            draw.AddImageRounded(wrap.Handle, imgMin, imgMax, uv.Min, uv.Max, 0xFFFFFFFF, 6 * scale);
            draw.AddRectFilledMultiColor(imgMin, imgMax, 0x00000000, 0x00000000, 0xA0000000, 0xA0000000);
        }
        else
        {
            draw.AddRectFilled(imgMin, imgMax, CategoryColor(mod.Type), 6 * scale);
            if (photo.Status == PhotoStatus.Pending)
                draw.AddText(new Vector2(imgMin.X + 8 * scale, imgMin.Y + 6 * scale), MutedText, "fetching photo…");
        }

        var label = ModClassifier.Label(mod.Type).ToUpperInvariant();
        draw.AddText(new Vector2(imgMin.X + 8 * scale, imgMax.Y - ImGui.GetTextLineHeight() - 6 * scale), ChipText, label);
        DrawStateChip(draw, imgMax, onLamina ? "On · Lamina" : "Off", onLamina);

        // Text lines.
        var cursor = new Vector2(imgMin.X, imgMax.Y + 6 * scale);
        draw.AddText(cursor, 0xFFFFFFFF, Ellipsis(mod.Name, width - 2 * pad));
        cursor.Y += ImGui.GetTextLineHeightWithSpacing();
        var byLine = string.IsNullOrEmpty(mod.Author) ? (string.IsNullOrEmpty(mod.Version) ? string.Empty : $"v{mod.Version}") : $"by {mod.Author}{(string.IsNullOrEmpty(mod.Version) ? string.Empty : $" · v{mod.Version}")}";
        draw.AddText(cursor, MutedText, Ellipsis(byLine, width - 2 * pad));
        cursor.Y += ImGui.GetTextLineHeightWithSpacing();
        var items   = mod.AffectedItems.Count == 1 ? "1 item" : $"{mod.AffectedItems.Count} items";
        var designs = 1 + _module.DesignStore.VariantsFor(mod.ModDirectory).Count();
        var designsText = designs == 1 ? "1 design" : $"{designs} designs";
        draw.AddText(cursor, MutedText, items);
        draw.AddText(new Vector2(imgMax.X - ImGui.CalcTextSize(designsText).X, cursor.Y), MutedText, designsText);

        // Click target.
        if (ImGui.InvisibleButton("##card", new Vector2(width, height)))
            _selectedMod = mod.ModDirectory;
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(mod.Description))
        {
            using var tooltip = ImRaii.Tooltip();
            ImGui.PushTextWrapPos(400 * scale);
            ImGui.TextUnformatted(mod.Description);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawStateChip(ImDrawListPtr draw, Vector2 imgMax, string text, bool on)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var size  = ImGui.CalcTextSize(text);
        var padX  = 8 * scale;
        var padY  = 3 * scale;
        var max   = new Vector2(imgMax.X - 6 * scale, imgMax.Y - 6 * scale);
        var min   = max - size - new Vector2(padX * 2, padY * 2);
        draw.AddRectFilled(min, max, on ? ChipOnBg : ChipOffBg, 10 * scale);
        draw.AddText(min + new Vector2(padX, padY), on ? Accent : ChipText, text);
    }

    /// <summary> Photo state plus texture for a mod; asks the photo service to resolve it when nothing is known yet. </summary>
    private (IDalamudTextureWrap? Wrap, PhotoState State) ResolvePhoto(ModInformation mod)
    {
        if (!_module.Settings.ShowImages)
            return (null, PhotoState.Missing);

        var state = _module.Photos.GetPhoto(mod);
        if (state.Status != PhotoStatus.Ready || string.IsNullOrEmpty(state.Path))
            return (null, state);

        try
        {
            return (_textures.GetFromFile(state.Path).GetWrapOrDefault(), state);
        }
        catch
        {
            return (null, state);
        }
    }

    // ---------------------------------------------------------------------------------------------------- details

    private void DrawDetails(ModInformation mod)
    {
        var scale  = ImGuiHelpers.GlobalScale;
        var lamina = _module.Host.LaminaCollectionId;

        // Image banner.
        var avail = ImGui.GetContentRegionAvail().X;
        var (wrap, photo) = ResolvePhoto(mod);
        var bannerH = avail * 0.45f;
        var origin  = ImGui.GetCursorScreenPos();
        var draw    = ImGui.GetWindowDrawList();
        if (wrap is not null)
        {
            var uv = CoverUv(wrap.Width, wrap.Height, avail, bannerH);
            draw.AddImageRounded(wrap.Handle, origin, origin + new Vector2(avail, bannerH), uv.Min, uv.Max, 0xFFFFFFFF, 6 * scale);
            draw.AddRectFilledMultiColor(origin, origin + new Vector2(avail, bannerH), 0x00000000, 0x00000000, 0xA0000000, 0xA0000000);
        }
        else
        {
            draw.AddRectFilled(origin, origin + new Vector2(avail, bannerH), CategoryColor(mod.Type), 6 * scale);
        }

        var bannerLabel = photo.Status switch
        {
            PhotoStatus.Ready   => $"{ModClassifier.Label(mod.Type).ToUpperInvariant()} · MOD PREVIEW IMAGE",
            PhotoStatus.Pending => $"{ModClassifier.Label(mod.Type).ToUpperInvariant()} · FETCHING PHOTO…",
            _                   => $"{ModClassifier.Label(mod.Type).ToUpperInvariant()} · NO PREVIEW IMAGE",
        };
        draw.AddText(origin + new Vector2(8 * scale, bannerH - ImGui.GetTextLineHeight() - 6 * scale), ChipText, bannerLabel);
        ImGui.Dummy(new Vector2(avail, bannerH + 6 * scale));

        ImGui.TextUnformatted(mod.Name);
        ImGui.TextColored(Color(MutedText), string.IsNullOrEmpty(mod.Author) ? $"v{mod.Version}" : $"by {mod.Author} · v{mod.Version}");
        if (!string.IsNullOrEmpty(mod.Website))
        {
            ImGui.SameLine();
            ImGui.TextColored(Color(Accent), "· mod page");
            if (ImGui.IsItemClicked())
                Dalamud.Utility.Util.OpenLink(mod.Website);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(mod.Website);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh photo"))
        {
            _module.Photos.RequestRefresh(mod);
            SetStatus("Looking for a photo again…");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Forget the cached photo and look again: mod folder first, then the mod page's preview image.");

        ImGui.TextColored(Color(MutedText), $"id: [{mod.ModDirectory}]");
        if (!string.IsNullOrEmpty(mod.Description))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(mod.Description.Length > 600 ? mod.Description[..600] + "…" : mod.Description);
            ImGui.PopTextWrapPos();
        }

        if (mod.Tags.Count > 0)
            ImGui.TextColored(Color(MutedText), string.Join(" · ", mod.Tags));

        ImGui.Spacing();
        SectionLabel("AFFECTS");
        if (mod.AffectedItems.Count == 0)
            ImGui.TextColored(Color(MutedText), "Nothing identified (files only).");
        else
            DrawAffectedChips(mod.AffectedItems);
        DrawTryOnRow(mod);

        ImGui.Spacing();
        SectionLabel("STATE PER COLLECTION (READ ONLY)");
        using (var table = ImRaii.Table("##states", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            if (table)
            {
                ImGui.TableNextColumn();
                DrawStateCell("Lamina", lamina is { } l && mod.PerCollection.TryGetValue(l, out var ls) ? ls : null);
                ImGui.TableNextColumn();
                var def = _defaultCollection;
                DrawStateCell(def?.Name ?? "Default", def is { } d && mod.PerCollection.TryGetValue(d.Id, out var ds) ? ds : null);
                ImGui.TableNextColumn();
                var active = mod.CurrentActiveCollection is { } ac && mod.PerCollection.TryGetValue(ac, out var acs) ? acs : ((bool, int, Dictionary<string, List<string>>)?)null;
                ImGui.TextColored(Color(MutedText), "Priority");
                ImGui.TextUnformatted(active is { } a ? a.Item2.ToString() : "–");
            }
        }

        ImGui.Spacing();
        SectionLabel("DESIGNS FROM THIS MOD");
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 90 * scale);
        if (ImGui.SmallButton("+ Variant"))
        {
            _newVariantName = $"{mod.Name} · Variant";
            ImGui.OpenPopup("##newVariant");
        }

        using (var popup = ImRaii.Popup("##newVariant"))
        {
            if (popup)
            {
                ImGui.SetNextItemWidth(260 * scale);
                ImGui.InputText("##variantName", ref _newVariantName, 128);
                if (ImGui.Button("Create"))
                {
                    var newId = _module.CreateVariant(mod.ModDirectory, _newVariantName);
                    _editingVariant = newId;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    ImGui.CloseCurrentPopup();
            }
        }

        var auto = _module.DesignStore.AutoDesignFor(mod.ModDirectory);
        if (auto is not null)
            DrawBaselineCard(mod, auto);

        foreach (var variant in _module.DesignStore.VariantsFor(mod.ModDirectory).ToList())
            DrawVariantCard(mod, variant);

        ImGui.Spacing();
        if (auto is not null)
        {
            if (ImGui.Button("Use in a ruleset", new Vector2(ImGui.GetContentRegionAvail().X * 0.5f - 4 * scale, 0)))
            {
                ImGui.SetClipboardText(auto.Id.ToString());
                SetStatus("Design id copied. Add a Vitrine Design layer in Lamina and pick it there.");
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Copies the design id. In Lamina: Pass → Add Layer → Vitrine Design.");
            ImGui.SameLine();
            if (ImGui.Button("Preview on character", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            {
                var message = _module.PreviewDesign(auto);
                if (ImGui.GetIO().KeyShift)
                    message += " " + TryOnAll(mod);
                SetStatus(message);
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Enables the design in the Lamina collection and redraws.\nHold Shift to also equip its gear through Glamourer so the mod is visible.");
            if (ImGui.SmallButton("Clear preview"))
                SetStatus(_module.ClearPreview(mod.ModDirectory));
        }

        if (DateTime.UtcNow < _statusUntil)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Color(Accent), _statusMessage);
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawStateCell(string title, (bool Enabled, int Priority, Dictionary<string, List<string>> Settings)? state)
    {
        ImGui.TextColored(Color(MutedText), title);
        if (state is { } s)
            ImGui.TextColored(Color(s.Enabled ? Accent : ChipText), s.Enabled ? "On" : "Off");
        else
            ImGui.TextColored(Color(MutedText), "–");
    }

    /// <summary> Gear mods only show on worn items: a row of try-on buttons equips the affected pieces through Glamourer (via the host). </summary>
    private void DrawTryOnRow(ModInformation mod)
    {
        var equippable = mod.AffectedItems.Where(i => i.ItemId is not null && ModClassifier.IsEquippable(i.Category)).ToList();
        if (equippable.Count == 0)
            return;

        var host  = _module.Host;
        var can   = host.CanTryOn;
        var scale = ImGuiHelpers.GlobalScale;
        using var id = ImRaii.PushId("tryon");
        SectionLabel("TRY ON");
        using (ImRaii.Disabled(!can))
        {
            var shown = 0;
            foreach (var item in equippable)
            {
                if (shown > 0 && ImGui.GetCursorPosX() + ImGui.CalcTextSize(item.Name).X + 24 * scale < ImGui.GetWindowContentRegionMax().X)
                    ImGui.SameLine();
                using var itemId = ImRaii.PushId(item.Name);
                if (ImGui.SmallButton(item.Name))
                    SetStatus(host.TryOnItem(item.ItemId!.Value));
                shown++;
            }

            if (ImGui.SmallButton("Try all"))
                SetStatus(TryOnAll(mod));
            ImGui.SameLine();
            if (ImGui.SmallButton("Revert try-on"))
                SetStatus(host.RevertTryOn());
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(can
                ? "Equips the pieces through Glamourer so you can see the mod. Revert re-applies the Lamina design, or Glamourer automation when Lamina holds nothing."
                : "Needs Glamourer and a logged-in character.");
    }

    /// <summary> Tries on every equippable affected item; the last item per slot wins. </summary>
    private string TryOnAll(ModInformation mod)
    {
        var host  = _module.Host;
        var items = mod.AffectedItems.Where(i => i.ItemId is not null && ModClassifier.IsEquippable(i.Category)).ToList();
        if (items.Count == 0)
            return "No equippable items in this mod.";
        if (!host.CanTryOn)
            return "Try-on needs Glamourer and a logged-in character.";

        var ok = 0;
        string? last = null;
        foreach (var item in items)
        {
            last = host.TryOnItem(item.ItemId!.Value);
            if (last.StartsWith("Equipped", StringComparison.Ordinal))
                ok++;
        }

        return ok == items.Count ? $"Equipped {ok} item(s)." : $"Equipped {ok} of {items.Count}: {last}";
    }

    private void DrawAffectedChips(IReadOnlyList<AffectedItem> items)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var shown = 0;
        var groups = items.GroupBy(i => i.Category).OrderBy(g => g.Key).ToList();
        foreach (var group in groups)
        {
            var names = group.Select(i => i.Name).Take(2).ToList();
            var extra = group.Count() - names.Count;
            var text  = $"{ModClassifier.Label(group.Key)} · {string.Join(", ", names)}{(extra > 0 ? $" +{extra}" : string.Empty)}";
            if (shown > 0 && ImGui.GetCursorPosX() + ImGui.CalcTextSize(text).X + 24 * scale < ImGui.GetWindowContentRegionMax().X)
                ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Button, ChipOffBg))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ChipOffBg))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive, ChipOffBg))
                ImGui.SmallButton(text);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", group.Select(i => i.Name)));
            shown++;
        }
    }

    private void DrawBaselineCard(ModInformation mod, VitrineDesign auto)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var id = ImRaii.PushId("baseline");
        using var frame = new Frame(true);

        ImGui.TextUnformatted(auto.Name);
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 70 * scale);
        ImGui.TextColored(Color(Accent), "Baseline");
        ImGui.TextColored(Color(MutedText), $"id {auto.Id}");

        var draft = _baselineDrafts.TryGetValue(mod.ModDirectory, out var d) ? d : null;
        var stored = _module.DesignStore.BaselineFor(mod.ModDirectory);
        if (draft is null)
        {
            DrawSettingsSummary(mod, stored?.Settings, stored?.Priority ?? 0);
            if (ImGui.SmallButton("Edit baseline"))
                _baselineDrafts[mod.ModDirectory] = new Baseline
                {
                    Priority = stored?.Priority ?? 0,
                    Settings = stored?.Settings?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()) ?? DefaultSettingsFor(mod),
                };
            return;
        }

        var priority = draft.Priority;
        ImGui.SetNextItemWidth(100 * scale);
        if (ImGui.InputInt("Priority", ref priority))
            draft.Priority = priority;

        draft.Settings ??= DefaultSettingsFor(mod);
        DrawSettingsEditor(mod, draft.Settings);

        if (ImGui.SmallButton("Save"))
        {
            _module.SetBaseline(mod.ModDirectory, draft);
            _baselineDrafts.Remove(mod.ModDirectory);
            SetStatus("Baseline saved.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Use Penumbra defaults"))
        {
            _module.SetBaseline(mod.ModDirectory, null);
            _baselineDrafts.Remove(mod.ModDirectory);
            SetStatus("Baseline reset to Penumbra defaults.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
            _baselineDrafts.Remove(mod.ModDirectory);
    }

    private void DrawVariantCard(ModInformation mod, VitrineDesign variant)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var id = ImRaii.PushId(variant.Id.ToString());
        using var frame = new Frame(false);

        var state = variant.Mods.Count > 0 ? variant.Mods[0] : null;
        var editing = _editingVariant == variant.Id;
        if (!editing)
        {
            ImGui.TextUnformatted(variant.Name);
            ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 60 * scale);
            ImGui.TextColored(Color(MutedText), "Variant");
            if (_module.DesignStore.IsOrphan(variant))
                ImGui.TextColored(Color(WarnText), "mod missing");
            DrawSettingsSummary(mod, state?.Settings, state?.Priority ?? 0, state?.Enabled ?? true);
            if (ImGui.SmallButton("Edit"))
            {
                _editingVariant = variant.Id;
                _variantDrafts[variant.Id] = new StoredVariantDraft
                {
                    Name     = variant.Name,
                    Enabled  = state?.Enabled ?? true,
                    Priority = state?.Priority ?? 0,
                    Settings = state?.Settings?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()) ?? DefaultSettingsFor(mod),
                };
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Copy id"))
            {
                ImGui.SetClipboardText(variant.Id.ToString());
                SetStatus("Variant id copied.");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Preview"))
                SetStatus(_module.PreviewDesign(variant));
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete") && ImGui.GetIO().KeyCtrl)
            {
                _module.DeleteVariant(variant.Id);
                SetStatus("Variant deleted.");
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Ctrl and click to delete.");
            return;
        }

        var draft = _variantDrafts[variant.Id];
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputText("##name", ref draft.Name, 128);
        ImGui.Checkbox("Enabled", ref draft.Enabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * scale);
        ImGui.InputInt("Priority", ref draft.Priority);
        DrawSettingsEditor(mod, draft.Settings);
        if (ImGui.SmallButton("Save"))
        {
            _module.UpdateVariant(variant.Id, draft.Name, draft.Enabled, draft.Priority, draft.Settings);
            _editingVariant = null;
            _variantDrafts.Remove(variant.Id);
            SetStatus("Variant saved.");
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Cancel"))
        {
            _editingVariant = null;
            _variantDrafts.Remove(variant.Id);
        }
    }

    private void DrawSettingsSummary(ModInformation mod, Dictionary<string, List<string>>? settings, int priority, bool enabled = true)
    {
        var parts = new List<string>();
        if (!enabled)
            parts.Add("disabled");
        if (priority != 0)
            parts.Add($"priority {priority}");
        if (settings is null)
            parts.Add(mod.AvailableSettings.Count == 0 ? "no options" : "Penumbra defaults");
        else
            foreach (var (group, options) in settings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                parts.Add(options.Count == 0 ? $"{group}: none" : $"{group}: {string.Join(", ", options)}");
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Color(MutedText), string.Join(" · ", parts));
        ImGui.PopTextWrapPos();
    }

    /// <summary> Option group editor: single-select groups as a row of exclusive buttons, multi-select groups as toggles. </summary>
    private void DrawSettingsEditor(ModInformation mod, Dictionary<string, List<string>> settings)
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (mod.AvailableSettings.Count == 0)
        {
            ImGui.TextColored(Color(MutedText), "This mod has no options.");
            return;
        }

        foreach (var (group, (options, groupType)) in mod.AvailableSettings.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            using var id = ImRaii.PushId(group);
            ImGui.TextColored(Color(MutedText), group);
            if (!settings.TryGetValue(group, out var selected))
                settings[group] = selected = new List<string>();

            var single = groupType == (int)GroupType.Single;
            var x = 0f;
            foreach (var option in options)
            {
                var on   = selected.Contains(option);
                var size = ImGui.CalcTextSize(option).X + 16 * scale;
                if (x > 0 && x + size < ImGui.GetContentRegionAvail().X)
                    ImGui.SameLine();
                else
                    x = 0;
                x += size + ImGui.GetStyle().ItemSpacing.X;

                using (ImRaii.PushColor(ImGuiCol.Button, on ? ChipOnBg : ChipOffBg))
                using (ImRaii.PushColor(ImGuiCol.Text, on ? Accent : ChipText))
                {
                    if (ImGui.SmallButton(option))
                    {
                        if (single)
                        {
                            selected.Clear();
                            selected.Add(option);
                        }
                        else if (on)
                        {
                            selected.Remove(option);
                        }
                        else
                        {
                            selected.Add(option);
                        }
                    }
                }
            }
        }
    }

    private static Dictionary<string, List<string>> DefaultSettingsFor(ModInformation mod)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var (group, (options, groupType)) in mod.AvailableSettings)
            result[group] = groupType == (int)GroupType.Single && options.Length > 0 ? new List<string> { options[0] } : new List<string>();
        return result;
    }

    // ---------------------------------------------------------------------------------------------------- collections tab

    private void DrawCollectionsTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        _collections ??= CollectionsView.Build(_module);
        var view = _collections;

        var rightW = 300 * scale;
        using (var left = ImRaii.Child("##colLeft", new Vector2(ImGui.GetContentRegionAvail().X - rightW - ImGui.GetStyle().ItemSpacing.X, 0), false))
        {
            if (left)
            {
                using (new Frame(true))
                {
                    {
                        ImGui.TextColored(Color(Accent), view.LaminaName ?? _module.Host.TemporarySource);
                        ImGui.SameLine();
                        ImGui.TextColored(Color(MutedText), view.LaminaId is null ? "not found in Penumbra" : "Managed by Lamina");
                        ImGui.PushTextWrapPos();
                        ImGui.TextUnformatted("The only collection Lamina writes to. Virtual designs set mod states here; your other collections are never touched.");
                        ImGui.PopTextWrapPos();
                        using var table = ImRaii.Table("##laminaCells", 3, ImGuiTableFlags.SizingStretchSame);
                        if (table)
                        {
                            ImGui.TableNextColumn();
                            ImGui.TextColored(Color(MutedText), "Assigned to");
                            ImGui.TextUnformatted(view.LaminaAssignedToPlayer ? "Yourself" : "Not you");
                            if (!view.LaminaAssignedToPlayer && ImGui.IsItemHovered())
                                ImGui.SetTooltip("Assign your character to the Lamina collection in Penumbra (Collections → Individual Assignments). Lamina never assigns or unassigns collections itself.");
                            ImGui.TableNextColumn();
                            ImGui.TextColored(Color(MutedText), "Mods set by Lamina");
                            ImGui.TextUnformatted(view.LaminaTemporaryCount.ToString());
                            ImGui.TableNextColumn();
                            ImGui.TextColored(Color(MutedText), "Mods enabled");
                            ImGui.TextUnformatted(view.LaminaEnabledCount.ToString());
                        }
                    }
                }

                ImGui.Spacing();
                SectionLabel("YOUR OTHER COLLECTIONS (READ ONLY)");
                using var others = ImRaii.Table("##collections", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
                if (others)
                {
                    ImGui.TableSetupColumn("Collection", ImGuiTableColumnFlags.WidthStretch, 2);
                    ImGui.TableSetupColumn("Assigned to", ImGuiTableColumnFlags.WidthStretch, 2);
                    ImGui.TableSetupColumn("Mods enabled", ImGuiTableColumnFlags.WidthStretch, 1);
                    ImGui.TableHeadersRow();
                    foreach (var row in view.Rows)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(row.IsLamina ? $"★ {row.Name}" : row.Name);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(row.Id.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(row.AssignedTo.Count == 0 ? "—" : string.Join(", ", row.AssignedTo));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(row.EnabledCount is { } n ? n.ToString() : "?");
                    }
                }
            }
        }

        ImGui.SameLine();
        using var right = ImRaii.Child("##colRight", new Vector2(rightW, 0), false);
        if (!right)
            return;

        SectionLabel("WHO IS USING WHAT RIGHT NOW");
        using (var table = ImRaii.Table("##usage", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                foreach (var (who, collection) in view.Usage)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(who);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Color(collection == view.LaminaName ? Accent : ChipText), collection ?? "—");
                }
            }
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Color(WarnText), "Something looks off? Unassign your character from the Lamina collection in Penumbra to see your normal setup. If it's still wrong, it's not Lamina. "
                                         + "Lamina never assigns or unassigns collections; you do that in Penumbra.");
        ImGui.PopTextWrapPos();
        if (ImGui.SmallButton("Refresh"))
        {
            _module.RequestCollectionRefresh();
            _collections = CollectionsView.Build(_module);
        }
    }

    // ---------------------------------------------------------------------------------------------------- settings tab

    private void DrawSettingsTab()
    {
        var scale    = ImGuiHelpers.GlobalScale;
        var settings = _module.Settings;
        var changed  = false;

        SectionLabel("CARDS");
        var width = settings.CardWidth;
        ImGui.SetNextItemWidth(240 * scale);
        if (ImGui.SliderFloat("Card width", ref width, 160, 420, "%.0f px"))
        {
            settings.CardWidth = width;
            changed = true;
        }

        var showImages = settings.ShowImages;
        if (ImGui.Checkbox("Show preview images", ref showImages))
        {
            settings.ShowImages = showImages;
            changed = true;
        }

        var showUnsorted = settings.ShowUnsorted;
        if (ImGui.Checkbox("Show mods without identified items", ref showUnsorted))
        {
            settings.ShowUnsorted = showUnsorted;
            changed = true;
        }

        ImGui.Spacing();
        SectionLabel("PHOTOS");
        var fetch = settings.FetchPhotosFromModPages;
        if (ImGui.Checkbox("Fetch display photos from mod pages", ref fetch))
        {
            settings.FetchPhotosFromModPages = fetch;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When a mod folder has no preview image, Vitrine loads the mod's website once and downloads its link-preview image (og:image). "
                           + "Only the page listed in the mod's metadata is contacted. Failures are retried after a week.");

        var photoDir = _module.Photos.PhotosDirectory;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputText("##photoDir", ref photoDir, 1024, ImGuiInputTextFlags.ReadOnly);
        var counters = _module.Photos.Counters;
        ImGui.TextColored(Color(MutedText), $"{counters.Cached} downloaded · {counters.Local} from mod folders · {counters.Pending} pending · {counters.Failed} without a photo");

        if (ImGui.Button("Fetch all missing photos"))
        {
            var queued = _module.Photos.FetchAllMissing(_module.Catalog.Mods);
            SetStatus(queued == 0 ? "Nothing to fetch." : $"Looking up {queued} photo(s) in the background…");
        }
        ImGui.SameLine();
        var ctrl = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!ctrl))
        {
            if (ImGui.Button("Clear photo cache"))
            {
                _module.Photos.ClearCache();
                SetStatus("Photo cache cleared.");
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl to delete every downloaded photo and forget what was looked up.");
        if (_statusMessage.Length > 0 && DateTime.UtcNow < _statusUntil)
            ImGui.TextColored(Color(Accent), _statusMessage);

        ImGui.Spacing();
        SectionLabel("DATA");
        var dir = _module.Host.DataDirectory;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputText("##dataDir", ref dir, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.TextColored(Color(MutedText), "mods.json (catalog cache), designs.json (baselines and variants), settings.json, photos/ (display photos).");

        ImGui.Spacing();
        SectionLabel("PENUMBRA");
        var reader = _module.Reader;
        ImGui.TextUnformatted(reader.Available
            ? $"Connected (API {reader.ApiVersion?.Breaking}.{reader.ApiVersion?.Feature}), {_module.Catalog.Mods.Count} mods catalogued."
            : "Not available.");
        if (_module.Catalog.IsScanning)
        {
            var (done, total) = _module.Catalog.ScanProgress;
            ImGui.ProgressBar(total == 0 ? 0 : (float)done / total, new Vector2(300 * scale, 0), $"{done}/{total}");
        }

        if (ImGui.Button("Rescan changed mods"))
            _module.Refresh(force: false);
        ImGui.SameLine();
        if (ImGui.Button("Force full rescan"))
            _module.Refresh(force: true);

        if (changed)
        {
            _module.SaveSettings();
            _visibleVersion = -1;
        }
    }

    // ---------------------------------------------------------------------------------------------------- about tab

    private void DrawAboutTab()
    {
        ImGui.TextColored(Color(Accent), "Vitrine");
        ImGui.TextUnformatted("Part of Lamina, by ahadev.");
        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        ImGui.TextUnformatted("Vitrine is a read focused view of your Penumbra mods. Every mod is catalogued by what it changes and gets one auto design "
                            + "(with the baseline settings you choose). Variants let you keep the same mod with different options. Lamina passes reference "
                            + "these designs by id through the Vitrine Design layer; Lamina then writes the resulting mod states into its own collection "
                            + "and never touches your other collections or your mod folders.");
        ImGui.PopTextWrapPos();
    }

    // ---------------------------------------------------------------------------------------------------- helpers

    private void DrawChip(string label, bool selected, Action onClick)
    {
        using (ImRaii.PushColor(ImGuiCol.Button, selected ? ChipOnBg : ChipOffBg))
        using (ImRaii.PushColor(ImGuiCol.Text, selected ? Accent : ChipText))
        using (ImRaii.PushColor(ImGuiCol.Border, selected ? Accent : AccentDim))
        {
            if (ImGui.SmallButton(label))
                onClick();
        }
    }

    private static void SectionLabel(string text)
        => ImGui.TextColored(Color(MutedText), text);

    private void SetStatus(string message)
    {
        _statusMessage = message;
        _statusUntil   = DateTime.UtcNow.AddSeconds(6);
    }

    private static string SortLabel(ModSort sort)
        => sort switch
        {
            ModSort.Author          => "Author",
            ModSort.Category        => "Category",
            ModSort.RecentlyScanned => "Recently added",
            _                       => "Name",
        };

    private static string StateLabel(StateFilter filter)
        => filter switch
        {
            StateFilter.OnInLamina  => "On in Lamina",
            StateFilter.OffInLamina => "Off in Lamina",
            _                       => "Any",
        };

    private static string Ellipsis(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        var len = text.Length;
        while (len > 1 && ImGui.CalcTextSize(text[..len] + "…").X > maxWidth)
            len--;
        return text[..len] + "…";
    }

    private static (Vector2 Min, Vector2 Max) CoverUv(int texW, int texH, float boxW, float boxH)
    {
        if (texW <= 0 || texH <= 0 || boxW <= 0 || boxH <= 0)
            return (Vector2.Zero, Vector2.One);

        var texAspect = (float)texW / texH;
        var boxAspect = boxW / boxH;
        if (texAspect > boxAspect)
        {
            var w = boxAspect / texAspect;
            return (new Vector2((1 - w) / 2, 0), new Vector2((1 + w) / 2, 1));
        }

        var h = texAspect / boxAspect;
        return (new Vector2(0, (1 - h) / 2), new Vector2(1, (1 + h) / 2));
    }

    private static uint CategoryColor(VitrineCategory category)
        => category switch
        {
            VitrineCategory.Gear      => Rgba(0x2E, 0x26, 0x44, 0xFF),
            VitrineCategory.Weapon    => Rgba(0x44, 0x2A, 0x2A, 0xFF),
            VitrineCategory.Accessory => Rgba(0x3A, 0x2E, 0x44, 0xFF),
            VitrineCategory.Hair      => Rgba(0x2C, 0x27, 0x3A, 0xFF),
            VitrineCategory.Face      => Rgba(0x1F, 0x33, 0x3A, 0xFF),
            VitrineCategory.Body or VitrineCategory.Skin => Rgba(0x3A, 0x2C, 0x27, 0xFF),
            VitrineCategory.Tail or VitrineCategory.Ears => Rgba(0x33, 0x2A, 0x33, 0xFF),
            VitrineCategory.Animation or VitrineCategory.Emote => Rgba(0x23, 0x35, 0x2B, 0xFF),
            VitrineCategory.Vfx       => Rgba(0x3A, 0x33, 0x22, 0xFF),
            VitrineCategory.Sfx       => Rgba(0x26, 0x2C, 0x33, 0xFF),
            VitrineCategory.Ui        => Rgba(0x22, 0x30, 0x3A, 0xFF),
            VitrineCategory.Minion or VitrineCategory.Mount => Rgba(0x30, 0x3A, 0x22, 0xFF),
            VitrineCategory.Housing   => Rgba(0x3A, 0x2F, 0x22, 0xFF),
            VitrineCategory.Npc       => Rgba(0x2A, 0x2A, 0x2A, 0xFF),
            _                         => Rgba(0x26, 0x26, 0x2B, 0xFF),
        };

    private static uint Rgba(byte r, byte g, byte b, byte a)
        => (uint)(r | (g << 8) | (b << 16) | (a << 24));

    private static Vector4 Color(uint rgba)
        => ImGui.ColorConvertU32ToFloat4(rgba);

    public void Dispose()
    { }

    /// <summary> A rounded, bordered box around a group of widgets, sized to its content. </summary>
    private readonly struct Frame : IDisposable
    {
        private readonly bool _accent;
        private readonly Vector2 _origin;
        private readonly float _pad;

        public Frame(bool accent)
        {
            _accent = accent;
            _pad    = 8 * ImGuiHelpers.GlobalScale;
            _origin = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().ChannelsSplit(2);
            ImGui.GetWindowDrawList().ChannelsSetCurrent(1);
            ImGui.SetCursorScreenPos(_origin + new Vector2(_pad, _pad));
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowPos().X - _pad);
        }

        public void Dispose()
        {
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
            var max  = ImGui.GetItemRectMax() + new Vector2(_pad, _pad);
            var min  = _origin;
            max.X    = Math.Max(max.X, ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X);
            var draw = ImGui.GetWindowDrawList();
            draw.ChannelsSetCurrent(0);
            draw.AddRectFilled(min, max, CardBg, 6 * ImGuiHelpers.GlobalScale);
            draw.AddRect(min, max, _accent ? Accent : AccentDim, 6 * ImGuiHelpers.GlobalScale);
            draw.ChannelsMerge();
            ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y + ImGui.GetStyle().ItemSpacing.Y));
            ImGui.Dummy(Vector2.Zero);
        }
    }

    private enum StateFilter
    {
        Any,
        OnInLamina,
        OffInLamina,
    }

    private sealed class StoredVariantDraft
    {
        public string Name = string.Empty;
        public bool Enabled = true;
        public int Priority;
        public Dictionary<string, List<string>> Settings = new();
    }

    /// <summary> Snapshot of collection assignments for the Collections tab. Built on demand, cheap. </summary>
    private sealed class CollectionsView
    {
        public Guid? LaminaId;
        public string? LaminaName;
        public bool LaminaAssignedToPlayer;
        public int LaminaTemporaryCount;
        public int LaminaEnabledCount;
        public List<Row> Rows = new();
        public List<(string Who, string? Collection)> Usage = new();

        public sealed class Row
        {
            public Guid Id;
            public string Name = string.Empty;
            public bool IsLamina;
            public List<string> AssignedTo = new();
            public int? EnabledCount;
        }

        public static CollectionsView Build(VitrineModule module)
        {
            var view   = new CollectionsView();
            var reader = module.Reader;
            if (!reader.Available)
                return view;

            var collections = reader.GetCollections();
            view.LaminaId   = module.Host.LaminaCollectionId;
            view.LaminaName = view.LaminaId is { } lid && collections.TryGetValue(lid, out var name) ? name : null;

            var assigned = new Dictionary<Guid, List<string>>();
            void Assign(Guid id, string who)
            {
                if (!assigned.TryGetValue(id, out var list))
                    assigned[id] = list = new List<string>();
                list.Add(who);
            }

            var player = reader.GetCollectionForObject(0);
            if (player is { } p)
            {
                Assign(p.Id, "Yourself");
                view.LaminaAssignedToPlayer = p.Id == view.LaminaId;
                view.Usage.Add(("Yourself", p.Name));
            }
            else
            {
                view.Usage.Add(("Yourself", null));
            }

            foreach (var (type, label) in new[] { (ApiCollectionType.Default, "Base"), (ApiCollectionType.Interface, "Interface"), (ApiCollectionType.Current, "Current"),
                         (ApiCollectionType.MalePlayerCharacter, "Male players"), (ApiCollectionType.FemalePlayerCharacter, "Female players"),
                         (ApiCollectionType.MaleNonPlayerCharacter, "Male NPCs"), (ApiCollectionType.FemaleNonPlayerCharacter, "Female NPCs") })
            {
                var special = reader.GetSpecialCollection(type);
                if (special is { } s)
                {
                    Assign(s.Id, label);
                    if (type is ApiCollectionType.Default or ApiCollectionType.Interface or ApiCollectionType.Current)
                        view.Usage.Add((label, s.Name));
                }
                else if (type is ApiCollectionType.Default or ApiCollectionType.Interface or ApiCollectionType.Current)
                {
                    view.Usage.Add((label, null));
                }
            }

            foreach (var (id, collectionName) in collections.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
            {
                var row = new Row { Id = id, Name = collectionName, IsLamina = id == view.LaminaId };
                if (assigned.TryGetValue(id, out var who))
                    row.AssignedTo = who;

                var all = reader.GetAllModSettings(id);
                if (all is not null)
                {
                    row.EnabledCount = all.Count(kv => kv.Value.Enabled);
                    if (row.IsLamina)
                    {
                        view.LaminaEnabledCount   = row.EnabledCount.Value;
                        view.LaminaTemporaryCount = all.Count(kv => kv.Value.Temporary);
                    }
                }

                view.Rows.Add(row);
            }

            return view;
        }
    }
}
