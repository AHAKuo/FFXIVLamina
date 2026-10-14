using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lamina.Models;
using Lamina.Runtime.State;

namespace Lamina.UI;

/// <summary> Shared editor for a <see cref="ConditionStack"/>: tree of All/Any groups and leaf conditions with per-kind field editors. </summary>
internal sealed class ConditionEditor
{
    private readonly LaminaServices _s;
    private readonly UiCaches _caches;

    private static readonly string[] GroupOpNames = { "All of", "Any of" };

    private static readonly IReadOnlyList<Widgets.PickItem<byte>> Races = new[]
    {
        new Widgets.PickItem<byte>(1, "Hyur"), new Widgets.PickItem<byte>(2, "Elezen"), new Widgets.PickItem<byte>(3, "Lalafell"),
        new Widgets.PickItem<byte>(4, "Miqo'te"), new Widgets.PickItem<byte>(5, "Roegadyn"), new Widgets.PickItem<byte>(6, "Au Ra"),
        new Widgets.PickItem<byte>(7, "Hrothgar"), new Widgets.PickItem<byte>(8, "Viera"),
    };

    private static readonly IReadOnlyList<Widgets.PickItem<byte>> Clans = new[]
    {
        new Widgets.PickItem<byte>(1, "Midlander", "Hyur"), new Widgets.PickItem<byte>(2, "Highlander", "Hyur"),
        new Widgets.PickItem<byte>(3, "Wildwood", "Elezen"), new Widgets.PickItem<byte>(4, "Duskwight", "Elezen"),
        new Widgets.PickItem<byte>(5, "Plainsfolk", "Lalafell"), new Widgets.PickItem<byte>(6, "Dunesfolk", "Lalafell"),
        new Widgets.PickItem<byte>(7, "Seeker of the Sun", "Miqo'te"), new Widgets.PickItem<byte>(8, "Keeper of the Moon", "Miqo'te"),
        new Widgets.PickItem<byte>(9, "Sea Wolf", "Roegadyn"), new Widgets.PickItem<byte>(10, "Hellsguard", "Roegadyn"),
        new Widgets.PickItem<byte>(11, "Raen", "Au Ra"), new Widgets.PickItem<byte>(12, "Xaela", "Au Ra"),
        new Widgets.PickItem<byte>(13, "Helions", "Hrothgar"), new Widgets.PickItem<byte>(14, "The Lost", "Hrothgar"),
        new Widgets.PickItem<byte>(15, "Rava", "Viera"), new Widgets.PickItem<byte>(16, "Veena", "Viera"),
    };

    private static readonly IReadOnlyList<Widgets.PickItem<byte>> Genders = new[]
    {
        new Widgets.PickItem<byte>(0, "Male"), new Widgets.PickItem<byte>(1, "Female"),
    };

    private static readonly IReadOnlyList<Widgets.PickItem<int>> Gearsets =
        Enumerable.Range(0, 100).Select(i => new Widgets.PickItem<int>(i, $"Gearset {i + 1}")).ToList();

    private static readonly IReadOnlyList<string> EventNames =
        StaticTables.SeasonalEvents.Select(e => e.Name).ToList();

    public ConditionEditor(LaminaServices services, UiCaches caches)
    {
        _s      = services;
        _caches = caches;
    }

    /// <summary> Draws the editor. Returns true when anything changed (caller marks the profile dirty). </summary>
    public bool Draw(ConditionStack stack, string id)
    {
        using var scope = ImRaii.PushId(id);
        var root = stack.EnsureRootGroup();
        var changed = DrawGroup(root, isRoot: true, out var removeRoot);
        if (removeRoot)
        {
            stack.Root = new ConditionGroup();
            changed = true;
        }
        return changed;
    }

