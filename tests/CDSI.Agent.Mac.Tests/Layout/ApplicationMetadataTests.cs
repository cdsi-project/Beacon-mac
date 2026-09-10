using System.Xml.Linq;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Layout;

public sealed class ApplicationMetadataTests
{
    [Fact]
    public void ApplicationNameMatchesMacBundleBranding()
    {
        var document = XDocument.Load(FindApplicationXaml());

        Assert.Equal("CDSI Beacon", (string?)document.Root?.Attribute("Name"));
    }

    private static string FindApplicationXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "CDSI.Agent.Mac", "App.axaml");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("Could not find CDSI.Agent.Mac/App.axaml.");
    }
}
