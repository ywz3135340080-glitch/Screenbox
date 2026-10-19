using Screenbox.Core.Playback;

if (args.Length != 1)
{
    Console.Error.WriteLine("An input file is required.");
    return 2;
}

await using var input = File.OpenRead(args[0]);
var configuration = await DolbyVisionDetector.ReadConfigurationAsync(input);
if (configuration is null)
{
    Console.Error.WriteLine("No supported MP4 Dolby Vision configuration was found.");
    return 1;
}

Console.WriteLine($"Profile={configuration.Profile}; Level={configuration.Level}; " +
    $"RPU={configuration.HasRpu}; EL={configuration.HasEnhancementLayer}; " +
    $"BL={configuration.HasBaseLayer}; Compatibility={configuration.BaseLayerCompatibilityId}; " +
    $"SingleLayerProfile5={configuration.IsProfile5SingleLayer}; RestoredPosition={input.Position}");
return 0;
