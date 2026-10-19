using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Screenbox.Core.Playback;

/// <summary>Builds mpv JSON IPC commands without interpreting paths or metadata as command text.</summary>
public static class MpvCommandBuilder
{
    /// <summary>Creates one command with typed property values and unchanged string argument boundaries.</summary>
    public static JsonObject Create(params string[] arguments)
    {
        if (arguments.Length == 0) throw new ArgumentException("A command name is required.", nameof(arguments));
        var command = new JsonArray();
        foreach (string argument in arguments) command.Add((JsonNode?)JsonValue.Create(argument));
        if (arguments.Length >= 3 && arguments[0] == "set_property")
        {
            if (arguments[1] is "pause" or "mute") command[2] = arguments[2] == "yes";
            else if (arguments[1] is "volume" or "speed" or "audio-delay" or "sub-delay" or "video-aspect-override")
                command[2] = double.Parse(arguments[2], CultureInfo.InvariantCulture);
        }
        return new JsonObject { ["command"] = command };
    }
}
