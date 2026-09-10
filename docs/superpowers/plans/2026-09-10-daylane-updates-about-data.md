# Daylane Updates, About & Data Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in update check, a data-management panel (purge, storage usage, data path) and an About panel, none of which contact anything on a default install.

**Architecture:** Three independent features sharing one Settings surface. The update check is split into pure pieces (version comparison, JSON parsing, the decision to check at all) and one thin HTTP call behind a delegate seam, so everything except the socket is unit-tested. Purge lives on `DailyStatsStore` beside the other write methods and is driven through `TrackingService`, which must close the open segment first. Storage usage is a pure function of file sizes plus row counts.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), Avalonia 12.1.0, Microsoft.Data.Sqlite 10.0.10, `System.Net.Http.HttpClient`, `System.Text.Json` source generation, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-10-daylane-updates-about-data-design.md`

## Global Constraints

- **No AI features, ever.** Permanent product constraint, not a deferral.
- **Windows only.** `net10.0-windows`; no macOS code paths.
- **A default install contacts nothing.** `CheckForUpdates` defaults to `false`, and with it false no update code path may construct an HTTP request.
- **Daylane never downloads or executes an update.** It links to the release page in the system browser.
- **The release source is `Marin-Kitagawa/Daylane`**, not the `mirbyte/Daylane` upstream this repo forked from.
- **The license is AGPL-3.0**, not Hindsight's MIT.
- No telemetry, no analytics, no crash reporting, anywhere.
- Schema scripts are **append-only**. V1–V4 are shipped and must not be edited. This sub-project adds no tables.
- Column naming is **PascalCase**. Timestamps go through `Services/Timestamps.cs`.
- Every colour in XAML must be a `{DynamicResource ...}` token. The gate is
  `grep -rnE "Brushes\.[A-Za-z]+|Color\.(Parse|FromRgb)|Colors\.|#[0-9A-Fa-f]{6}" Controls/ ViewModels/ App.axaml MainWindow.axaml`
  and must show only `App.axaml`'s token definitions and `Controls/TimelinePalette.cs`'s deliberate magenta fallback.
- `System.Text.Json` source generation: any new serializable type **must be a positional record with defaulted parameters**, never init-only properties. See the doc comment on `Models/DaylaneSettings.cs` — a source-generated deserializer leaves init-only properties absent from the JSON at their CLR default rather than the C# initializer, which silently zeroes settings.
- Commits are GPG-signed (`git commit -S`) with **no** `Co-Authored-By` trailer and no self-attribution. Plain imperative subjects.
- The suite stands at **229 tests, all green**. Full green is the bar after every task. Verify the build with **`dotnet build Daylane.csproj --no-incremental`** — an incremental build reports "0 Warning(s)" without recompiling and will lie to you. Zero warnings is the bar.
- **Do not launch the app** unless the controller says the user's Daylane is closed — `Program.cs`'s mutex name is a hardcoded constant, so a second instance pops the user's live window to the foreground.

## File Structure

| File | Responsibility |
|---|---|
| `Models/DaylaneSettings.cs` (modify) | Gains `CheckForUpdates`, `LastSeenVersion` |
| `Services/VersionCompare.cs` (create) | Pure: is tag X newer than version Y |
| `Models/ReleaseInfo.cs` (create) | The two fields we use from a release: tag and page URL |
| `Services/ReleaseFeed.cs` (create) | Pure: GitHub release JSON → `ReleaseInfo?` |
| `Services/UpdateChecker.cs` (create) | The decision to check, the comparison, the suppression. HTTP arrives as a delegate |
| `Services/ReleaseFetch.cs` (create) | The only file that touches the network. One `HttpClient` call |
| `Services/DailyStatsStore.cs` (modify) | `PurgeRecordedActivity()` beside the other write methods |
| `Services/StorageUsage.cs` (create) | Pure: database file sizes and row counts |
| `Services/AppInfo.cs` (create) | Version, author, license, links — static, offline |
| `Services/TrackingService.cs` (modify) | Drives purge safely; exposes storage usage |
| `ViewModels/MainWindowViewModel.cs` (modify) | Properties and commands for all three panels |
| `ViewModels/SettingsChangeNotifier.cs` (modify) | New property names |
| `MainWindow.axaml` (modify) | Updates, Data and About panels |
| `Daylane.csproj` (modify) | `Authors`, `Product` |
| `README.md` (modify) | Documents the request; badges repointed to this fork |

---

### Task 1: Settings keys for the update check

**Files:**
- Modify: `Models/DaylaneSettings.cs`
- Test: `Daylane.Tests/UpdateSettingsTests.cs`

**Interfaces:**
- Produces: `DaylaneSettings.CheckForUpdates` (`bool`, default `false`), `DaylaneSettings.LastSeenVersion` (`string?`, default `null`).

**These are positional record parameters with defaults, appended after `IgnoreRules`.** Do not add them as init-only properties — read the doc comment at the top of the file for why that silently zeroes settings under source-generated JSON.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/UpdateSettingsTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class UpdateSettingsTests
{
    [Fact]
    public void CheckForUpdates_DefaultsToOff()
    {
        // The promise: a default install contacts nothing. If this flips, the README is a lie.
        Assert.False(new DaylaneSettings().CheckForUpdates);
    }

    [Fact]
    public void LastSeenVersion_DefaultsToNull()
        => Assert.Null(new DaylaneSettings().LastSeenVersion);

    [Fact]
    public void CheckForUpdates_SurvivesARoundTripThroughTheStore()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var service = new SettingsService(temp.ConnectionString);
        Assert.True(service.Update(s => s with { CheckForUpdates = true, LastSeenVersion = "v1.2.3" }));

        // A fresh service reads the persisted row, not the in-memory value.
        var reread = new SettingsService(temp.ConnectionString).Current;
        Assert.True(reread.CheckForUpdates);
        Assert.Equal("v1.2.3", reread.LastSeenVersion);
    }

    [Fact]
    public void Normalize_LeavesTheUpdateKeysAlone()
    {
        var settings = (new DaylaneSettings() with
        {
            CheckForUpdates = true,
            LastSeenVersion = "v9.9.9"
        }).Normalize();

        Assert.True(settings.CheckForUpdates);
        Assert.Equal("v9.9.9", settings.LastSeenVersion);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter UpdateSettingsTests`
Expected: compile error — `DaylaneSettings` has no `CheckForUpdates`.

- [ ] **Step 3: Add the parameters**

In `Models/DaylaneSettings.cs`, append two parameters after `IgnoreRules`, keeping the trailing `)`:

```csharp
    /// <summary>Off by default, and the only thing that permits any network access at all.
    /// While false, no update code path constructs an HTTP request.</summary>
    [property: JsonPropertyName("checkForUpdates")]
    bool CheckForUpdates = false,

    /// <summary>The newest release tag the user has already been shown, so the same release is
    /// not announced twice. Deliberately not a last-checked timestamp: this is the only state
    /// needed to avoid nagging, and it carries no clock-skew failure modes.</summary>
    [property: JsonPropertyName("lastSeenVersion")]
    string? LastSeenVersion = null)
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 233 tests.

- [ ] **Step 5: Commit**

```bash
git add Models/DaylaneSettings.cs Daylane.Tests/UpdateSettingsTests.cs
git commit -S -m "Add the update-check settings, off by default"
```

---

### Task 2: Version comparison

**Files:**
- Create: `Services/VersionCompare.cs`
- Test: `Daylane.Tests/VersionCompareTests.cs`

**Interfaces:**
- Produces: `internal static bool VersionCompare.IsNewer(string? candidateTag, string currentVersion)` and `internal static Version? VersionCompare.TryParseTag(string? tag)`.

A tag that does not parse is **not an update**, never an error. A malformed tag upstream must not surface a scary message to a user who can do nothing about it.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/VersionCompareTests.cs`:

