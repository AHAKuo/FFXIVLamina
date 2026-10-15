using Lamina.Ipc;
using Lamina.Models;
using Lamina.Runtime;
using Lamina.Runtime.Compose;
using Lamina.Tests.Compose;
using Xunit;

namespace Lamina.Tests.Fakes;

/// <summary> Parsing of Vitrine's documented IPC payload and the resolver's behaviour through the bridge. </summary>
public class VitrineBridgeTests
{
    /// <summary> The example from vitrine/docs/IPC.md, verbatim. </summary>
    private const string DocumentedDesign = """
        {
          "id": "a0f95146-309c-544e-9225-4702f2c86cb1",
          "name": "Cowkini",
          "category": "Gear",
          "isAuto": true,
          "baseDesignId": null,
          "mods": [
            {
              "modDirectory": "Cowkini",
              "modName": "Cowkini",
              "enabled": true,
              "priority": 0,
              "settings": { "Cowkini Legs": ["Default Eve Legs"], "Extras": ["Tiger stripes"] }
            }
          ]
        }
        """;

    [Fact]
    public void ParseDesigns_ReadsTheDocumentedExample()
    {
        var designs = VitrineBridge.ParseDesigns("[" + DocumentedDesign + "]");

        var d = Assert.Single(designs);
        Assert.Equal(Guid.Parse("a0f95146-309c-544e-9225-4702f2c86cb1"), d.Id);
        Assert.Equal("Cowkini", d.Name);
        Assert.Equal("Gear", d.Category);
        Assert.True(d.IsAuto);
        Assert.Null(d.BaseDesignId);
        var mod = Assert.Single(d.Mods);
        Assert.Equal("Cowkini", mod.ModDirectory);
        Assert.True(mod.Enabled);
        Assert.Equal(0, mod.Priority);
        Assert.Equal(new[] { "Default Eve Legs" }, mod.Settings!["Cowkini Legs"]);
        Assert.Equal(new[] { "Tiger stripes" }, mod.Settings["Extras"]);
    }

    [Fact]
    public void ParseDesign_SingleObject_AndEmptyPayload()
    {
        Assert.NotNull(VitrineBridge.ParseDesign(DocumentedDesign));
        Assert.Null(VitrineBridge.ParseDesign(string.Empty));
    }

    [Fact]
    public void ParseDesigns_ToleratesUnknownFieldsNullSettingsAndDropsInvalidEntries()
    {
        const string json = """
            [
              { "id": "00000000-0000-0000-0000-000000000000", "name": "bad" },
              { "id": "0f0f0f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f", "name": "ok", "category": "Future", "future": 1,
                "mods": [ { "modDirectory": "M", "settings": null }, { "modDirectory": "" } ] }
            ]
            """;

        var designs = VitrineBridge.ParseDesigns(json);

        var d = Assert.Single(designs);
        Assert.Equal("Future", d.Category);
        var mod = Assert.Single(d.Mods);
        Assert.Null(mod.Settings);
        Assert.True(mod.Enabled);
    }

    [Fact]
    public void Resolver_AppliesSettingsOverridePerGroup_OnTopOfDesignSettings()
    {
        var vitrine = new FakeVitrineBridge();
        var design = new VitrineDesignInfo
        {
            Id   = Guid.NewGuid(),
            Name = "D",
            Mods =
            {
                new VitrineModInfo
                {
                    ModDirectory = "Dir",
                    ModName      = "Mod",
                    Priority     = 3,
                    Settings     = new() { ["Legs"] = new() { "A" }, ["Extras"] = new() { "X" } },
                },
            },
        };
        vitrine.DesignList.Add(design);
        var resolver = Build.Resolver(new Lamina.Tests.Compose.FakeGlamourerBridge(), vitrine);
        var layer    = new VitrineDesignLayer { VitrineDesignId = design.Id, SettingsOverride = new() { ["Legs"] = new() { "B" } } };

        var draft = resolver.Resolve(layer, Provenance.None, out var reason);

        Assert.Null(reason);
        var mod = draft!.Mods["Dir"].Value;
        Assert.Equal(ModToggle.Enable, mod.State);
        Assert.Equal(3, mod.Priority);
        Assert.Equal(new[] { "B" }, mod.Settings!["Legs"]);
        Assert.Equal(new[] { "X" }, mod.Settings["Extras"]);
    }

    [Fact]
    public void Resolver_SkipsWhenVitrineIsMissingOrDesignUnknown()
    {
        var vitrine  = new FakeVitrineBridge { Available = false };
        var resolver = Build.Resolver(new Lamina.Tests.Compose.FakeGlamourerBridge(), vitrine);
        var layer    = new VitrineDesignLayer { VitrineDesignId = Guid.NewGuid() };

        Assert.Null(resolver.Resolve(layer, Provenance.None, out var reason));
        Assert.Contains("not installed", reason);

        vitrine.Available = true;
        Assert.Null(resolver.Resolve(layer, Provenance.None, out reason));
        Assert.Contains("not found", reason);
    }

    [Fact]
    public void Fake_OpenWindowAndPreview_RecordCalls()
    {
        var vitrine = new FakeVitrineBridge();
        vitrine.OpenWindow(null);
        Assert.Null(vitrine.LastOpenWindow);
        Assert.Equal("Preview written.", vitrine.PreviewDesign(Guid.NewGuid()));
        Assert.Equal(2, vitrine.Calls.Count);
    }
}
