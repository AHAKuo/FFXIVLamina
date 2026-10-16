using Lamina.Decals.Mesh;
using Xunit;

namespace Lamina.Tests.Decals;

public class BoneNamesTests
{
    [Theory]
    [InlineData("j_ude_a_l", "left upper arm")]
    [InlineData("j_ude_b_r", "right forearm")]
    [InlineData("j_asi_a_l", "left thigh")]
    [InlineData("j_asi_c_r", "right foot")]
    [InlineData("j_te_l", "left hand")]
    [InlineData("j_hito_b_r", "right index fingertip")]
    [InlineData("j_mune_l", "left chest")]
    [InlineData("j_sebo_b", "mid spine")]
    [InlineData("j_kosi", "hips")]
    [InlineData("j_kao", "head")]
    [InlineData("j_f_hoho_r", "right cheek")]
    [InlineData("j_f_hana", "nose")]
    [InlineData("n_sippo_c", "tail")]
    public void Friendly_MapsKnownBones(string bone, string expected)
        => Assert.Equal(expected, BoneNames.Friendly(bone));

    [Theory]
    [InlineData("j_ex_h0101_ke_a")]
    [InlineData("j_buki_sebo_l")]
    [InlineData("iv_kougan_a_l")]
    public void Friendly_FallsBackToTheRawName(string bone)
        => Assert.Equal(bone, BoneNames.Friendly(bone));

    [Fact]
    public void Friendly_HandlesMissingNames()
    {
        Assert.Equal("unknown", BoneNames.Friendly(null));
        Assert.Equal("unknown", BoneNames.Friendly(string.Empty));
    }
}
