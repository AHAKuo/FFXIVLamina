namespace Lamina.Decals.Mesh;

/// <summary>
/// Friendly names for the game's skeleton bones (<c>j_ude_a_l</c> → "left upper arm"). Covers the body, hands, feet, tail,
/// ears and the face; anything unknown (physics, weapon and most mod-added bones) falls back to the raw bone name.
/// </summary>
public static class BoneNames
{
    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal)
    {
        ["n_root"]      = "root",
        ["n_hara"]      = "pelvis",
        ["j_kosi"]      = "hips",
        ["j_sebo_a"]    = "lower spine",
        ["j_sebo_b"]    = "mid spine",
        ["j_sebo_c"]    = "upper spine",
        ["j_kubi"]      = "neck",
        ["j_kao"]       = "head",
        ["j_ago"]       = "jaw",
        ["n_sippo_a"]   = "tail base",
        ["n_sippo_b"]   = "tail",
        ["n_sippo_c"]   = "tail",
        ["n_sippo_d"]   = "tail",
        ["n_sippo_e"]   = "tail tip",
        ["j_f_hana"]    = "nose",
        ["j_f_ago"]     = "chin",
        ["j_f_uago"]    = "upper jaw",
        ["j_f_shita"]   = "tongue",
        ["j_f_memoto"]  = "between the eyes",
        ["j_f_ulip_a"]  = "upper lip",
        ["j_f_ulip_b"]  = "upper lip",
        ["j_f_dlip_a"]  = "lower lip",
        ["j_f_dlip_b"]  = "lower lip",
        ["j_f_face"]    = "face",
        ["iv_fukubu_phys"] = "belly",
    };

    /// <summary> Bones that exist as a left / right pair; keyed without the <c>_l</c> / <c>_r</c> suffix. </summary>
    private static readonly Dictionary<string, string> Sided = new(StringComparer.Ordinal)
    {
        ["j_sako"]    = "clavicle",
        ["n_hkata"]   = "shoulder",
        ["j_ude_a"]   = "upper arm",
        ["j_ude_b"]   = "forearm",
        ["n_hhiji"]   = "elbow",
        ["n_hte"]     = "wrist",
        ["j_te"]      = "hand",
        ["j_oya_a"]   = "thumb",
        ["j_oya_b"]   = "thumb tip",
        ["j_hito_a"]  = "index finger",
        ["j_hito_b"]  = "index fingertip",
        ["j_naka_a"]  = "middle finger",
        ["j_naka_b"]  = "middle fingertip",
        ["j_kusu_a"]  = "ring finger",
        ["j_kusu_b"]  = "ring fingertip",
        ["j_ko_a"]    = "little finger",
        ["j_ko_b"]    = "little fingertip",
        ["j_mune"]    = "chest",
        ["j_asi_a"]   = "thigh",
        ["j_asi_b"]   = "calf",
        ["j_asi_c"]   = "foot",
        ["j_asi_d"]   = "toes",
        ["j_asi_e"]   = "toe tip",
        ["n_hhiza"]   = "knee",
        ["j_mimi"]    = "ear",
        ["j_zera_a"]  = "ear",
        ["j_zera_b"]  = "ear tip",
        ["j_f_mayu"]  = "eyebrow",
        ["j_f_miken"] = "brow",
        ["j_f_umab"]  = "upper eyelid",
        ["j_f_dmab"]  = "lower eyelid",
        ["j_f_mabup"] = "upper eyelid",
        ["j_f_mabdn"] = "lower eyelid",
        ["j_f_eye"]   = "eye",
        ["j_f_hoho"]  = "cheek",
        ["j_f_lip"]   = "mouth corner",
        ["j_f_hige"]  = "whiskers",
        ["j_ex_top_a"] = "chest",
        ["iv_shiri"]  = "buttock",
        ["iv_c_mune"] = "chest",
    };

    /// <summary> The friendly name of a bone, or the raw name when it is unknown; "unknown" for null / empty. </summary>
    public static string Friendly(string? bone)
    {
        if (string.IsNullOrEmpty(bone))
            return "unknown";

        if (Exact.TryGetValue(bone, out var exact))
            return exact;

        string? side = null;
        var     core = bone;
        if (bone.EndsWith("_l", StringComparison.Ordinal))
        {
            side = "left";
            core = bone[..^2];
        }
        else if (bone.EndsWith("_r", StringComparison.Ordinal))
        {
            side = "right";
            core = bone[..^2];
        }

        if (Sided.TryGetValue(core, out var name))
            return side is null ? name : $"{side} {name}";

        return bone;
    }
}