    private bool DrawGroup(ConditionGroup group, bool isRoot, out bool remove)
    {
        remove = false;
        var changed = false;
        using var scope = ImRaii.PushId(group.Id.ToString());

        var op = group.Op == GroupOp.All ? 0 : 1;
        ImGui.SetNextItemWidth(90 * Widgets.Scale);
        if (ImGui.Combo("##op", ref op, GroupOpNames, GroupOpNames.Length))
        {
            group.Op = op == 0 ? GroupOp.All : GroupOp.Any;
            changed  = true;
        }
        Widgets.Tooltip("All of: every child must be true.\nAny of: at least one child must be true.\nAn empty group is true.");
        ImGui.SameLine();
        var negate = group.Negate;
        if (ImGui.Checkbox("Not##g", ref negate))
        {
            group.Negate = negate;
            changed      = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("+ Add Condition"))
            ImGui.OpenPopup("##addCondition");
        ImGui.SameLine();
        if (ImGui.Button("+ Sub-group"))
        {
            group.Children.Add(new ConditionGroup());
            changed = true;
        }
        if (!isRoot)
        {
            ImGui.SameLine();
            if (Widgets.IconButton(FontAwesomeIcon.Trash, "Remove this sub-group"))
                remove = true;
        }
        else if (group.Children.Count == 0)
        {
            ImGui.SameLine();
            Widgets.Hint("(no conditions: always true)");
        }

        using (var popup = ImRaii.Popup("##addCondition"))
        {
            if (popup)
            {
                foreach (var (kind, display, create) in ConditionKinds.All)
                {
                    if (!ImGui.Selectable(display))
                        continue;
                    var c = create();
                    group.Children.Add(c);
                    changed = true;
                    _s.Log.Debug("UI: added condition {Kind}.", kind);
                }
            }
        }

        using var indent = ImRaii.PushIndent(14f * Widgets.Scale);
        var removeAt = -1;
        for (var i = 0; i < group.Children.Count; i++)
        {
            var child = group.Children[i];
            using var cid = ImRaii.PushId(i);
            switch (child)
            {
                case ConditionGroup sub:
                {
                    using var subIndent = ImRaii.PushIndent(10f * Widgets.Scale);
                    ImGui.TextColored(Widgets.Accent, "Sub-group");
                    ImGui.SameLine();
                    if (DrawGroup(sub, false, out var removeSub))
                        changed = true;
                    if (removeSub)
                        removeAt = i;
                    break;
                }
                case Condition leaf:
                    if (DrawLeaf(leaf, out var removeLeaf))
                        changed = true;
                    if (removeLeaf)
                        removeAt = i;
                    break;
            }
        }
        if (removeAt >= 0)
        {
            group.Children.RemoveAt(removeAt);
            changed = true;
        }
        return changed;
    }

