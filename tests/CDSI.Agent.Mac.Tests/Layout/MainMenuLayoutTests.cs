using System.Xml.Linq;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Layout;

public sealed class MainMenuLayoutTests
{
    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";

    [Fact]
    public void SettingsIsAnExportableTopLevelMenuWithTheStandardShortcut()
    {
        var menu = LoadMainMenu();
        var settingsMenu = menu
            .Elements(AvaloniaNamespace + "NativeMenuItem")
            .Single(element => (string?)element.Attribute("Header") == "设置");

        Assert.Null(settingsMenu.Attribute("Command"));

        var settingsItem = Assert.Single(settingsMenu
            .Descendants(AvaloniaNamespace + "NativeMenuItem"));
        Assert.Equal("设置...", (string?)settingsItem.Attribute("Header"));
        Assert.Equal(
            "{Binding OpenSettingsCommand}",
            (string?)settingsItem.Attribute("Command"));
        Assert.Equal("Meta+,", (string?)settingsItem.Attribute("Gesture"));
    }

    [Fact]
    public void FileMenuDoesNotContainTheLegacyScanDirectorySettingsEntry()
    {
        var menu = LoadMainMenu();
        var fileMenu = menu
            .Elements(AvaloniaNamespace + "NativeMenuItem")
            .Single(element => (string?)element.Attribute("Header") == "文件");

        Assert.DoesNotContain(
            fileMenu.Descendants(AvaloniaNamespace + "NativeMenuItem"),
            element =>
                (string?)element.Attribute("Header") == "扫描目录设置..." ||
                (string?)element.Attribute("Command") == "{Binding OpenSettingsCommand}");
    }

    private static XElement LoadMainMenu()
    {
        var document = XDocument.Load(FindMainWindowXaml());
        return Assert.IsType<XElement>(document.Root?
            .Element(AvaloniaNamespace + "NativeMenu.Menu")?
            .Element(AvaloniaNamespace + "NativeMenu"));
    }

    private static string FindMainWindowXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "CDSI.Agent.Mac", "MainWindow.axaml");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("Could not find CDSI.Agent.Mac/MainWindow.axaml.");
    }
}
