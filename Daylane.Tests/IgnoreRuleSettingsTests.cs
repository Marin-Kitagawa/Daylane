using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class IgnoreRuleSettingsTests
{
    private static TempDatabase MigratedDatabase()
    {
        var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, temp.DatabasePath);
        return temp;
    }

    [Fact]
    public void NewCaptureSettings_DefaultToOff()
    {
        using var temp = MigratedDatabase();

        var service = new SettingsService(temp.ConnectionString);

        Assert.False(service.Current.RecordWindowTitles);
        Assert.False(service.Current.RecordBrowserHost);
        Assert.Empty(service.Current.PrivacyKeywords);
        Assert.Empty(service.Current.IgnoreRules);
    }

    [Fact]
    public void IgnoreRules_RoundTripThroughTheStore()
    {
        using var temp = MigratedDatabase();
        var service = new SettingsService(temp.ConnectionString);

        service.Update(s => s with
        {
            RecordWindowTitles = true,
            PrivacyKeywords = new[] { "bank", "password" },
            IgnoreRules = new[]
            {
                new IgnoreRule("WindowsTerminal", "download"),
                new IgnoreRule("Solitaire", null)
            }
        });

        var reloaded = new SettingsService(temp.ConnectionString).Current;
        Assert.True(reloaded.RecordWindowTitles);
        Assert.Equal(new[] { "bank", "password" }, reloaded.PrivacyKeywords);
        Assert.Equal(2, reloaded.IgnoreRules.Count);
        Assert.Equal("WindowsTerminal", reloaded.IgnoreRules[0].ProcessName);
        Assert.Equal("download", reloaded.IgnoreRules[0].TitleKeyword);
        Assert.Null(reloaded.IgnoreRules[1].TitleKeyword);
    }

    [Fact]
    public void UnknownKeys_StillSurviveAWrite()
    {
        using var temp = MigratedDatabase();
        using (var connection = temp.Open())
        {
            using var seed = connection.CreateCommand();
            seed.CommandText =
                """UPDATE SettingsStore SET Data = '{"appearance":"dark","futureFlag":true}' WHERE Id = 1;""";
            seed.ExecuteNonQuery();
        }

        var service = new SettingsService(temp.ConnectionString);
        service.Update(s => s with { RecordWindowTitles = true });

        using var read = temp.Open();
        using var command = read.CreateCommand();
        command.CommandText = "SELECT Data FROM SettingsStore WHERE Id = 1;";
        Assert.Contains("futureFlag", (string)command.ExecuteScalar()!);
    }
}