    private bool DrawLeaf(Condition c, out bool remove)
    {
        remove = false;
        var changed = false;
        using var scope = ImRaii.PushId(c.Id.ToString());

        var enabled = c.Enabled;
        if (ImGui.Checkbox("##en", ref enabled))
        {
            c.Enabled = enabled;
            changed   = true;
        }
        Widgets.Tooltip(enabled ? "Enabled" : "Disabled (ignored)");
        ImGui.SameLine();

        var trueNow = TrueNow(c);
        ImGui.TextColored(trueNow ? Widgets.Good : Widgets.Muted, trueNow ? "●" : "○");
        Widgets.Tooltip(trueNow ? "True right now (before Not / hold / linger)" : "False right now");
        ImGui.SameLine();

        var display = ConditionKinds.All.FirstOrDefault(k => k.Kind == c.Kind).DisplayName ?? c.Kind;
        var header  = (c.Negate ? "NOT " : string.Empty) + c.Describe();
        using (ImRaii.PushColor(ImGuiCol.Text, c.Enabled ? new Vector4(1, 1, 1, 1) : Widgets.Muted))
        using (var node = ImRaii.TreeNode($"{header}###node"))
        {
            ImGui.SameLine();
            Widgets.Hint($"[{display}]");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 26 * Widgets.Scale);
            if (Widgets.IconButton(FontAwesomeIcon.Trash, "Remove condition"))
                remove = true;
            if (!node)
                return changed;

            using var indent = ImRaii.PushIndent(8f * Widgets.Scale);
            if (DrawFields(c))
                changed = true;

            ImGui.Spacing();
            var negate = c.Negate;
            if (ImGui.Checkbox("Not (invert)", ref negate))
            {
                c.Negate = negate;
                changed  = true;
            }
            ImGui.SameLine();
            var hold = c.HoldSeconds;
            if (Widgets.NullableSeconds("Hold", ref hold, _s.Profiles.Config.DefaultHoldSeconds))
            {
                c.HoldSeconds = hold;
                changed       = true;
            }
            Widgets.Tooltip("Must stay true this long before it counts. Prevents flicker from short states.");
            ImGui.SameLine();
            var linger = c.LingerSeconds;
            if (Widgets.NullableSeconds("Linger", ref linger, _s.Profiles.Config.DefaultLingerSeconds))
            {
                c.LingerSeconds = linger;
                changed         = true;
            }
            Widgets.Tooltip("Stays true this long after it stopped being true.");
        }
        return changed;
    }

    private bool TrueNow(Condition c)
    {
        try
        {
            return c.Matches(_s.Engine.Snapshot);
        }
        catch
        {
            return false;
        }
    }

    private bool DrawFields(Condition c)
        => c switch
        {
            TimeCondition t         => DrawTime(t),
            CalendarCondition cal   => DrawCalendar(cal),
            WeatherCondition w      => DrawWeather(w),
            BiomeCondition b        => Widgets.StringList("Biomes##b", b.Biomes, "biome name", StaticTables.Biomes.All),
            LocationCondition l     => DrawLocation(l),
            DutyCondition d         => DrawDuty(d),
            HousingCondition h      => DrawHousing(h),
            SocialCondition so      => DrawSocial(so),
            CharacterCondition ch   => DrawCharacter(ch),
            JobCondition j          => DrawJob(j),
            OnlineStatusCondition o => Widgets.MultiSelect("Online status##os", _caches.OnlineStatuses, o.StatusIds, id => _s.Catalog.OnlineStatusName(id)),
            StatusEffectCondition s => DrawStatusEffect(s),
            MovementCondition m     => Widgets.EnumMultiSelect("Movement states##mv", m.States),
            BodyStateCondition bs   => DrawBodyState(bs),
            EmoteCondition e        => DrawEmote(e),
            ActivityCondition a     => Widgets.EnumMultiSelect("Activities##act", a.Activities),
            UnknownCondition u      => DrawUnknown(u),
            _                       => false,
        };

    private static bool DrawTime(TimeCondition t)
    {
        var changed = false;
        var clock   = t.Clock;
        if (Widgets.EnumCombo("Clock##time", ref clock, 90))
        {
            t.Clock = clock;
            changed = true;
        }
        Widgets.Tooltip("ET: Eorzea time. LT: your local clock. ST: server time (UTC).");
        var from = t.FromMinutes;
        if (Widgets.TimeInput("From##time", ref from)) { t.FromMinutes = from; changed = true; }
        var to = t.ToMinutes;
        if (Widgets.TimeInput("To##time", ref to)) { t.ToMinutes = to; changed = true; }
        Widgets.Hint("Ranges may wrap midnight (18:00 → 06:00). Equal From and To means all day.");
        return changed;
    }

    private static bool DrawCalendar(CalendarCondition cal)
    {
        var changed = false;
        var from    = cal.DateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        ImGui.SetNextItemWidth(120 * Widgets.Scale);
        if (ImGui.InputTextWithHint("From date##cal", "yyyy-mm-dd", ref from, 10))
        {
            cal.DateFrom = DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            changed      = true;
        }
        ImGui.SameLine();
        var to = cal.DateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        ImGui.SetNextItemWidth(120 * Widgets.Scale);
        if (ImGui.InputTextWithHint("To date##cal", "yyyy-mm-dd", ref to, 10))
        {
            cal.DateTo = DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            changed    = true;
        }
        if (Widgets.EnumMultiSelect("Weekdays##cal", cal.Weekdays))
            changed = true;
        ImGui.SetNextItemWidth(200 * Widgets.Scale);
        using (var combo = ImRaii.Combo("Seasonal event##cal", string.IsNullOrEmpty(cal.Event) ? "(none)" : cal.Event))
        {
            if (combo)
            {
                if (ImGui.Selectable("(none)", string.IsNullOrEmpty(cal.Event)))
                {
                    cal.Event = null;
                    changed   = true;
                }
                foreach (var ev in EventNames)
                    if (ImGui.Selectable(ev, ev == cal.Event))
                    {
                        cal.Event = ev;
                        changed   = true;
                    }
            }
        }
        Widgets.Hint("Event windows are approximate yearly dates.");
        return changed;
    }

    private bool DrawWeather(WeatherCondition w)
    {
        var changed = Widgets.EnumMultiSelect("Weather groups##w", w.Groups);
        if (Widgets.MultiSelect("Specific weathers##w", _caches.Weathers, w.WeatherIds, id => _s.Catalog.WeatherName(id)))
            changed = true;
        Widgets.Hint("True when the current weather is in any selected group or list.");
        return changed;
    }

    private bool DrawLocation(LocationCondition l)
    {
        var changed = Widgets.MultiSelect("Zones##loc", _caches.Territories, l.TerritoryIds, id => _s.Catalog.TerritoryName(id));
        if (Widgets.MultiSelect("Regions##loc", _caches.Regions, l.RegionIds))
            changed = true;
        if (Widgets.MultiSelect("Worlds##loc", _caches.Worlds, l.WorldIds, id => _s.Catalog.WorldName(id)))
            changed = true;
        var sanc = l.Sanctuary;
        if (Widgets.TriState("Sanctuary (rested area)##loc", ref sanc)) { l.Sanctuary = sanc; changed = true; }
        var ind = l.Indoors;
        if (Widgets.TriState("Indoors##loc", ref ind)) { l.Indoors = ind; changed = true; }
        var inn = l.InnRoom;
        if (Widgets.TriState("Inn room##loc", ref inn)) { l.InnRoom = inn; changed = true; }
        var home = l.HomeWorld;
        if (Widgets.TriState("On home world##loc", ref home)) { l.HomeWorld = home; changed = true; }
        return changed;
    }

    private bool DrawDuty(DutyCondition d)
    {
        var changed = Widgets.EnumMultiSelect("Content types##duty", d.ContentTypes);
        if (Widgets.MultiSelect("Specific duties##duty", _caches.Duties, d.ContentIds, id => _s.Catalog.DutyName(id)))
            changed = true;
        var fate = d.InFate;
        if (Widgets.TriState("In a FATE##duty", ref fate)) { d.InFate = fate; changed = true; }
        return changed;
    }

    private static bool DrawHousing(HousingCondition h)
    {
        var changed = false;
        var where   = h.Where;
        if (Widgets.EnumCombo("Where##h", ref where)) { h.Where = where; changed = true; }
        if (Widgets.EnumMultiSelect("Districts##h", h.Districts))
            changed = true;
        var ward = h.Ward;
        if (Widgets.NullableInt("Ward##h", ref ward, 1, 1, 30)) { h.Ward = ward; changed = true; }
        ImGui.SameLine();
        var plot = h.Plot;
        if (Widgets.NullableInt("Plot##h", ref plot, 1, 1, 60)) { h.Plot = plot; changed = true; }
        var inside = h.Inside;
        if (Widgets.TriState("Inside a building##h", ref inside)) { h.Inside = inside; changed = true; }
        Widgets.Hint("Only true inside a housing district.");
        return changed;
    }

    private static bool DrawSocial(SocialCondition s)
    {
        var changed = false;
        var state   = s.PartyState;
        if (Widgets.EnumCombo("Party##soc", ref state)) { s.PartyState = state; changed = true; }
        var min = s.MinSize;
        if (Widgets.NullableInt("Min size##soc", ref min, 2, 1, 24)) { s.MinSize = min; changed = true; }
        ImGui.SameLine();
        var max = s.MaxSize;
        if (Widgets.NullableInt("Max size##soc", ref max, 8, 1, 24)) { s.MaxSize = max; changed = true; }
        if (Widgets.StringList("Players in party (Name@World)##soc", s.Players, "Name@World"))
            changed = true;
        return changed;
    }

    private static bool DrawCharacter(CharacterCondition c)
    {
        var changed = Widgets.MultiSelect("Races##ch", Races, c.Races);
        if (Widgets.MultiSelect("Clans##ch", Clans, c.Clans)) changed = true;
        if (Widgets.MultiSelect("Genders##ch", Genders, c.Genders)) changed = true;
        if (Widgets.StringList("Characters (Name@World)##ch", c.Characters, "Name@World")) changed = true;
        return changed;
    }

    private bool DrawJob(JobCondition j)
    {
        var changed = Widgets.MultiSelect("Jobs##job", _caches.Jobs, j.JobIds, id => _s.Catalog.JobAbbreviation(id));
        if (Widgets.EnumMultiSelect("Roles##job", j.Roles)) changed = true;
        if (Widgets.EnumMultiSelect("Disciplines##job", j.Disciplines)) changed = true;
        if (Widgets.MultiSelect("Gearsets##job", Gearsets, j.GearsetIds, id => $"Gearset {id + 1}")) changed = true;
        var min = j.LevelMin;
        if (Widgets.NullableInt("Level min##job", ref min, 1, 1, 100)) { j.LevelMin = min; changed = true; }
        ImGui.SameLine();
        var max = j.LevelMax;
        if (Widgets.NullableInt("Level max##job", ref max, 100, 1, 100)) { j.LevelMax = max; changed = true; }
        var drawn = j.WeaponDrawn;
        if (Widgets.TriState("Weapon drawn##job", ref drawn)) { j.WeaponDrawn = drawn; changed = true; }
        return changed;
    }

    private bool DrawStatusEffect(StatusEffectCondition s)
    {
        var changed = Widgets.MultiSelect("Status effects##se", _caches.Statuses, s.StatusIds, id => _s.Catalog.StatusName(id));
        var min = s.MinStacks;
        if (Widgets.NullableInt("Min stacks##se", ref min, 1, 1, 99)) { s.MinStacks = min; changed = true; }
        Widgets.Hint("No selection: true while any status effect is present.");
        return changed;
    }

    private static bool DrawBodyState(BodyStateCondition b)
    {
        var changed = Widgets.EnumMultiSelect("Body states##bs", b.States);
        var hp = b.HpBelowPercent;
        if (Widgets.NullableInt("HP below %##bs", ref hp, 25, 1, 100)) { b.HpBelowPercent = hp; changed = true; }
        return changed;
    }

    private bool DrawEmote(EmoteCondition e)
    {
        var changed = Widgets.EnumMultiSelect("Poses##em", e.Poses);
        if (Widgets.MultiSelect("Emotes##em", _caches.Emotes, e.EmoteIds, id => _s.Catalog.EmoteName(id))) changed = true;
        var loop = e.LoopingOnly;
        if (ImGui.Checkbox("Looping emotes only##em", ref loop)) { e.LoopingOnly = loop; changed = true; }
        Widgets.Hint("No selection: true while any emote plays.");
        return changed;
    }

    private static bool DrawUnknown(UnknownCondition u)
    {
        ImGui.TextColored(Widgets.Warn, $"Unsupported condition kind '{u.UnknownKind}' from a newer Lamina. It never matches and is kept as is.");
        var raw = u.RawJson;
        ImGui.InputTextMultiline("##raw", ref raw, 4096, new Vector2(-1, 80 * Widgets.Scale), ImGuiInputTextFlags.ReadOnly);
        return false;
    }
}
