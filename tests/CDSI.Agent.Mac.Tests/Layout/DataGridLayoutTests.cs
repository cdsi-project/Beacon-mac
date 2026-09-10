using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Layout;

public sealed class DataGridLayoutTests
{
    private static readonly XNamespace AvaloniaNamespace =
        "https://github.com/avaloniaui";

    [Fact]
    public void SettingsServiceGridsScrollAndKeepStatusHeadersReadable()
    {
        var document = LoadXaml("Settings", "SettingsWindow.axaml");
        var openWebGrid = GetGrid(GetTab(document, "OpenWeb"));
        var gitGrid = GetGrid(GetTab(document, "Git"));

        AssertGridScrolls(openWebGrid);
        AssertFixedColumnsHaveMinimumWidths(openWebGrid);
        AssertColumnMinWidth(openWebGrid, "默认", 76);
        AssertColumnMinWidth(openWebGrid, "凭据", 82);

        AssertGridScrolls(gitGrid);
        AssertFixedColumnsHaveMinimumWidths(gitGrid);
        AssertColumnMinWidth(gitGrid, "访问方式", 104);
        AssertColumnMinWidth(gitGrid, "默认", 76);
        AssertColumnMinWidth(gitGrid, "凭据", 82);
    }

    [Theory]
    [InlineData("ProjectsView.axaml", "ProjectGrid", "已备份", 88)]
    [InlineData("ProjectsView.axaml", "ProjectAssetGrid", "备份状态", 120)]
    [InlineData("CloudBackupsView.axaml", "CloudBackupProjectGrid", "最近备份", 145)]
    [InlineData("CloudBackupsView.axaml", "CloudBackupGrid", "备份时间", 145)]
    public void SplitViewGridsScrollAndKeepTrailingHeadersReadable(
        string filename,
        string gridName,
        string header,
        double minimumWidth)
    {
        var document = LoadXaml("Views", filename);
        var grid = document
            .Descendants(AvaloniaNamespace + "DataGrid")
            .Single(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" &&
                attribute.Value == gridName));

        AssertGridScrolls(grid);
        AssertFixedColumnsHaveMinimumWidths(grid);
        AssertColumnMinWidth(grid, header, minimumWidth);
    }

    private static XElement GetTab(XDocument document, string header) =>
        document
            .Descendants(AvaloniaNamespace + "TabItem")
            .Single(element => (string?)element.Attribute("Header") == header);

    private static XElement GetGrid(XContainer container) =>
        container.Descendants(AvaloniaNamespace + "DataGrid").Single();

    private static void AssertGridScrolls(XElement grid)
    {
        Assert.Equal("Auto", (string?)grid.Attribute("HorizontalScrollBarVisibility"));
    }

    private static void AssertColumnMinWidth(
        XContainer container,
        string header,
        double expectedMinimum)
    {
        var column = container
            .Descendants(AvaloniaNamespace + "DataGridTextColumn")
            .Single(element => (string?)element.Attribute("Header") == header);
        var minimumWidth = double.Parse(
            Assert.IsType<XAttribute>(column.Attribute("MinWidth")).Value,
            CultureInfo.InvariantCulture);

        Assert.True(minimumWidth >= expectedMinimum);
    }

    private static void AssertFixedColumnsHaveMinimumWidths(XContainer container)
    {
        foreach (var column in container
                     .Descendants(AvaloniaNamespace + "DataGridTextColumn"))
        {
            var widthText = (string?)column.Attribute("Width");
            if (widthText is null || widthText.Contains('*', StringComparison.Ordinal))
            {
                continue;
            }

            var width = double.Parse(widthText, CultureInfo.InvariantCulture);
            var minimumWidth = double.Parse(
                Assert.IsType<XAttribute>(column.Attribute("MinWidth")).Value,
                CultureInfo.InvariantCulture);
            Assert.True(
                minimumWidth >= width,
                $"Column '{(string?)column.Attribute("Header")}' can shrink below its declared width.");
        }
    }

    private static XDocument LoadXaml(params string[] relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                directory.FullName,
                "CDSI.Agent.Mac",
                Path.Combine(relativePath));
            if (File.Exists(path))
            {
                return XDocument.Load(path);
            }
        }

        throw new FileNotFoundException(
            $"Could not find CDSI.Agent.Mac/{Path.Combine(relativePath)}.");
    }
}