```csharp
using Daylane.Services;

namespace Daylane.Tests;

public class VersionCompareTests
{
    [Theory]
    [InlineData("v1.0.2", "1.0.1")]
    [InlineData("1.0.2", "1.0.1")]
    [InlineData("v1.1.0", "1.0.9")]
    [InlineData("v2.0.0", "1.9.9")]
    // String comparison would call 1.0.9 newer than 1.0.10; version comparison must not.
    [InlineData("v1.0.10", "1.0.9")]
    public void IsNewer_TrueWhenTheTagIsAhead(string tag, string current)
        => Assert.True(VersionCompare.IsNewer(tag, current));

    [Theory]
    [InlineData("v1.0.1", "1.0.1")]
    [InlineData("v1.0.0", "1.0.1")]
    [InlineData("v0.9.9", "1.0.1")]
    public void IsNewer_FalseWhenTheTagIsNotAhead(string tag, string current)
        => Assert.False(VersionCompare.IsNewer(tag, current));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("release-2026-09-10")]
    [InlineData("v1.2.3-beta.1")]
    public void IsNewer_TreatsAnUnparseableTagAsNoUpdate(string? tag)
    {
        // Not an error. A tag we cannot read is upstream's problem, not something to alarm
        // a user about, and certainly not grounds for offering a download.
        Assert.False(VersionCompare.IsNewer(tag, "1.0.1"));
    }

    [Fact]
    public void IsNewer_FalseWhenTheCurrentVersionIsUnreadable()
    {
        // If we cannot tell what we are, we must not claim something else is newer.
        Assert.False(VersionCompare.IsNewer("v99.0.0", "not-a-version"));
    }

    [Fact]
    public void TryParseTag_StripsALeadingV()
        => Assert.Equal(new Version(1, 2, 3), VersionCompare.TryParseTag("v1.2.3"));

    [Fact]
    public void TryParseTag_AcceptsATwoPartVersion()
        => Assert.Equal(new Version(1, 2), VersionCompare.TryParseTag("1.2"));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter VersionCompareTests`
Expected: compile error — `VersionCompare` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Services/VersionCompare.cs`:

```csharp
namespace Daylane.Services;

