using Daylane.ViewModels;

namespace Daylane.Tests;

/// <summary>
/// Pins MainWindowViewModel.FormatBytes at its unit boundaries. It is the only pure logic this
/// view-model surface added -- everything else on MainWindowViewModel needs a live
/// TrackingService (real database, real OS hooks) and cannot be constructed in a unit test.
/// </summary>
public class FormatBytesTests
{
    [Fact]
    public void FormatBytes_ZeroBytes_ReportsZeroB()
    {
        Assert.Equal("0 B", MainWindowViewModel.FormatBytes(0));
    }

    [Fact]
    public void FormatBytes_JustBelowOneKilobyte_StaysInBytes()
    {
        Assert.Equal("1023 B", MainWindowViewModel.FormatBytes(1023));
    }

    [Fact]
    public void FormatBytes_ExactlyOneKilobyte_RollsOverToKB()
    {
        Assert.Equal("1 KB", MainWindowViewModel.FormatBytes(1024));
    }

    [Fact]
    public void FormatBytes_OneAndAHalfKilobytes_ReportsOneDecimalPlace()
    {
        Assert.Equal("1.5 KB", MainWindowViewModel.FormatBytes(1536));
    }

    [Fact]
    public void FormatBytes_ManyGigabytes_CapsAtGBRatherThanRollingToTB()
    {
        // 1024 GB worth of bytes: the unit loop stops once it reaches the last entry in the
        // table (GB), so this reports "1024 GB", never "1 TB".
        long bytes = 1024L * 1024 * 1024 * 1024;

        Assert.Equal("1024 GB", MainWindowViewModel.FormatBytes(bytes));
    }
}
