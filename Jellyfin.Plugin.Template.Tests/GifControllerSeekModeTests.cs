using System.Diagnostics;
using System.Reflection;
using Jellyfin.Plugin.Template.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Template.Tests;

public class GifControllerSeekModeTests
{
    private const string ControllerTypeName = "Jellyfin.Plugin.Template.Api.Controllers.GifController";

    [Fact]
    public void BuildProcessStartInfo_SubtitleBurnIn_UsesAccurateOrderingBeforeThreshold()
    {
        var processInfo = BuildProcessStartInfo(599.999, SubtitleSeekMode.Fast);

        var firstSeekIndex = processInfo.ArgumentList.IndexOf("-ss");
        var firstInputIndex = processInfo.ArgumentList.IndexOf("-i");

        Assert.True(firstInputIndex >= 0);
        Assert.True(firstSeekIndex >= 0);
        Assert.True(firstInputIndex < firstSeekIndex);
    }

    [Fact]
    public void BuildProcessStartInfo_SubtitleBurnIn_UsesFastOrderingAtThreshold()
    {
        var processInfo = BuildProcessStartInfo(600, SubtitleSeekMode.Accurate);

        var firstSeekIndex = processInfo.ArgumentList.IndexOf("-ss");
        var firstInputIndex = processInfo.ArgumentList.IndexOf("-i");

        Assert.True(firstInputIndex >= 0);
        Assert.True(firstSeekIndex >= 0);
        Assert.True(firstSeekIndex < firstInputIndex);
    }

    [Fact]
    public void BuildProcessStartInfo_SubtitleBurnIn_UnknownConfiguredMode_FallsBackToThresholdLogic()
    {
        var unknownMode = (SubtitleSeekMode)99;

        var beforeThreshold = BuildProcessStartInfo(599.999, unknownMode);
        Assert.True(beforeThreshold.ArgumentList.IndexOf("-i") < beforeThreshold.ArgumentList.IndexOf("-ss"));

        var atThreshold = BuildProcessStartInfo(600, unknownMode);
        Assert.True(atThreshold.ArgumentList.IndexOf("-ss") < atThreshold.ArgumentList.IndexOf("-i"));
    }

    private static ProcessStartInfo BuildProcessStartInfo(double startSeconds, SubtitleSeekMode subtitleSeekMode)
    {
        var controllerType = typeof(Jellyfin.Plugin.Template.Plugin).Assembly.GetType(ControllerTypeName, throwOnError: true)!;
        var subtitleSelectionType = controllerType.GetNestedType("SubtitleSelection", BindingFlags.NonPublic)!;
        var subtitleSelection = Activator.CreateInstance(
            subtitleSelectionType,
            args: [true, null, 0, null])!;

        var method = controllerType.GetMethod(
            "BuildProcessStartInfo",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        return (ProcessStartInfo)method.Invoke(
            obj: null,
            parameters:
            [
                "ffmpeg",
                startSeconds,
                3d,
                "/tmp/input.mkv",
                12,
                320,
                "/tmp/out.gif",
                subtitleSelection,
                null,
                0d,
                subtitleSeekMode
            ])!;
    }
}