/// <summary>
/// Compares a GitHub release tag against the running version.
///
/// Versions, never strings: a string comparison makes "1.0.9" newer than "1.0.10", which is
/// exactly the point in a project's life when the bug would first appear and would look like
/// the update check silently breaking.
/// </summary>
internal static class VersionCompare
{
    /// <summary>Parses a release tag, tolerating a leading "v". Returns null for anything that
    /// is not a plain numeric version -- including prerelease suffixes like "1.2.3-beta.1",
    /// which System.Version does not accept and which we do not want to offer anyway.</summary>
    internal static Version? TryParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        return Version.TryParse(trimmed, out Version? parsed) ? parsed : null;
    }

    /// <summary>True only when both sides parse AND the candidate is strictly greater. Every
    /// other case -- unreadable tag, unreadable current version, equal, older -- is "no
    /// update", never an error.</summary>
    internal static bool IsNewer(string? candidateTag, string currentVersion)
    {
        Version? candidate = TryParseTag(candidateTag);
        Version? current = TryParseTag(currentVersion);

        return candidate is not null && current is not null && candidate > current;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 250 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/VersionCompare.cs Daylane.Tests/VersionCompareTests.cs
git commit -S -m "Compare release tags as versions, not strings"
```

---

### Task 3: Release JSON parsing

**Files:**
- Create: `Models/ReleaseInfo.cs`
- Create: `Services/ReleaseFeed.cs`
- Test: `Daylane.Tests/ReleaseFeedTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `internal sealed record ReleaseInfo(string TagName, string HtmlUrl)`, and `internal static ReleaseInfo? ReleaseFeed.Parse(string? json)`.

**`GitHubRelease` must be a positional record with defaulted parameters**, for the source-generation reason in the Global Constraints. It also needs a `[JsonSerializable]` entry on a serializer context.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/ReleaseFeedTests.cs`:

```csharp
using Daylane.Services;

namespace Daylane.Tests;

public class ReleaseFeedTests
{
    [Fact]
    public void Parse_ReadsTheTagAndThePageUrl()
    {
        const string json = """
            {"tag_name":"v1.0.2","html_url":"https://github.com/Marin-Kitagawa/Daylane/releases/tag/v1.0.2","draft":false,"prerelease":false}
            """;

        var release = ReleaseFeed.Parse(json);

        Assert.NotNull(release);
        Assert.Equal("v1.0.2", release.TagName);
        Assert.Equal("https://github.com/Marin-Kitagawa/Daylane/releases/tag/v1.0.2", release.HtmlUrl);
    }

    [Fact]
    public void Parse_RejectsADraft()
    {
        const string json = """
            {"tag_name":"v9.0.0","html_url":"https://example.com/r","draft":true,"prerelease":false}
            """;

        Assert.Null(ReleaseFeed.Parse(json));
    }

    [Fact]
    public void Parse_RejectsAPrerelease()
    {
        const string json = """
            {"tag_name":"v9.0.0","html_url":"https://example.com/r","draft":false,"prerelease":true}
            """;

        Assert.Null(ReleaseFeed.Parse(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    public void Parse_ReturnsNullForAnythingUnusable(string? json)
    {
        // Rate-limit bodies are valid JSON with no tag_name. They must read as "nothing to
        // report", not as an exception escaping into a background thread.
        Assert.Null(ReleaseFeed.Parse(json));
    }

    [Fact]
    public void Parse_ReturnsNullWhenTheTagIsPresentButThereIsNoUrl()
    {
        const string json = """{"tag_name":"v1.0.2","draft":false,"prerelease":false}""";

        // Offering an update with nowhere to send the user is worse than offering none.
        Assert.Null(ReleaseFeed.Parse(json));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter ReleaseFeedTests`
Expected: compile error — `ReleaseFeed` does not exist.

- [ ] **Step 3: Write the model**

Create `Models/ReleaseInfo.cs`:

```csharp
namespace Daylane.Models;

/// <summary>The two things Daylane uses from a GitHub release: what it is called, and where to
/// send the user. Deliberately not the release body -- Daylane does not render release notes,
/// and doing so would mean rendering untrusted Markdown from the network.</summary>
internal sealed record ReleaseInfo(string TagName, string HtmlUrl);
```

- [ ] **Step 4: Write the parser**

Create `Services/ReleaseFeed.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Daylane.Models;

namespace Daylane.Services;

/// <summary>
/// Turns a GitHub "latest release" response into a <see cref="ReleaseInfo"/>, or into null.
///
/// Every unusable input -- malformed JSON, a rate-limit body, a draft, a prerelease, a release
/// with no download page -- returns null rather than throwing. This runs on a background thread
/// during startup, so an escaping exception would be a crash, and there is nothing a user could
/// do about any of these cases anyway.
/// </summary>
internal static class ReleaseFeed
{
    internal static ReleaseInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        GitHubRelease? release;
        try
        {
            release = JsonSerializer.Deserialize(json, ReleaseJsonContext.Default.GitHubRelease);
        }
        catch (JsonException)
        {
            return null;
        }

        if (release is null
            || release.Draft
            || release.Prerelease
            || string.IsNullOrWhiteSpace(release.TagName)
            || string.IsNullOrWhiteSpace(release.HtmlUrl))
        {
            return null;
        }

        return new ReleaseInfo(release.TagName.Trim(), release.HtmlUrl.Trim());
    }
}

/// <summary>Positional record with defaulted parameters, not init-only properties: see the doc
/// comment on DaylaneSettings for why source-generated deserialization needs a constructor.</summary>
internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName = null,
    [property: JsonPropertyName("html_url")] string? HtmlUrl = null,
    [property: JsonPropertyName("draft")] bool Draft = false,
    [property: JsonPropertyName("prerelease")] bool Prerelease = false);

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 261 tests.

- [ ] **Step 6: Commit**

```bash
git add Models/ReleaseInfo.cs Services/ReleaseFeed.cs Daylane.Tests/ReleaseFeedTests.cs
git commit -S -m "Parse the GitHub release response, failing to null

Rate-limit bodies are valid JSON with no tag, and this runs on a
background thread at startup, so every unusable input has to read as
nothing to report rather than as an exception."
```

---

### Task 4: The update checker, and the default-off proof

**Files:**
- Create: `Services/UpdateChecker.cs`
- Test: `Daylane.Tests/UpdateCheckerTests.cs`

**Interfaces:**
- Consumes: `VersionCompare.IsNewer`, `ReleaseFeed.Parse`, `ReleaseInfo`, `DaylaneSettings.CheckForUpdates`, `DaylaneSettings.LastSeenVersion`.
- Produces:
  - `internal enum UpdateCheckOutcome { Skipped, UpToDate, UpdateAvailable, Failed }`
  - `internal sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release = null)`
  - `internal sealed class UpdateChecker(Func<CancellationToken, Task<string?>> fetch, string currentVersion)`
  - `internal Task<UpdateCheckResult> UpdateChecker.CheckAsync(DaylaneSettings settings, bool userRequested, CancellationToken cancellationToken)`

**The HTTP call arrives as a delegate.** That is the whole seam: everything above it is tested with canned strings, and the delegate itself is one file added in Task 5. No test in this project ever touches the network.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/UpdateCheckerTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class UpdateCheckerTests
{
    private const string NewerJson = """
        {"tag_name":"v2.0.0","html_url":"https://example.com/r/2","draft":false,"prerelease":false}
        """;

    private static string Json(string tag) => $$"""
        {"tag_name":"{{tag}}","html_url":"https://example.com/r","draft":false,"prerelease":false}
        """;

    [Fact]
    public async Task WithDefaultSettings_NoRequestIsEverMade()
    {
        // THE promise of this sub-project. If this test fails, a default install has started
        // talking to GitHub and the README is false.
        bool fetched = false;
        var checker = new UpdateChecker(_ => { fetched = true; return Task.FromResult<string?>(NewerJson); }, "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(new DaylaneSettings(), userRequested: false, CancellationToken.None);

        Assert.False(fetched);
        Assert.Equal(UpdateCheckOutcome.Skipped, result.Outcome);
    }

    [Fact]
    public async Task AUserRequestedCheckRunsEvenWhenTheSettingIsOff()
    {
        // Pressing a button is an explicit act. The toggle governs automatic checks.
        bool fetched = false;
        var checker = new UpdateChecker(_ => { fetched = true; return Task.FromResult<string?>(NewerJson); }, "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(new DaylaneSettings(), userRequested: true, CancellationToken.None);

        Assert.True(fetched);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task WhenEnabledAndANewerReleaseExists_ItIsReported()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(NewerJson), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: false, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("v2.0.0", result.Release!.TagName);
        Assert.Equal("https://example.com/r/2", result.Release.HtmlUrl);
    }

    [Fact]
    public async Task WhenTheReleaseIsNotNewer_ItReportsUpToDate()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(Json("v1.0.1")), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: false, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task AlreadySeenRelease_ReportsUpToDateRatherThanNagging()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true, LastSeenVersion = "v2.0.0" };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(NewerJson), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: false, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task AReleaseNewerThanTheOneAlreadySeen_IsStillReported()
    {
        // Suppression must not become "never tell me again".
        var settings = new DaylaneSettings() with { CheckForUpdates = true, LastSeenVersion = "v1.5.0" };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(NewerJson), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: false, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task AUserRequestedCheckIgnoresSuppression()
    {
        // If you press the button, you get the answer, even about a release you dismissed.
        var settings = new DaylaneSettings() with { CheckForUpdates = true, LastSeenVersion = "v2.0.0" };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(NewerJson), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: true, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    [Fact]
    public async Task AFetchThatThrows_ReportsFailedRatherThanPropagating()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(
            _ => Task.FromException<string?>(new HttpRequestException("offline")), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: true, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AFetchThatReturnsNull_ReportsFailed()
    {
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>(null), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: true, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AnUnparseableBody_ReportsUpToDateNotFailed()
    {
        // We reached GitHub and it told us something we cannot use. That is not a failure to
        // report to the user -- there is no newer release we can offer, which is "up to date".
        var settings = new DaylaneSettings() with { CheckForUpdates = true };
        var checker = new UpdateChecker(_ => Task.FromResult<string?>("{\"message\":\"rate limited\"}"), "1.0.1");

        UpdateCheckResult result = await checker.CheckAsync(settings, userRequested: true, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter UpdateCheckerTests`
Expected: compile error — `UpdateChecker` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Services/UpdateChecker.cs`:

```csharp
using Daylane.Models;

namespace Daylane.Services;

internal enum UpdateCheckOutcome
{
    /// <summary>No request was made. The setting is off and the user did not ask.</summary>
    Skipped,
    UpToDate,
    UpdateAvailable,
    /// <summary>We tried and could not reach GitHub. Not an error the user must act on.</summary>
    Failed
}

internal sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo? Release = null);

/// <summary>
/// Decides whether to look for a newer release, and what to make of the answer.
///
/// The network call arrives as a delegate rather than an HttpClient held here, which is what
/// lets every branch of this class be tested without a socket -- including the one that matters
/// most, that a default install never calls the delegate at all.
/// </summary>
internal sealed class UpdateChecker(
    Func<CancellationToken, Task<string?>> fetchLatestReleaseJson,
    string currentVersion)
{
    /// <param name="userRequested">True when the user pressed Check now. An explicit act, so it
    /// runs regardless of the setting and ignores the already-seen suppression.</param>
    internal async Task<UpdateCheckResult> CheckAsync(
        DaylaneSettings settings,
        bool userRequested,
        CancellationToken cancellationToken)
    {
        if (!userRequested && !settings.CheckForUpdates)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Skipped);
        }

        string? json;
        try
        {
            json = await fetchLatestReleaseJson(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Offline, DNS, TLS, timeout, rate-limit-as-status: all the same to a user who
            // cannot act on any of them, and none of them may escape a background check.
            return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        }

        if (json is null)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Failed);
        }

        ReleaseInfo? release = ReleaseFeed.Parse(json);
        if (release is null || !VersionCompare.IsNewer(release.TagName, currentVersion))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        }

        // A release the user has already been shown is not news. Compared as a version, not by
        // string equality, so dismissing v1.5.0 does not hide v2.0.0.
        if (!userRequested
            && settings.LastSeenVersion is { } seen
            && !VersionCompare.IsNewer(release.TagName, seen))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        }

        return new UpdateCheckResult(UpdateCheckOutcome.UpdateAvailable, release);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 271 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/UpdateChecker.cs Daylane.Tests/UpdateCheckerTests.cs
git commit -S -m "Add the update checker with the default-off proof

The network call is a delegate, so every branch is testable without a
socket -- including the one that matters, that a default install never
calls it."
```

---

### Task 5: App info for the About panel

**Files:**
- Modify: `Daylane.csproj`
- Create: `Services/AppInfo.cs`
- Test: `Daylane.Tests/AppInfoTests.cs`

**Interfaces:**
- Produces: `AppInfo.Version` (`string`), `AppInfo.Author`, `AppInfo.License`, `AppInfo.RepositoryUrl`, `AppInfo.IssuesUrl`, `AppInfo.LicenseUrl`, `AppInfo.UpstreamProjectUrl` — all `string`.

**The license is AGPL-3.0.** The repository's `LICENSE` is the GNU Affero General Public License v3. Do not write MIT.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/AppInfoTests.cs`:

```csharp
using Daylane.Services;

namespace Daylane.Tests;

public class AppInfoTests
{
    [Fact]
    public void Version_IsAReadableVersionNotAPlaceholder()
    {
        // Reads the real assembly, so this fails if the informational version stops resolving.
        Assert.NotNull(VersionCompare.TryParseTag(AppInfo.Version));
    }

    [Fact]
    public void License_IsTheRepositorysOwnLicence()
    {
        // AGPL-3.0, not the MIT of the project whose feature set this port follows.
        Assert.Contains("AGPL", AppInfo.License, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://github.com/Marin-Kitagawa/Daylane")]
    public void RepositoryUrl_PointsAtThisFork(string expected)
        => Assert.Equal(expected, AppInfo.RepositoryUrl);

    [Fact]
    public void EveryLink_IsAnAbsoluteHttpsUrl()
    {
        foreach (string url in new[]
                 {
                     AppInfo.RepositoryUrl, AppInfo.IssuesUrl,
                     AppInfo.LicenseUrl, AppInfo.UpstreamProjectUrl
                 })
        {
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed), url);
            Assert.Equal(Uri.UriSchemeHttps, parsed!.Scheme);
        }
    }

    [Fact]
    public void Author_IsNotEmpty()
        => Assert.False(string.IsNullOrWhiteSpace(AppInfo.Author));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter AppInfoTests`
Expected: compile error — `AppInfo` does not exist.

- [ ] **Step 3: Add the assembly metadata**

In `Daylane.csproj`, in the first `PropertyGroup`, after `<Version>1.0.1</Version>`:

```xml
    <Authors>Marin Kitagawa</Authors>
    <Product>Daylane</Product>
```

- [ ] **Step 4: Write the implementation**

Create `Services/AppInfo.cs`:

```csharp
using System.Reflection;

namespace Daylane.Services;

/// <summary>
/// Static, offline facts about this build, for the About panel and the update check's
/// User-Agent. Nothing here reads a file or a network.
/// </summary>
internal static class AppInfo
{
    /// <summary>The informational version, with any build metadata suffix removed so it parses
    /// as a plain version for comparison against a release tag.</summary>
    internal static string Version { get; } = ReadVersion();

    internal static string Author =>
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company
        ?? "Marin Kitagawa";

    internal static string License => "AGPL-3.0";

    internal const string RepositoryUrl = "https://github.com/Marin-Kitagawa/Daylane";

    internal const string IssuesUrl = "https://github.com/Marin-Kitagawa/Daylane/issues";

    internal const string LicenseUrl =
        "https://github.com/Marin-Kitagawa/Daylane/blob/main/LICENSE";

    /// <summary>The project this port draws its feature set from. Named because it is the
    /// honest provenance of a parity port, and it costs nothing to say so.</summary>
    internal const string UpstreamProjectUrl = "https://github.com/Tomotsugu-dev/Hindsight";

    private static string ReadVersion()
    {
        string? informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // "1.0.1+abc123" -> "1.0.1". The csproj disables source-revision suffixes, but a
        // published build can still carry one and a tag comparison must not trip over it.
        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 277 tests.

- [ ] **Step 6: Commit**

```bash
git add Daylane.csproj Services/AppInfo.cs Daylane.Tests/AppInfoTests.cs
git commit -S -m "Add offline app info for the About panel

The licence is Daylane's own AGPL-3.0, not the MIT of the project whose
feature set this port follows."
```

---

### Task 6: The one file that touches the network

**Files:**
- Create: `Services/ReleaseFetch.cs`
- Test: none. See below.

**Interfaces:**
- Produces: `internal static readonly string ReleaseFetch.LatestReleaseUrl`, and `internal static Task<string?> ReleaseFetch.LatestReleaseJsonAsync(CancellationToken cancellationToken)` — the delegate `UpdateChecker` takes.

**Deliberately untested, and say so in your report.** A test here would either hit the network (which this project never does) or assert that `HttpClient` was configured the way the code configures it, which proves nothing. The seam exists so the logic on both sides is tested; this file is the seam itself. What it must get right is the request shape, which a reviewer verifies by reading it.

- [ ] **Step 1: Write the implementation**

Create `Services/ReleaseFetch.cs`:

```csharp
using System.Net.Http;
using System.Net.Http.Headers;

namespace Daylane.Services;

/// <summary>
/// The only code in Daylane that contacts a network, and it runs solely when the user has
/// turned the update check on or pressed Check now.
///
/// What leaves the machine, in full: one unauthenticated HTTPS GET to the URL below, carrying a
/// User-Agent of "Daylane/&lt;version&gt;" (GitHub rejects requests without one) and an Accept
/// header. No account, no token, no identifier, no telemetry. GitHub observes the requesting IP
/// and that version string, as it would for any download. README.md documents exactly this.
/// </summary>
internal static class ReleaseFetch
{
    // This fork, not the mirbyte/Daylane upstream it was branched from: the shipped build
    // contains work upstream does not have, so pointing there would offer a downgrade.
    internal const string LatestReleaseUrl =
        "https://api.github.com/repos/Marin-Kitagawa/Daylane/releases/latest";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // A check must never be the reason startup feels slow. Ten seconds is generous for
            // one small GET and short enough that nobody waits on it.
            Timeout = TimeSpan.FromSeconds(10)
        };

        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Daylane", AppInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    /// <summary>Returns the response body, or null if the request failed or GitHub returned a
    /// non-success status. Never throws: the caller treats null as "could not reach GitHub".</summary>
    internal static async Task<string?> LatestReleaseJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await Client.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

- [ ] **Step 2: Verify the build**

Run: `dotnet build Daylane.csproj --no-incremental`
Expected: clean, zero warnings. `AppInfo.Version` comes from Task 5, which lands before this one.

- [ ] **Step 3: Run the suite**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, unchanged count.

- [ ] **Step 4: Commit**

```bash
git add Services/ReleaseFetch.cs
git commit -S -m "Add the single network call behind the update seam

One unauthenticated GET with a version-bearing User-Agent, a ten second
timeout, and no path by which a failure reaches the caller."
```

---

### Task 7: Purge recorded activity

**Files:**
- Modify: `Services/DailyStatsStore.cs`
- Test: `Daylane.Tests/PurgeTests.cs`

**Interfaces:**
- Produces: `internal int DailyStatsStore.PurgeRecordedActivity()` — returns the number of rows deleted across the capture tables.

**Deleted:** `ActivitySegment`, `OpenAppSegment`, `DailyInput`, `ProcessPaths`, `AppIcons`, `SyncOutbox`; `SyncCursor` is emptied.
**Never touched:** `SettingsStore`, `Devices`, `AuthState`, `Categories`, `SuperCategories`, `AppGroups`, `AppGroupMembers`.

`VACUUM` runs **after** the transaction commits, in a separate command — SQLite forbids `VACUUM` inside a transaction. Without it a purge reclaims no disk space and looks broken; Hindsight shipped exactly that bug and fixed it in v0.6.7.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/PurgeTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class PurgeTests
{
    private static ForegroundApp App(string process) =>
        new(process, $@"C:\Apps\{process}.exe", process, false);

    private static long Count(TempDatabase temp, string table)
    {
        using var connection = temp.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Purge_EmptiesTheCaptureTables()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        long id = store.OpenSegment(App("chrome"), start);
        store.CloseSegment(id, start.AddMinutes(1), 3, 4);
        store.Flush();
        Assert.Equal(1L, Count(temp, "ActivitySegment"));

        int deleted = store.PurgeRecordedActivity();

        Assert.Equal(0L, Count(temp, "ActivitySegment"));
        Assert.True(deleted >= 1, $"expected at least the one segment to be counted, got {deleted}");
    }

    [Fact]
    public void Purge_LeavesSettingsAlone()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        var settings = new SettingsService(temp.ConnectionString);
        settings.Update(s => s with { RetentionDays = 45, CheckForUpdates = true });

        store.PurgeRecordedActivity();

        // Clearing your history must not clear your preferences.
        var reread = new SettingsService(temp.ConnectionString).Current;
        Assert.Equal(45, reread.RetentionDays);
        Assert.True(reread.CheckForUpdates);
    }

    [Fact]
    public void Purge_LeavesThisDevicesIdentityAlone()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        long before = Count(temp, "Devices");
        Assert.True(before >= 1, "the store seeds a self device row on open");

        store.PurgeRecordedActivity();

        Assert.Equal(before, Count(temp, "Devices"));
    }

    [Fact]
    public void Purge_OnAnEmptyDatabase_ReportsNothingDeletedAndDoesNotThrow()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        Assert.Equal(0, store.PurgeRecordedActivity());
    }

    [Fact]
    public void Purge_IsRepeatable()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        store.CloseSegment(store.OpenSegment(App("chrome"), start), start.AddMinutes(1), 0, 0);

        store.PurgeRecordedActivity();
        store.PurgeRecordedActivity();

        Assert.Equal(0L, Count(temp, "ActivitySegment"));
    }

    [Fact]
    public void Purge_LeavesTheDatabaseUsable()
    {
        // VACUUM rebuilds the file. If it left the connection or the schema in a bad state,
        // the very next write would fail -- which is the failure a user would hit immediately.
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        store.PurgeRecordedActivity();

        DateTime start = DateTime.UtcNow;
        long id = store.OpenSegment(App("notepad"), start);
        store.CloseSegment(id, start.AddMinutes(1), 1, 1);
        store.Flush();

        Assert.Equal(1L, Count(temp, "ActivitySegment"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter PurgeTests`
Expected: compile error — `PurgeRecordedActivity` does not exist.

- [ ] **Step 3: Write the implementation**

Add to `Services/DailyStatsStore.cs`, beside `PruneOldData`:

```csharp
    // Recorded activity and the caches derived from it. Deliberately NOT SettingsStore,
    // Devices, AuthState, Categories, SuperCategories, AppGroups or AppGroupMembers: a user
    // clearing their history does not expect to lose their preferences, this device's
    // identity, or a taxonomy they built by hand.
    private static readonly string[] PurgedTables =
    [
        "ActivitySegment",
        "OpenAppSegment",
        "DailyInput",
        "ProcessPaths",
        "AppIcons",
        "SyncOutbox",
        "SyncCursor"
    ];

    /// <summary>
    /// Deletes all recorded activity and returns the number of rows removed.
    ///
    /// One transaction, so a failure leaves the history intact rather than half-deleted. The
    /// VACUUM afterwards is not optional housekeeping: SQLite does not return freed pages to
    /// the filesystem on DELETE, so without it the file does not shrink and a user who just
    /// cleared a year of history sees the same number of megabytes and concludes it did not
    /// work.
    /// </summary>
    internal int PurgeRecordedActivity()
    {
        lock (_dbWriteLock)
        {
            int deleted = 0;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();

                foreach (string table in PurgedTables)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {table};";
                    deleted += command.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            // Separate connection and no transaction: SQLite rejects VACUUM inside one. A
            // failure here is not a failed purge -- the rows are already gone -- so it must
            // not throw away the count or surface as an error.
            try
            {
                using var vacuum = new SqliteConnection(_connectionString);
                vacuum.Open();
                using var command = vacuum.CreateCommand();
                command.CommandText = "VACUUM;";
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VACUUM after purge failed: {ex.Message}");
            }

            return deleted;
        }
    }
```

`SyncCursor` is in the delete list because its rows are pointers into data that no longer exists. If it holds a single seeded row that other code expects, delete its contents and say so in your report — do not silently drop it from the list.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 283 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/DailyStatsStore.cs Daylane.Tests/PurgeTests.cs
git commit -S -m "Purge recorded activity, keeping settings and identity

VACUUM runs after the commit because SQLite forbids it inside a
transaction, and without it the file never shrinks and the purge looks
like it did nothing."
```

---

### Task 8: Storage usage

**Files:**
- Create: `Services/StorageUsage.cs`
- Test: `Daylane.Tests/StorageUsageTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed record StorageReport(long? DatabaseBytes, long RecordedRows, string DatabasePath)`
  - `internal static StorageReport StorageUsage.Measure(string databasePath, string connectionString)`

`DatabaseBytes` is **nullable on purpose**: if the file cannot be stat'ed, the answer is unknown, and reporting `0` would be a claim rather than the truth.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/StorageUsageTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class StorageUsageTests
{
    private static ForegroundApp App(string process) =>
        new(process, $@"C:\Apps\{process}.exe", process, false);

    [Fact]
    public void Measure_ReportsTheDatabaseSizeAndItsPath()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        StorageReport report = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString);

        Assert.Equal(temp.DatabasePath, report.DatabasePath);
        Assert.NotNull(report.DatabaseBytes);
        Assert.True(report.DatabaseBytes > 0, "an opened database is not zero bytes");
    }

    [Fact]
    public void Measure_IncludesTheWalFileWhenPresent()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        for (int i = 0; i < 40; i++)
        {
            long id = store.OpenSegment(App($"app{i}"), start.AddSeconds(i));
            store.CloseSegment(id, start.AddSeconds(i + 1), 1, 1);
        }
        store.Flush();

        long walBytes = new FileInfo(temp.DatabasePath + "-wal") is { Exists: true } wal ? wal.Length : 0;
        StorageReport report = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString);

        // The point of the test: the reported figure is the total of the database's files, so a
        // WAL that has grown is visible rather than silently omitted.
        long mainBytes = new FileInfo(temp.DatabasePath).Length;
        Assert.Equal(mainBytes + walBytes + ShmBytes(temp.DatabasePath), report.DatabaseBytes);
    }

    private static long ShmBytes(string databasePath) =>
        new FileInfo(databasePath + "-shm") is { Exists: true } shm ? shm.Length : 0;

    [Fact]
    public void Measure_CountsRecordedRows()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        long a = store.OpenSegment(App("chrome"), start);
        store.CloseSegment(a, start.AddMinutes(1), 0, 0);
        long b = store.OpenSegment(App("notepad"), start.AddMinutes(2));
        store.CloseSegment(b, start.AddMinutes(3), 0, 0);
        store.Flush();

        StorageReport report = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString);

        Assert.True(report.RecordedRows >= 2,
            $"two segments plus today's input row should count at least 2, got {report.RecordedRows}");
    }

    [Fact]
    public void Measure_ReportsUnknownRatherThanZeroForAMissingFile()
    {
        using var temp = new TempDatabase();
        string missing = Path.Combine(temp.DatabasePath + "-nope", "daylane.db");

        StorageReport report = StorageUsage.Measure(missing, temp.ConnectionString);

        // Zero is a claim. Unknown is the truth.
        Assert.Null(report.DatabaseBytes);
    }

    [Fact]
    public void Measure_ReportsZeroRowsAfterAPurge()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        store.CloseSegment(store.OpenSegment(App("chrome"), start), start.AddMinutes(1), 0, 0);
        store.Flush();
        store.PurgeRecordedActivity();

        Assert.Equal(0, StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString).RecordedRows);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter StorageUsageTests`
Expected: compile error — `StorageUsage` does not exist.

- [ ] **Step 3: Write the implementation**

Create `Services/StorageUsage.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace Daylane.Services;

/// <param name="DatabaseBytes">Null when the files cannot be measured. Reporting 0 for an
/// unreadable file would be a claim; null is the truth, and the UI says "unavailable".</param>
internal sealed record StorageReport(long? DatabaseBytes, long RecordedRows, string DatabasePath);

/// <summary>
/// How much disk the database occupies and how many rows it holds.
///
/// The size is the sum of the main file, the WAL and the shared-memory file, not just the main
/// file. Daylane runs in WAL mode and an un-checkpointed WAL can be larger than the database
/// itself, so reporting the main file alone would understate the real footprint -- sometimes
/// by more than it reports.
/// </summary>
internal static class StorageUsage
{
    private static readonly string[] CountedTables =
        ["ActivitySegment", "OpenAppSegment", "DailyInput"];

    internal static StorageReport Measure(string databasePath, string connectionString) =>
        new(MeasureBytes(databasePath), CountRows(connectionString), databasePath);

    private static long? MeasureBytes(string databasePath)
    {
        try
        {
            var main = new FileInfo(databasePath);
            if (!main.Exists)
            {
                return null;
            }

            long total = main.Length;
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = new FileInfo(databasePath + suffix);
                if (sidecar.Exists)
                {
                    total += sidecar.Length;
                }
            }

            return total;
        }
        catch (Exception)
        {
            // Locked, denied, or a path we cannot stat. Unknown, not zero.
            return null;
        }
    }

    private static long CountRows(string connectionString)
    {
        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            long total = 0;
            foreach (string table in CountedTables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                total += (long)(command.ExecuteScalar() ?? 0L);
            }

            return total;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 288 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/StorageUsage.cs Daylane.Tests/StorageUsageTests.cs
git commit -S -m "Report database size including the WAL

An un-checkpointed WAL can exceed the main file, so measuring only
daylane.db would understate the footprint. An unreadable file reports
unknown rather than zero."
```

---

### Task 9: Drive purge and storage through the tracking service

**Files:**
- Modify: `Services/TrackingService.cs`
- Test: `Daylane.Tests/PurgeIntegrationTests.cs`

**Interfaces:**
- Consumes: `DailyStatsStore.PurgeRecordedActivity`, `StorageUsage.Measure`.
- Produces: `internal int TrackingService.PurgeRecordedActivity()` and `internal StorageReport TrackingService.MeasureStorage()`.

**The open segment must be closed first, and this is the whole point of the task.** `TrackingService` holds `_openSegment` with a row id. Purging while a segment is open deletes the row that id points at, and the next `FlushOpenSegmentCounts` writes counts against a row that no longer exists — a silent no-op that leaves the in-memory state permanently disagreeing with the database.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/PurgeIntegrationTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class PurgeIntegrationTests
{
    private static ForegroundApp App(string process) =>
        new(process, $@"C:\Apps\{process}.exe", process, false);

    private static long SegmentCount(TempDatabase temp)
    {
        using var connection = temp.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActivitySegment;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void PurgingWithAnOpenSegment_LeavesNoRowBehindAndNoDanglingId()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);

        // An open segment: opened, never closed. This is the state the tracker is in almost
        // always, so it is the state a purge has to survive.
        long openId = store.OpenSegment(App("chrome"), start);
        Assert.Equal(1L, SegmentCount(temp));

        store.CloseSegment(openId, DateTime.UtcNow, 0, 0);
        store.Flush();
        store.PurgeRecordedActivity();

        Assert.Equal(0L, SegmentCount(temp));

        // The id is now dangling. Writing counts against it must not resurrect a row.
        store.UpdateOpenSegmentCounts(openId, 5, 5);
        Assert.Equal(0L, SegmentCount(temp));
    }

    [Fact]
    public void MeasureStorage_AfterAPurge_ReportsNoRows()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        store.CloseSegment(store.OpenSegment(App("chrome"), start), start.AddMinutes(1), 0, 0);
        store.Flush();

        store.PurgeRecordedActivity();
        StorageReport report = StorageUsage.Measure(temp.DatabasePath, temp.ConnectionString);

        Assert.Equal(0, report.RecordedRows);
        Assert.NotNull(report.DatabaseBytes);
    }
}
```

These tests drive `DailyStatsStore` rather than `TrackingService`, because `TrackingService`'s constructor opens a real database next to the executable and starts OS hooks and timers — it cannot be constructed in a unit test. **State that limitation plainly in your report**; do not write a test that pretends to cover the service.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter PurgeIntegrationTests`
Expected: FAIL or compile error, depending on whether Task 7 has landed.

- [ ] **Step 3: Add the service methods**

In `Services/TrackingService.cs`:

```csharp
    /// <summary>
    /// Closes whatever is open, deletes all recorded activity, and resets the live counters.
    ///
    /// The close must come first: this service holds an open segment's row id, and purging
    /// underneath it would leave the next flush writing counts against a row that no longer
    /// exists -- a silent no-op that leaves memory and the database permanently disagreeing.
    /// </summary>
    internal int PurgeRecordedActivity()
    {
        CloseOpenSegment(DateTime.UtcNow);
        CloseAllOpenApps(DateTime.UtcNow);
        _store.Flush();

        int deleted = _store.PurgeRecordedActivity();

        lock (_rolloverLock)
        {
            _keyPressCount = 0;
            _mouseClickCount = 0;
            _currentDateKey = TodayKey();
        }

        // Force, so the UI shows zeroes now rather than at the next five-second tick.
        PublishActivity(force: true);
        return deleted;
    }

    internal StorageReport MeasureStorage() =>
        StorageUsage.Measure(_store.DatabasePath, _store.ConnectionString);
```

If `CloseAllOpenApps` or `_rolloverLock` is named differently in the file, use the real names — read the surrounding code rather than trusting these.

- [ ] **Step 4: Run the tests and the build**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj` then `dotnet build Daylane.csproj --no-incremental`
Expected: PASS, 290 tests; build clean, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add Services/TrackingService.cs Daylane.Tests/PurgeIntegrationTests.cs
git commit -S -m "Close the open segment before purging

The service holds an open segment's row id; deleting underneath it would
leave the next flush writing counts against a row that is gone."
```

---

### Task 10: View-model surface

**Files:**
- Modify: `ViewModels/MainWindowViewModel.cs`
- Modify: `ViewModels/SettingsChangeNotifier.cs`
- Test: `Daylane.Tests/SettingsChangeNotifierTests.cs` (existing — extend)

**Interfaces:**
- Produces, on `MainWindowViewModel`: `CheckForUpdates` (bool, two-way), `UpdateStatus` (string), `UpdateLinkUrl` (string?), `HasUpdateLink` (bool), `CheckForUpdatesCommand`, `OpenUpdateLinkCommand`, `PurgeDataCommand`, `ConfirmPurgeVisible` (bool), `ConfirmPurgeCommand`, `CancelPurgeCommand`, `StorageSummary` (string), `RecordedRowsSummary` (string), `DatabasePathDisplay` (string), plus `AppVersion`, `AppAuthor`, `AppLicense` and the four link commands for About.

Follow the existing settings-property shape exactly: getter reads `_settings.Current.X`, setter early-returns on no change, calls `_settings.Update`, then `OnPropertyChanged()`.

- [ ] **Step 1: Extend the notifier test**

In `Daylane.Tests/SettingsChangeNotifierTests.cs`, add:

```csharp
    [Fact]
    public void Properties_IncludeTheUpdateCheckToggle()
    {
        // A settings change from any writer must refresh this control, not only a change made
        // by this view model's own setter.
        Assert.Contains(nameof(MainWindowViewModel.CheckForUpdates), SettingsChangeNotifier.Properties);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter SettingsChangeNotifierTests`
Expected: FAIL — the name is not in the list.

- [ ] **Step 3: Add the notifier entry**

In `ViewModels/SettingsChangeNotifier.cs`, add `nameof(MainWindowViewModel.CheckForUpdates),` to the array, above the comment block explaining the two deliberate omissions. Do not add `StorageSummary` or any About property: those are not settings-backed.

- [ ] **Step 4: Add the update surface**

In `ViewModels/MainWindowViewModel.cs`:

```csharp
    public bool CheckForUpdates
    {
        get => _settings.Current.CheckForUpdates;
        set
        {
            if (_settings.Current.CheckForUpdates == value)
            {
                return;
            }

            _settings.Update(s => s with { CheckForUpdates = value });
            OnPropertyChanged();
        }
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (_updateStatus == value)
            {
                return;
            }

            _updateStatus = value;
            OnPropertyChanged();
        }
    }

    public string? UpdateLinkUrl
    {
        get => _updateLinkUrl;
        private set
        {
            if (_updateLinkUrl == value)
            {
                return;
            }

            _updateLinkUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUpdateLink));
        }
    }

    public bool HasUpdateLink => !string.IsNullOrWhiteSpace(_updateLinkUrl);

    private async Task RunUpdateCheckAsync(bool userRequested)
    {
        UpdateStatus = "Checking…";

        var checker = new UpdateChecker(ReleaseFetch.LatestReleaseJsonAsync, AppInfo.Version);
        UpdateCheckResult result = await checker
            .CheckAsync(_settings.Current, userRequested, CancellationToken.None)
            .ConfigureAwait(true);

        switch (result.Outcome)
        {
            case UpdateCheckOutcome.UpdateAvailable when result.Release is { } release:
                UpdateStatus = $"Version {release.TagName} is available. You have {AppInfo.Version}.";
                UpdateLinkUrl = release.HtmlUrl;
                _settings.Update(s => s with { LastSeenVersion = release.TagName });
                break;

            case UpdateCheckOutcome.UpToDate:
                UpdateStatus = $"Daylane {AppInfo.Version} is up to date.";
                UpdateLinkUrl = null;
                break;

            case UpdateCheckOutcome.Failed:
                UpdateStatus = "Could not reach GitHub.";
                UpdateLinkUrl = null;
                break;

            default:
                UpdateStatus = "Automatic checks are off.";
                UpdateLinkUrl = null;
                break;
        }
    }
```

Back the two new fields with `private string _updateStatus = "Automatic checks are off.";` and `private string? _updateLinkUrl;`. Wire `CheckForUpdatesCommand` to `RunUpdateCheckAsync(userRequested: true)` and `OpenUpdateLinkCommand` to the existing shell-open pattern.

**Kick off the startup check from the constructor without blocking it**, and only when enabled:

```csharp
        // Fire-and-forget on purpose: an update check must never delay the window appearing,
        // and its failure is already a no-op. Guarded so a default install makes no request.
        if (Settings.Current.CheckForUpdates)
        {
            _ = RunUpdateCheckAsync(userRequested: false);
        }
```

- [ ] **Step 5: Add the data and About surface**

```csharp
    public bool ConfirmPurgeVisible
    {
        get => _confirmPurgeVisible;
        private set
        {
            if (_confirmPurgeVisible == value)
            {
                return;
            }

            _confirmPurgeVisible = value;
            OnPropertyChanged();
        }
    }

    public string StorageSummary
    {
        get => _storageSummary;
        private set
        {
            if (_storageSummary == value)
            {
                return;
            }

            _storageSummary = value;
            OnPropertyChanged();
        }
    }

    public string AppVersion => AppInfo.Version;

    public string AppAuthor => AppInfo.Author;

    public string AppLicense => AppInfo.License;

    private void RefreshStorage()
    {
        StorageReport report = _tracking.MeasureStorage();

        string size = report.DatabaseBytes is { } bytes
            ? FormatBytes(bytes)
            : "unavailable";

        StorageSummary = $"{size} · {report.RecordedRows:N0} rows recorded";
        DatabasePathDisplay = report.DatabasePath;
        OnPropertyChanged(nameof(DatabasePathDisplay));
    }

    private void ConfirmPurge()
    {
        int deleted = _tracking.PurgeRecordedActivity();
        ConfirmPurgeVisible = false;
        StorageSummary = $"Deleted {deleted:N0} rows.";
        RefreshDay();
        RefreshInsights();
        RefreshStorage();
    }

    /// <summary>Binary units, because that is what a file manager shows for the same file.</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
```

`PurgeDataCommand` sets `ConfirmPurgeVisible = true`; `CancelPurgeCommand` sets it false; `ConfirmPurgeCommand` calls `ConfirmPurge`. Call `RefreshStorage()` when the Settings tab becomes selected, **not on a timer** — the spec rejects Hindsight's 30-second poll for two `stat` calls behind a tab nobody is looking at.

Add `private bool _confirmPurgeVisible;` and `private string _storageSummary = "…";` and `public string DatabasePathDisplay { get; private set; } = "";`. Use the existing `RelayCommand` for every command, matching how the file already builds them.

- [ ] **Step 6: Run the suite and the build**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj` then `dotnet build Daylane.csproj --no-incremental`
Expected: PASS, 291 tests; build clean, zero warnings.

- [ ] **Step 7: Commit**

```bash
git add ViewModels/MainWindowViewModel.cs ViewModels/SettingsChangeNotifier.cs Daylane.Tests/SettingsChangeNotifierTests.cs
git commit -S -m "Add the view-model surface for updates, data and About

The startup check is fire-and-forget and guarded on the setting, so it
cannot delay the window and a default install makes no request."
```

---

### Task 11: The three settings panels

**Files:**
- Modify: `MainWindow.axaml`
- Test: none — presentation over logic already covered. Say so in your report.

Add three panels after the existing Privacy panel, each a `<Border Classes="panel" Padding="18">` containing a `<StackPanel Spacing="14">` whose first child is a `<TextBlock FontSize="13" FontWeight="SemiBold" />` heading, matching the Appearance / Tracking / Startup / Privacy panels exactly. Read the Privacy panel first and copy its structure.

- [ ] **Step 1: Add the Updates panel**

Heading **Updates**. Contents:

- A `ToggleSwitch` bound to `CheckForUpdates`, labelled **Check for updates**, with the muted description: `"Off by default. When on, Daylane asks GitHub once per launch whether a newer release exists. Nothing else is sent."`
- A `TextBlock` bound to `UpdateStatus`.
- A **Check now** `Button` bound to `CheckForUpdatesCommand`, with a muted line below: `"Checks once, even when the switch is off."`
- A **Download** `Button` bound to `OpenUpdateLinkCommand`, with `IsVisible="{Binding HasUpdateLink}"`.

- [ ] **Step 2: Add the Data panel**

Heading **Data**. Contents:

- `StorageSummary` and `DatabasePathDisplay` as text, the path with `TextWrapping="Wrap"`, plus an **Open folder** `Button` bound to the existing `OpenDataFolderCommand`.
- A muted line: `"Daylane prunes automatically when a retention window is set, and keeps everything when it is not."` Do not write "the database is never auto-cleaned" — `RetentionPruner` has done exactly that since sub-project 1, so that sentence would be false here even though it is true in the project this port follows.
- A **Delete recorded activity** `Button` (`Classes="secondary"`) bound to `PurgeDataCommand`.
- An inline confirmation, `IsVisible="{Binding ConfirmPurgeVisible}"`, reading: `"This permanently deletes all recorded activity on this computer. It cannot be undone. Your settings are kept."` with **Delete everything** (`ConfirmPurgeCommand`) and **Cancel** (`CancelPurgeCommand`) buttons.

The confirmation copy must not mention sync, the cloud, or re-downloading. Hindsight's says *"the next sync will re-pull all history from the cloud"* even for users who never configured sync — false for almost everyone reading it.

- [ ] **Step 3: Add the About panel**

Heading **About**. Contents: `AppVersion`, `AppAuthor`, `AppLicense` as labelled rows; buttons or hyperlink-styled buttons for **Repository**, **Report an issue** and **License**; and one muted line naming Hindsight as the project this port's feature set follows, linking to it.

- [ ] **Step 4: Verify**

Run the colour-literal gate:

```bash
grep -rnE "Brushes\.[A-Za-z]+|Color\.(Parse|FromRgb)|Colors\.|#[0-9A-Fa-f]{6}" Controls/ ViewModels/ App.axaml MainWindow.axaml
```

Expected: only `App.axaml`'s token definitions and `Controls/TimelinePalette.cs`'s magenta fallback. Then `dotnet build Daylane.csproj --no-incremental` (clean, zero warnings) and `dotnet test Daylane.Tests/Daylane.Tests.csproj` (green).

- [ ] **Step 5: Commit**

```bash
git add MainWindow.axaml
git commit -S -m "Add the Updates, Data and About settings panels"
```

---

### Task 12: README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Document the update check**

Add a short section stating, in these terms:

- The check is **off by default**; a default install contacts nothing.
- When enabled, Daylane sends one unauthenticated HTTPS `GET` to
  `https://api.github.com/repos/Marin-Kitagawa/Daylane/releases/latest`, once per launch.
- The request carries a `User-Agent` of `Daylane/<version>` and an `Accept` header, and nothing else. No account, no token, no identifier. GitHub sees the requesting IP and that version string.
- Daylane never downloads or installs an update; it links to the release page.
- **Check now** performs a single check even while the switch is off, because pressing a button is an explicit act.

The version-in-User-Agent detail is the one thing a reader would not otherwise expect, so it must be stated rather than implied.

- [ ] **Step 2: Document data management and About**

Add the new Settings controls to whatever list the README already keeps of them, using the in-app labels. State that deleting recorded activity keeps settings, and that the database file shrinks because the purge vacuums.

- [ ] **Step 3: Repoint the badges**

The badges at the top currently point at `mirbyte/Daylane`, the upstream this repo forked from, so the download-count and latest-release badges describe a different build than the one shipped here. Repoint every `mirbyte/Daylane` occurrence to `Marin-Kitagawa/Daylane`.

- [ ] **Step 4: Re-read the whole README**

Check every claim still holds, not only the parts you changed — this sub-project added a network capability to an app whose README opens with "No accounts, no cloud", so any sentence making an absolute claim about network access needs re-reading. Fix any pre-existing inaccuracy you find and report it.

- [ ] **Step 5: Verify and commit**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: unchanged, green — a docs change must not move the count.

```bash
git add README.md
git commit -S -m "Document the opt-in update check and data management

States the exact request, that it is off by default, and repoints the
badges from the upstream fork source to this repository."
```

---

## Self-Review

**Spec coverage.**

| Spec section | Task(s) |
|---|---|
| §2 divergence: off by default | 1, 4, 12 |
| §2 divergence: once per launch, no interval | 4, 10 |
| §2 divergence: link out, never install | 6, 10, 11 |
| §2 divergence: this fork, not upstream | 5, 6, 12 |
| §3.1 settings keys | 1 |
| §3.2 behaviour, incl. button-while-off and silent failure | 4, 10 |
| §3.3 what is sent | 6, 12 |
| §3.4 version comparison, csproj metadata | 2, 5 |
| §4.1 purge, table split, ordering, VACUUM, copy | 7, 9, 11 |
| §4.2 storage usage incl. WAL, rows, path not editable, no poll | 8, 10, 11 |
| §5 About, AGPL-3.0, links, badges | 5, 11, 12 |
| §6 error handling table | 2, 3, 4, 7, 8 |
| §7 testing, incl. the default-off proof | 1–9 |
| §8 done criteria | 11, 12 |

**Placeholder scan.** No "TBD", no "add error handling", no "similar to Task N". Every code step carries the code. Tasks 6, 9 and 11 have deliberate, named test gaps with the reason stated, which is not the same as an unwritten test.

**Type consistency.** `UpdateChecker.CheckAsync(DaylaneSettings, bool, CancellationToken)` returns `UpdateCheckResult` in Tasks 4 and 10. `ReleaseInfo(TagName, HtmlUrl)` is identical in Tasks 3, 4 and 10. `StorageReport(DatabaseBytes, RecordedRows, DatabasePath)` matches between Tasks 8, 9 and 10. `PurgeRecordedActivity()` returns `int` on both the store (7) and the service (9). `AppInfo.Version` is consumed by Tasks 5, 6 and 10 with the same type.

**Test counts** rise 229 → 233 → 250 → 261 → 271 → 271 → 277 → 283 → 288 → 290 → 291, then hold through Tasks 11 and 12, which add none by design. **These are estimates from the test bodies above, not promises.** If a task's run reports fewer than the previous task's total, a test was dropped rather than added — find it before moving on. Report the real number every time and never adjust code or tests to hit a predicted count.
