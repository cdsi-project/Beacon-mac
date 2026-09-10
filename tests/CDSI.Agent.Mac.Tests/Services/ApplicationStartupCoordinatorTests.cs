using CDSI.Agent.Mac.Services;
using Xunit;

namespace CDSI.Agent.Mac.Tests.Services;

public sealed class ApplicationStartupCoordinatorTests
{
    [Fact]
    public void FreshInstallationDoesNotReportDatabasesAsMissing()
    {
        var result = ApplicationStartupCoordinator.GetMissingStateDatabases(
            clientIdentityExistedBeforeStartup: false,
            assetDatabaseExists: false,
            readerDatabaseExists: false);

        Assert.Equal(MissingStateDatabases.None, result);
    }

    [Theory]
    [InlineData(true, false, false, 3)]
    [InlineData(false, true, false, 2)]
    [InlineData(false, false, true, 1)]
    [InlineData(true, true, true, 0)]
    public void ExistingInstallationReportsEveryMissingDatabase(
        bool identityExists,
        bool assetDatabaseExists,
        bool readerDatabaseExists,
        int expected)
    {
        var result = ApplicationStartupCoordinator.GetMissingStateDatabases(
            identityExists,
            assetDatabaseExists,
            readerDatabaseExists);

        Assert.Equal((MissingStateDatabases)expected, result);
    }
}
