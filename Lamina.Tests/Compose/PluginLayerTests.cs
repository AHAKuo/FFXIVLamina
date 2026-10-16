using Xunit;
using System.Numerics;
using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Tests.Fakes;
using Newtonsoft.Json.Linq;

namespace Lamina.Tests.Compose;

/// <summary> Resolving the Customize+ and Simple Heels layers, cascade order, and the JSON the bridges exchange with the plugins. </summary>
public sealed class PluginLayerTests
{
    private static readonly Provenance Src = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "pass / layer");

    private static CustomizePlusBone Bone(float scale, float rotY = 0f)
        => new(Vector3.Zero, new Vector3(0, rotY, 0), new Vector3(scale), Vector3.One, false, false, false, false);

    private static (FakeCustomizePlusBridge Bridge, Guid Profile) CustomizePlus(params (string Name, float Scale)[] bones)
    {
        var bridge = new FakeCustomizePlusBridge();
        var id     = bridge.AddProfile("Tall", bones.ToDictionary(b => b.Name, b => Bone(b.Scale), StringComparer.Ordinal));
        return (bridge, id);
    }

    // ---- Customize+ layer -----------------------------------------------------------------------------

    [Fact]
    public void CustomizePlus_ProfileBonesLandInTheDraft_WithProvenance()
    {
        var (bridge, profile) = CustomizePlus(("j_kosi", 1.2f), ("j_kubi", 0.9f));
        var resolver = Build.Resolver(new FakeGlamourerBridge(), customizePlus: bridge);

        var draft = resolver.Resolve(new CustomizePlusLayer { ProfileId = profile }, Src, out var skip);
        Assert.Null(skip);
        Assert.NotNull(draft);
        Assert.Equal(2, draft!.CustomizePlusBones.Count);
        Assert.Equal(1.2f, draft.CustomizePlusBones["j_kosi"].Value.Scaling.X);
        Assert.Equal(Src, draft.CustomizePlusBones["j_kubi"].Source);
        Assert.Null(draft.SimpleHeels);
        Assert.False(draft.IsEmpty);
    }

    [Fact]
    public void CustomizePlus_MissingSelectionPluginOrProfile_IsSkippedWithAReason()
    {
        var (bridge, profile) = CustomizePlus(("j_kosi", 1.2f));
        var resolver = Build.Resolver(new FakeGlamourerBridge(), customizePlus: bridge);

        Assert.Null(resolver.Resolve(new CustomizePlusLayer(), Src, out var noPick));
        Assert.Equal("no Customize+ profile selected", noPick);

        Assert.Null(resolver.Resolve(new CustomizePlusLayer { ProfileId = Guid.NewGuid(), ProfileName = "Gone" }, Src, out var missing));
        Assert.Contains("Gone", missing);
        Assert.Contains("not found", missing);

        bridge.Available = false;
        Assert.Null(resolver.Resolve(new CustomizePlusLayer { ProfileId = profile }, Src, out var unavailable));
        Assert.Equal("Customize+ is not installed", unavailable);

        var without = Build.Resolver(new FakeGlamourerBridge());
        Assert.Null(without.Resolve(new CustomizePlusLayer { ProfileId = profile }, Src, out var noBridge));
        Assert.Equal("Customize+ is not installed", noBridge);
    }

    [Fact]
    public void CustomizePlus_EmptyProfile_IsSkipped()
    {
        var bridge   = new FakeCustomizePlusBridge();
        var profile  = bridge.AddProfile("Empty", new Dictionary<string, CustomizePlusBone>());
        var resolver = Build.Resolver(new FakeGlamourerBridge(), customizePlus: bridge);

        Assert.Null(resolver.Resolve(new CustomizePlusLayer { ProfileId = profile }, Src, out var skip));
        Assert.Equal("Customize+ profile has no bone edits", skip);
    }

    [Fact]
    public void CustomizePlus_LaterPassOverridesSingleBones()
    {
        var early = new DesignDraft();
        early.CustomizePlusBones["j_kosi"] = new Field<CustomizePlusBone>(Bone(1.2f), Src);
        early.CustomizePlusBones["j_kubi"] = new Field<CustomizePlusBone>(Bone(0.9f), Src);
        var later = new DesignDraft();
        var laterSrc = new Provenance(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "later");
        later.CustomizePlusBones["j_kubi"] = new Field<CustomizePlusBone>(Bone(1.5f), laterSrc);

        early.MergeOnTop(later);
        Assert.Equal(2, early.CustomizePlusBones.Count);
        Assert.Equal(1.2f, early.CustomizePlusBones["j_kosi"].Value.Scaling.X);
        Assert.Equal(1.5f, early.CustomizePlusBones["j_kubi"].Value.Scaling.X);
        Assert.Equal(laterSrc, early.CustomizePlusBones["j_kubi"].Source);
    }

    [Fact]
    public void CustomizePlus_SurvivesEveryMask()
    {
        var draft = new DesignDraft();
        draft.CustomizePlusBones["j_kosi"] = new Field<CustomizePlusBone>(Bone(1.2f), Src);
        draft.SimpleHeels = new Field<HeelsOffset>(new HeelsOffset(0.1f, true), Src);

        var masked = draft.Filter(ApplyMask.OnlyMods());
        Assert.Single(masked.CustomizePlusBones);
        Assert.NotNull(masked.SimpleHeels);
        Assert.False(LayerKinds.IsMaskable(new CustomizePlusLayer()));
        Assert.False(LayerKinds.IsMaskable(new SimpleHeelsLayer()));
    }

    [Fact]
    public void CustomizePlus_CacheRefetchesAfterInvalidate_AndNotBefore()
    {
        var (bridge, profile) = CustomizePlus(("j_kosi", 1.2f));
        var cache = new CustomizePlusProfileCache(bridge);

        Assert.NotNull(cache.GetBones(profile));
        Assert.NotNull(cache.GetBones(profile));
        Assert.Single(bridge.CallsNamed("GetProfileBones"));
        Assert.Equal("Tall", cache.TryGetProfileName(profile));
        Assert.Single(bridge.CallsNamed("GetProfiles"));

        bridge.Bones[profile]["j_kosi"] = Bone(2f);
        Assert.Equal(1.2f, cache.GetBones(profile)!["j_kosi"].Scaling.X);
        cache.InvalidateAll();
        Assert.Equal(2f, cache.GetBones(profile)!["j_kosi"].Scaling.X);
    }

    [Fact]
    public void CustomizePlus_CacheDoesNotRememberMissesWhileUnavailable()
    {
        var (bridge, profile) = CustomizePlus(("j_kosi", 1.2f));
        var cache = new CustomizePlusProfileCache(bridge);
        bridge.Available = false;
        Assert.Null(cache.GetBones(profile));
        Assert.Empty(cache.Profiles);

        bridge.Available = true;
        Assert.NotNull(cache.GetBones(profile));
        Assert.Single(cache.Profiles);
    }

    // ---- Simple Heels layer ----------------------------------------------------------------------------

    [Fact]
    public void SimpleHeels_OffsetAndModeLandInTheDraft_LaterPassReplaces()
    {
        var resolver = Build.Resolver(new FakeGlamourerBridge());

        var first = resolver.Resolve(new SimpleHeelsLayer { Offset = 0.05f }, Src, out var skip);
        Assert.Null(skip);
        Assert.Equal(new HeelsOffset(0.05f, true), first!.SimpleHeels!.Value.Value);

        var second = resolver.Resolve(new SimpleHeelsLayer { Offset = 0.2f, Relative = false }, Src, out _);
        first.MergeOnTop(second!);
        Assert.Equal(new HeelsOffset(0.2f, false), first.SimpleHeels!.Value.Value);
    }

    [Fact]
    public void SimpleHeels_DisabledLayer_IsSkipped()
    {
        var resolver = Build.Resolver(new FakeGlamourerBridge());
        Assert.Null(resolver.Resolve(new SimpleHeelsLayer { Offset = 0.05f, Enabled = false }, Src, out var skip));
        Assert.Equal("disabled", skip);
    }

    // ---- bridge JSON -----------------------------------------------------------------------------------

    [Fact]
    public void CustomizePlusBridge_ParsesTheIpcProfileJson_DefaultsOmittedMembers()
    {
        // As Customize+ serializes it: DefaultValueHandling.Ignore drops zero vectors and false flags.
        const string json = """{"Bones":{"j_kosi":{"Scaling":{"X":1.2,"Y":1.2,"Z":1.2},"ChildScaling":{"X":1.0,"Y":1.0,"Z":1.0}},"j_kubi":{"Rotation":{"X":0.0,"Y":15.0,"Z":0.0},"ChildScaleIndependent":true}}}""";
        var bones = CustomizePlusBridge.ParseBones(json);

        Assert.Equal(2, bones.Count);
        Assert.Equal(new Vector3(1.2f), bones["j_kosi"].Scaling);
        Assert.Equal(Vector3.Zero, bones["j_kosi"].Translation);
        Assert.False(bones["j_kosi"].ChildScaleIndependent);
        Assert.Equal(15f, bones["j_kubi"].Rotation.Y);
        Assert.True(bones["j_kubi"].ChildScaleIndependent);
        Assert.Empty(CustomizePlusBridge.ParseBones("{}"));
    }

    [Fact]
    public void CustomizePlusBridge_BuildsTheIpcProfileJson_RoundTrips()
    {
        var bones = new Dictionary<string, CustomizePlusBone>(StringComparer.Ordinal)
        {
            ["j_kosi"] = new(new Vector3(0.1f, 0.2f, 0.3f), new Vector3(0, 15, 0), new Vector3(1.2f), Vector3.One, true, false, true, false),
        };
        var json = CustomizePlusBridge.BuildProfileJson(bones);
        var obj  = JObject.Parse(json);

        Assert.Equal(1.2f, obj["Bones"]!["j_kosi"]!["Scaling"]!["X"]!.Value<float>());
        Assert.Equal(15f, obj["Bones"]!["j_kosi"]!["Rotation"]!["Y"]!.Value<float>());
        Assert.True(obj["Bones"]!["j_kosi"]!["ChildScaleIndependent"]!.Value<bool>());
        Assert.True(obj["Bones"]!["j_kosi"]!["PropagateRotation"]!.Value<bool>());
        Assert.Equal(bones["j_kosi"], CustomizePlusBridge.ParseBones(json)["j_kosi"]);
    }

    [Fact]
    public void SimpleHeelsBridge_ConfigJson_RoundTrips_AndMissingOffsetReadsAsZero()
    {
        var json = SimpleHeelsBridge.BuildConfigJson(0.125f);
        Assert.Equal(0.125f, JObject.Parse(json)["DefaultOffset"]!.Value<float>());
        Assert.Equal(0.125f, SimpleHeelsBridge.ParseOffset(json));

        // Simple Heels omits DefaultOffset when it is (close to) zero, and reports an empty string with no player.
        Assert.Equal(0f, SimpleHeelsBridge.ParseOffset("""{"PluginVersion":"0.8.0.0"}"""));
        Assert.Equal(0f, SimpleHeelsBridge.ParseOffset(string.Empty));
        Assert.Null(SimpleHeelsBridge.ParseOffset("not json"));
    }

    [Fact]
    public void DescribeHeels_ShowsModeAndSign()
    {
        Assert.Equal("+0.05 (added to your offset)", DesignExplainer.DescribeHeels(new HeelsOffset(0.05f, true)));
        Assert.Equal("-0.02 (added to your offset)", DesignExplainer.DescribeHeels(new HeelsOffset(-0.02f, true)));
        Assert.Equal("0.125", DesignExplainer.DescribeHeels(new HeelsOffset(0.125f, false)));
    }

    [Fact]
    public void Fakes_RecordCallsAndHonourAvailability()
    {
        var cplus = new FakeCustomizePlusBridge();
        var (code, id) = cplus.SetTemporaryProfile(0, new Dictionary<string, CustomizePlusBone> { ["j_kosi"] = Bone(1f) });
        Assert.Equal(CustomizePlusEc.Success, code);
        Assert.NotNull(id);
        Assert.Equal(CustomizePlusEc.Success, cplus.DeleteTemporaryProfile(0));
        Assert.Equal(CustomizePlusEc.ProfileNotFound, cplus.DeleteTemporaryProfile(0));
        cplus.Available = false;
        Assert.Equal(CustomizePlusEc.UnknownError, cplus.SetTemporaryProfile(0, new Dictionary<string, CustomizePlusBone>()).Code);

        var heels = new FakeSimpleHeelsBridge { LocalOffset = 0.03f };
        Assert.True(heels.RegisterOffset(0, 0.1f));
        Assert.Equal(0.03f, heels.GetLocalOffset());
        var raised = new List<float>();
        heels.LocalOffsetChanged += raised.Add;
        heels.ChangeLocalOffset(0.08f);
        Assert.Equal([0.08f], raised);
        heels.Available = false;
        Assert.False(heels.Unregister(0));
        Assert.Null(heels.GetLocalOffset());
        Assert.Equal(["RegisterOffset", "GetLocalOffset", "Unregister", "GetLocalOffset"], heels.Calls.Select(c => c.Name).ToList());
    }
}
