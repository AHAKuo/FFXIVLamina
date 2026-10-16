using System.Text;
using Lamina.Decals.Mesh;
using Xunit;

namespace Lamina.Tests.Decals;

public class MtrlReaderTests
{
    private const string NormalPath  = "chara/human/c0101/obj/body/b0001/texture/c0101b0001_norm.tex";
    private const string DiffusePath = "chara/human/c0101/obj/body/b0001/texture/c0101b0001_base.tex";

    /// <summary> A layout-exact .mtrl with two DX11 textures, one uv set, one shader key and two samplers. </summary>
    private static byte[] Build(uint diffuseSamplerId)
    {
        var strings = new[] { NormalPath, DiffusePath, "uv1", "skin.shpk" };
        var offsets = new ushort[strings.Length];
        var table   = new MemoryStream();
        for (var i = 0; i < strings.Length; i++)
        {
            offsets[i] = (ushort)table.Length;
            table.Write(Encoding.UTF8.GetBytes(strings[i]));
            table.WriteByte(0);
        }

        using var file = new MemoryStream();
        var       w    = new BinaryWriter(file);
        w.Write(0x01030000u);          // version
        w.Write((ushort)0);            // file size (unused)
        w.Write((ushort)0);            // data set size
        w.Write((ushort)table.Length); // string table size
        w.Write(offsets[3]);           // shader package name offset
        w.Write((byte)2);              // texture count
        w.Write((byte)1);              // uv set count
        w.Write((byte)0);              // color set count
        w.Write((byte)0);              // additional data size

        w.Write(offsets[0]);
        w.Write(MtrlTexture.Dx11Flag);
        w.Write(offsets[1]);
        w.Write(MtrlTexture.Dx11Flag);
        w.Write(offsets[2]);           // uv set
        w.Write((ushort)0);

        w.Write(table.ToArray());

        w.Write((ushort)0);            // shader value list size
        w.Write((ushort)1);            // shader key count
        w.Write((ushort)0);            // constant count
        w.Write((ushort)2);            // sampler count
        w.Write(0u);                   // flags
        w.Write(0xDEADBEEFu);          // shader key
        w.Write(1u);

        w.Write(MtrlReader.SamplerNormal);
        w.Write(0u);
        w.Write((byte)0);
        w.Write(new byte[3]);
        w.Write(diffuseSamplerId);
        w.Write(0u);
        w.Write((byte)1);
        w.Write(new byte[3]);
        return file.ToArray();
    }

    [Fact]
    public void Parses_Textures_Samplers_And_ShaderPackage()
    {
        var material = MtrlReader.Read(Build(MtrlReader.SamplerDiffuse));
        Assert.Equal(0x01030000u, material.Version);
        Assert.Equal("skin.shpk", material.ShaderPackage);
        Assert.Equal(2, material.Textures.Length);
        Assert.Equal(NormalPath, material.Textures[0].Path);
        Assert.Equal(DiffusePath, material.Textures[1].Path);
        Assert.True(material.Textures[1].Dx11);
        Assert.Equal(2, material.Samplers.Length);
        Assert.Equal(MtrlReader.SamplerNormal, material.Samplers[0].Id);
        Assert.Equal(0, material.Samplers[0].TextureIndex);
        Assert.Equal(1, material.Samplers[1].TextureIndex);
    }

    [Fact]
    public void Diffuse_ComesFromTheSamplerTable()
    {
        var material = MtrlReader.Read(Build(MtrlReader.SamplerDiffuse));
        var diffuse  = material.Diffuse;
        Assert.NotNull(diffuse);
        Assert.Equal(DiffusePath, diffuse.Value.Path);
        Assert.Equal("chara/human/c0101/obj/body/b0001/texture/--c0101b0001_base.tex", diffuse.Value.LoadPath);
        Assert.Equal(NormalPath, material.TextureForSampler(MtrlReader.SamplerNormal)?.Path);
        Assert.Null(material.TextureForSampler(0x12345678));
    }

    [Fact]
    public void Diffuse_FallsBackToTheFileName()
    {
        var material = MtrlReader.Read(Build(0x12345678));
        Assert.Equal(DiffusePath, material.Diffuse?.Path);
    }

    [Fact]
    public void Rejects_TruncatedFile()
    {
        var data = Build(MtrlReader.SamplerDiffuse);
        Assert.Throws<InvalidDataException>(() => MtrlReader.Read(data[..(data.Length - 10)]));
    }

    [Theory]
    [InlineData("chara/x/texture/c0101b0001_d.tex", true)]
    [InlineData("chara/x/texture/--c0101b0001_d.tex", true)]
    [InlineData("chara/x/texture/c0101b0001_base.tex", true)]
    [InlineData("C:\\mods\\body\\skin_diffuse.tex", true)]
    [InlineData("chara/x/texture/c0101b0001_n.tex", false)]
    [InlineData("chara/x/texture/c0101b0001_m.tex", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeDiffuse_UsesTheSuffix(string? path, bool expected)
        => Assert.Equal(expected, MtrlReader.LooksLikeDiffuse(path));

    [Fact]
    public void Dx11Prefix_RoundTrips_And_SameTexture_IgnoresIt()
    {
        Assert.Equal("a/b/--c.tex", MtrlReader.WithDx11Prefix("a/b/c.tex"));
        Assert.Equal("a/b/--c.tex", MtrlReader.WithDx11Prefix("a/b/--c.tex"));
        Assert.Equal("a/b/c.tex", MtrlReader.WithoutDx11Prefix("a/b/--c.tex"));
        Assert.Equal("--c.tex", MtrlReader.WithDx11Prefix("c.tex"));
        Assert.True(MtrlReader.SameTexture("a/b/--c.tex", "A/B/c.tex"));
        Assert.False(MtrlReader.SameTexture("a/b/c.tex", "a/b/d.tex"));
        Assert.False(MtrlReader.SameTexture(null, "a/b/d.tex"));
    }
}
