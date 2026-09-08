using Daylane.Services;

namespace Daylane.Tests;

public class LegacyConfigImportTests
{
    private static string WriteConfig(string contents)
    {
        string path = Path.Combine(
            Path.GetTempPath(), "daylane-tests", Guid.NewGuid().ToString("N"), "config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void ReadThresholdMinutes_ParsesValidValue()
    {
        string path = WriteConfig("[settings]\nthreshold_minutes=7\n");

        Assert.Equal(7, LegacyConfig.ReadThresholdMinutes(path));
    }

    [Theory]
    [InlineData("[settings]\nthreshold_minutes=0\n")]
    [InlineData("[settings]\nthreshold_minutes=999\n")]
    [InlineData("[settings]\nthreshold_minutes=abc\n")]
    [InlineData("[settings]\n")]
    public void ReadThresholdMinutes_RejectsInvalidValue(string contents)
    {
        string path = WriteConfig(contents);

        Assert.Null(LegacyConfig.ReadThresholdMinutes(path));
    }

    [Fact]
    public void ReadThresholdMinutes_WhenFileMissing_ReturnsNull()
    {
        Assert.Null(LegacyConfig.ReadThresholdMinutes(
            Path.Combine(Path.GetTempPath(), "daylane-tests", "definitely-absent", "config.ini")));
    }

    [Fact]
    public void LegacyConfigImported_DefaultsToFalseAndSurvivesAWrite()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var service = new SettingsService(temp.ConnectionString);
        Assert.False(service.Current.LegacyConfigImported);

        service.Update(s => s with { LegacyConfigImported = true });

        Assert.True(new SettingsService(temp.ConnectionString).Current.LegacyConfigImported);
    }

    [Fact]
    public void LegacyConfigImported_OnceSet_StopsTheImportOverwritingAChosenValue()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var service = new SettingsService(temp.ConnectionString);
        service.Update(s => s with { IdleThresholdMinutes = 15, LegacyConfigImported = true });

        // Re-reading must not reset a deliberately chosen value that happens to equal the default.
        Assert.Equal(15, new SettingsService(temp.ConnectionString).Current.IdleThresholdMinutes);
        Assert.True(new SettingsService(temp.ConnectionString).Current.LegacyConfigImported);
    }
}
