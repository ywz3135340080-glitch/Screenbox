using System.Globalization;
using System.Text.Json.Nodes;
using Screenbox.Core.Playback;

namespace Screenbox.Core.Tests.Playback;

public class MpvCommandBuilderTests
{
    [Test]
    public async Task Loadfile_UnicodeAndQuotedPath_RemainsOneLiteralArgument()
    {
        const string path = "D:\\电影\\名字 \"含引号\"; quit\\片源.mp4";
        string json = MpvCommandBuilder.Create("loadfile", path, "replace").ToJsonString();
        JsonArray command = JsonNode.Parse(json)!["command"]!.AsArray();
        await Assert.That(command.Count).IsEqualTo(3);
        await Assert.That(command[0]!.GetValue<string>()).IsEqualTo("loadfile");
        await Assert.That(command[1]!.GetValue<string>()).IsEqualTo(path);
    }

    [Test]
    [Arguments("pause", "yes", true)]
    [Arguments("pause", "no", false)]
    [Arguments("mute", "yes", true)]
    [Arguments("mute", "no", false)]
    public async Task BooleanProperty_UsesJsonBoolean(string property, string value, bool expected)
    {
        JsonNode command = MpvCommandBuilder.Create("set_property", property, value)["command"]!;
        await Assert.That(command[2]!.GetValue<bool>()).IsEqualTo(expected);
    }

    [Test]
    public async Task NumericProperty_UsesInvariantCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            JsonNode command = MpvCommandBuilder.Create("set_property", "audio-delay", "0.125")["command"]!;
            await Assert.That(command[2]!.GetValue<double>()).IsEqualTo(0.125);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task TrackDisable_RetainsMpvChoiceValue()
    {
        JsonNode command = MpvCommandBuilder.Create("set_property", "sid", "no")["command"]!;
        await Assert.That(command[2]!.GetValue<string>()).IsEqualTo("no");
    }
}
