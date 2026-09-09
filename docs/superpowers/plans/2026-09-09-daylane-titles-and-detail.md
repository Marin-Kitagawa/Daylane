# Daylane Titles & Detail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Daylane answer "what was I actually doing inside that app?" — capture window titles and browser hosts (opt-in), exclude chosen windows from counted time, and surface it all through app detail and search.

**Architecture:** Titles ride on the existing foreground poll, read fresh each sample because the tracker caches process resolution by PID. Browser host extraction is expensive (a UI Automation tree walk), so it runs only at segment-open for a known browser, never on the poll path. Two orthogonal rule systems gate the result: privacy keywords suppress *detail capture*, ignore rules suppress *counted time*.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), Avalonia 12.1.0, Microsoft.Data.Sqlite 10.0.10, xUnit, Win32 P/Invoke (`user32`), UI Automation via COM interop (`CUIAutomation`).

**Spec:** [docs/superpowers/specs/2026-09-09-daylane-titles-and-detail-design.md](../specs/2026-09-09-daylane-titles-and-detail-design.md)

## Global Constraints

- **No AI features, ever.** Permanent product constraint, not a deferral.
- **Windows only.** `net10.0-windows`; no macOS code paths.
- **Every new capture capability ships disabled by default.** `RecordWindowTitles` and `RecordBrowserHost` both default to `false`; an upgraded install that changes nothing records exactly what it records today.
- **Only the host is ever persisted from a URL** — never path, query or fragment.
- Schema scripts are **append-only**. V1 and V2 are shipped and must not be edited; this adds V3.
- Column naming is **PascalCase**.
- Timestamps are written through `Services/Timestamps.cs`'s `ToUtcText`, which is `DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O")`.
- Tombstone/LWW default is the literal `'1970-01-01T00:00:00Z'`.
- Commits are GPG-signed (`git commit -S`) with **no** `Co-Authored-By` trailer. Plain imperative subjects.
- The suite stands at **124 tests, all green**. Full green is the bar after every task.
- **Do not launch the app** unless the controller says the user's Daylane is closed — `Program.cs`'s mutex name is a hardcoded constant, so a second instance pops the user's live window to the foreground.

---

### Task 1: Settings and the ignore-rule model

**Files:**
- Create: `Models/IgnoreRule.cs`
- Modify: `Models/DaylaneSettings.cs`
- Test: `Daylane.Tests/IgnoreRuleSettingsTests.cs`

**Interfaces:**
- Consumes: `DaylaneSettings` (positional record, source-gen JSON), `SettingsService.Update`.
- Produces:
  - `internal sealed record IgnoreRule(string ProcessName, string? TitleKeyword)`
  - `DaylaneSettings.RecordWindowTitles` (bool, default `false`), `.RecordBrowserHost` (bool, default `false`), `.PrivacyKeywords` (`IReadOnlyList<string>`, default empty), `.IgnoreRules` (`IReadOnlyList<IgnoreRule>`, default empty).

**Critical:** `DaylaneSettings` is a **positional record with defaulted constructor parameters**, not init-only properties. That shape is deliberate — init-only properties silently zero every setting when the source-generated deserializer reads the seeded `{}` row. The file's doc comment explains it. Add new parameters in the same style; do not restructure.

- [ ] **Step 1: Write the failing test**

Create `Daylane.Tests/IgnoreRuleSettingsTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter IgnoreRuleSettingsTests`
Expected: compile error — `IgnoreRule` does not exist and `DaylaneSettings` has no `RecordWindowTitles`.

- [ ] **Step 3: Create the ignore-rule model**

Create `Models/IgnoreRule.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Daylane.Models;

/// <summary>
/// One ignore rule: a process name (exact) plus an optional window-title keyword (substring).
///
/// A rule MUST carry a process name. A title-keyword-only rule would match windows across
/// every application, and the time it swallowed would raise no error and no warning — the
/// totals would simply stop adding up. Silent data loss is this feature's characteristic
/// accident, so the shape forbids it rather than warning about it.
/// </summary>
internal sealed record IgnoreRule(
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("titleKeyword")] string? TitleKeyword = null);
```

- [ ] **Step 4: Add the four settings**

In `Models/DaylaneSettings.cs`, append four parameters to the positional record, matching the existing style (each with a `[property: JsonPropertyName(...)]` and a default):

```csharp
    /// <summary>Master switch for this sub-project. Off by default: an upgraded install that
    /// changes nothing records exactly what it recorded before.</summary>
    [property: JsonPropertyName("recordWindowTitles")]
    bool RecordWindowTitles = false,

    /// <summary>Record the browser site (host only, never the path or query). Meaningless
    /// without RecordWindowTitles, and gated behind it in the UI.</summary>
    [property: JsonPropertyName("recordBrowserHost")]
    bool RecordBrowserHost = false,

    /// <summary>Title/URL substrings that suppress detail capture. A hit means no title and
    /// no host are stored for that segment — the row still exists and still counts.</summary>
    [property: JsonPropertyName("privacyKeywords")]
    IReadOnlyList<string>? PrivacyKeywords = null,

    /// <summary>Windows excluded from counted time. Orthogonal to PrivacyKeywords: a hit here
    /// stores the detail but sets Excluded = 1.</summary>
    [property: JsonPropertyName("ignoreRules")]
    IReadOnlyList<IgnoreRule>? IgnoreRules = null)
```

Collections default to `null` because a positional record parameter's default must be a compile-time constant. Expose them as never-null through `Normalize()`, which already exists — extend it:

```csharp
        PrivacyKeywords = PrivacyKeywords ?? Array.Empty<string>(),
        IgnoreRules = IgnoreRules ?? Array.Empty<IgnoreRule>(),
```

Add `[JsonSerializable(typeof(IgnoreRule))]` and `[JsonSerializable(typeof(IReadOnlyList<IgnoreRule>))]` to `SettingsJsonContext` beside the existing attribute, or source generation will not emit converters for the nested type.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 127 tests.

- [ ] **Step 6: Commit**

```bash
git add Models/IgnoreRule.cs Models/DaylaneSettings.cs Daylane.Tests/IgnoreRuleSettingsTests.cs
git commit -S -m "Add title-capture settings and the ignore-rule model

Both capture switches default off, so an upgraded install records what
it recorded before. A rule cannot be title-keyword-only: that would
swallow time across every app with no error."
```

---

### Task 2: Ignore-rule matching

This is the highest-value logic in the sub-project, because its failure mode is silent — wrongly excluded time produces no error, just totals that stop adding up.

**Files:**
- Create: `Services/IgnoreRules.cs`
- Test: `Daylane.Tests/IgnoreRulesTests.cs`

**Interfaces:**
- Consumes: `IgnoreRule` (Task 1).
- Produces: `internal static bool IgnoreRules.IsExcluded(string processName, string? title, IReadOnlyList<IgnoreRule> rules)`

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/IgnoreRulesTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class IgnoreRulesTests
{
    private static IgnoreRule[] Rules(params IgnoreRule[] rules) => rules;

    [Fact]
    public void EmptyRuleList_ExcludesNothing()
        => Assert.False(IgnoreRules.IsExcluded("chrome", "anything", Array.Empty<IgnoreRule>()));

    [Fact]
    public void NullKeyword_ExcludesTheWholeProcess()
        => Assert.True(IgnoreRules.IsExcluded("Solitaire", "any title", Rules(new IgnoreRule("Solitaire", null))));

    [Fact]
    public void KeywordMatch_IsCaseInsensitiveSubstring()
        => Assert.True(IgnoreRules.IsExcluded("WindowsTerminal", "npm DOWNLOAD running",
            Rules(new IgnoreRule("windowsterminal", "download"))));

    [Fact]
    public void KeywordMiss_DoesNotExclude()
        => Assert.False(IgnoreRules.IsExcluded("WindowsTerminal", "editing a file",
            Rules(new IgnoreRule("WindowsTerminal", "download"))));

    [Fact]
    public void ProcessNameMustMatch_KeywordAloneIsNotEnough()
        => Assert.False(IgnoreRules.IsExcluded("chrome", "download page",
            Rules(new IgnoreRule("WindowsTerminal", "download"))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceKeyword_MatchesNothing(string keyword)
    {
        // Contains("") is always true. Treating an empty keyword as "match everything" would
        // let one slip in the UI silently exclude an entire application.
        Assert.False(IgnoreRules.IsExcluded("chrome", "any title at all",
            Rules(new IgnoreRule("chrome", keyword))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RuleWithEmptyProcessName_IsInert(string processName)
        => Assert.False(IgnoreRules.IsExcluded("chrome", "any title",
            Rules(new IgnoreRule(processName, null))));

    [Fact]
    public void NullTitle_StillMatchesAWholeProcessRule()
        => Assert.True(IgnoreRules.IsExcluded("Solitaire", null, Rules(new IgnoreRule("Solitaire", null))));

    [Fact]
    public void NullTitle_NeverMatchesAKeywordRule()
        => Assert.False(IgnoreRules.IsExcluded("chrome", null, Rules(new IgnoreRule("chrome", "mail"))));

    [Fact]
    public void AnyMatchingRule_Excludes()
        => Assert.True(IgnoreRules.IsExcluded("chrome", "webmail",
            Rules(new IgnoreRule("firefox", null), new IgnoreRule("chrome", "mail"))));

    [Fact]
    public void ProcessNameComparison_IgnoresSurroundingWhitespace()
        => Assert.True(IgnoreRules.IsExcluded("chrome", "x", Rules(new IgnoreRule("  chrome  ", null))));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter IgnoreRulesTests`
Expected: compile error — `IgnoreRules` does not exist.

- [ ] **Step 3: Implement matching**

Create `Services/IgnoreRules.cs`:

```csharp
using Daylane.Models;

namespace Daylane.Services;

/// <summary>
/// Decides whether an activity row counts toward totals.
///
/// Orthogonal to <see cref="PrivacyKeywords"/> and easy to confuse with it:
/// privacy controls whether DETAIL IS CAPTURED (a hit stores no title and no host, but the
/// row still counts); this controls whether TIME IS COUNTED (a hit stores the detail but
/// marks the row Excluded, and every report skips it).
///
/// This is the fine-grained sibling of a future "hidden" category: hidden will exclude a
/// whole app, a rule here can exclude one kind of window within an app — a terminal left
/// running a download overnight, where the terminal itself should still count.
/// </summary>
internal static class IgnoreRules
{
    internal static bool IsExcluded(string processName, string? title, IReadOnlyList<IgnoreRule> rules)
    {
        if (rules.Count == 0)
        {
            return false;
        }

        string app = processName.Trim();
        if (app.Length == 0)
        {
            return false;
        }

        foreach (IgnoreRule rule in rules)
        {
            string want = rule.ProcessName.Trim();

            // A rule with no process name would match every window. Treat it as inert
            // rather than letting it swallow everything.
            if (want.Length == 0 || !string.Equals(want, app, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.TitleKeyword is null)
            {
                return true;
            }

            string keyword = rule.TitleKeyword.Trim();

            // An all-whitespace keyword matches nothing: Contains("") is always true, and
            // treating it as "match everything" would let one UI slip exclude a whole app.
            if (keyword.Length == 0 || title is null)
            {
                continue;
            }

            if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 140 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/IgnoreRules.cs Daylane.Tests/IgnoreRulesTests.cs
git commit -S -m "Add ignore-rule matching

An empty or whitespace title keyword matches nothing, and a rule with no
process name is inert. Both guard the same accident: silently excluding
an entire application because Contains(\"\") is always true."
```

---

### Task 3: Privacy keyword suppression

**Files:**
- Create: `Services/PrivacyKeywords.cs`
- Test: `Daylane.Tests/PrivacyKeywordsTests.cs`

**Interfaces:**
- Produces: `internal static bool PrivacyKeywords.Suppresses(string? title, string? host, IReadOnlyList<string> keywords)`

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/PrivacyKeywordsTests.cs`:

```csharp
using Daylane.Services;

namespace Daylane.Tests;

public class PrivacyKeywordsTests
{
    [Fact]
    public void NoKeywords_SuppressesNothing()
        => Assert.False(PrivacyKeywords.Suppresses("anything", "example.com", Array.Empty<string>()));

    [Fact]
    public void TitleMatch_Suppresses()
        => Assert.True(PrivacyKeywords.Suppresses("My Bank - Login", null, new[] { "bank" }));

    [Fact]
    public void HostMatch_Suppresses()
        => Assert.True(PrivacyKeywords.Suppresses(null, "secure.mybank.com", new[] { "mybank" }));

    [Fact]
    public void Match_IsCaseInsensitive()
        => Assert.True(PrivacyKeywords.Suppresses("PASSWORD manager", null, new[] { "password" }));

    [Fact]
    public void NoMatch_DoesNotSuppress()
        => Assert.False(PrivacyKeywords.Suppresses("Daylane", "github.com", new[] { "bank" }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceKeyword_IsIgnored(string keyword)
    {
        // Same hazard as ignore rules: Contains("") is always true, so an empty keyword
        // would suppress capture for every single segment.
        Assert.False(PrivacyKeywords.Suppresses("any title", "any.host", new[] { keyword }));
    }

    [Fact]
    public void BothNull_DoesNotSuppress()
        => Assert.False(PrivacyKeywords.Suppresses(null, null, new[] { "bank" }));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter PrivacyKeywordsTests`
Expected: compile error — `PrivacyKeywords` does not exist.

- [ ] **Step 3: Implement suppression**

Create `Services/PrivacyKeywords.cs`:

```csharp
namespace Daylane.Services;

/// <summary>
/// Decides whether a segment's detail is captured at all.
///
/// Orthogonal to <see cref="IgnoreRules"/>: a hit here means no title and no host are
/// written, but the row still exists and still counts toward totals. A hit there means the
/// detail is written but the time is not counted.
///
/// Suppression is not reversible — the detail was never persisted. That is the intent: a
/// user who names a keyword is asking for nothing to be recorded, not for it to be recorded
/// and hidden.
/// </summary>
internal static class PrivacyKeywords
{
    internal static bool Suppresses(string? title, string? host, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0 || (title is null && host is null))
        {
            return false;
        }

        foreach (string raw in keywords)
        {
            string keyword = raw.Trim();

            // An empty keyword would match everything, suppressing capture entirely.
            if (keyword.Length == 0)
            {
                continue;
            }

            if (title is not null && title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (host is not null && host.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 148 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/PrivacyKeywords.cs Daylane.Tests/PrivacyKeywordsTests.cs
git commit -S -m "Add privacy keyword suppression

A hit stores neither title nor host while the row still counts, which is
the opposite of an ignore rule and the pair most likely to be confused."
```

---

### Task 4: Schema v3 — title search index

**Files:**
- Modify: `Services/Migrations.cs`
- Test: `Daylane.Tests/SchemaV3Tests.cs`

**Interfaces:**
- Consumes: `Migrations.Scripts`, `Migrations.Apply`, `Migrations.ReadUserVersion`.
- Produces: `Migrations.CurrentVersion` becomes 3; index `IX_ActivitySegment_LocalDate_Title`.

**V1 and V2 are shipped — do not edit them.** Append `V3` to the `Scripts` array.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/SchemaV3Tests.cs`:

```csharp
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class SchemaV3Tests
{
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void V3_CreatesTitleSearchIndex()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(1L, Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_ActivitySegment_LocalDate_Title';"));
    }

    [Fact]
    public void V3_IsReachedFromAV2Database()
    {
        using var temp = new TempDatabase();
        using var connection = temp.Open();
        Migrations.Apply(connection, new[] { Migrations.Scripts[0], Migrations.Scripts[1] }, temp.DatabasePath);
        Assert.Equal(2, Migrations.ReadUserVersion(connection));

        Migrations.Apply(connection, temp.DatabasePath);

        Assert.Equal(Migrations.CurrentVersion, Migrations.ReadUserVersion(connection));
        Assert.Equal(3, Migrations.CurrentVersion);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter SchemaV3Tests`
Expected: FAIL — the index does not exist and `CurrentVersion` is 2.

- [ ] **Step 3: Add the V3 script**

In `Services/Migrations.cs`:

```csharp
    internal static readonly string[] Scripts = [V1, V2, V3];
```

```csharp
    // v3: title search. WindowTitle is written from sub-project 2 onward; this index serves
    // the search query, which always filters by LocalDate first.
    private const string V3 = """
        CREATE INDEX IF NOT EXISTS IX_ActivitySegment_LocalDate_Title
            ON ActivitySegment (LocalDate, WindowTitle);
        """;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 150 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/Migrations.cs Daylane.Tests/SchemaV3Tests.cs
git commit -S -m "Add schema v3 with the title search index"
```

---

### Task 5: Capture the window title

**Files:**
- Modify: `Models/ForegroundApp.cs`
- Modify: `Services/ForegroundTracker.cs`
- Test: `Daylane.Tests/ForegroundAppTests.cs`

**Interfaces:**
- Produces: `ForegroundApp` gains `WindowTitle` (`string?`) and `UrlHost` (`string?`) as record-struct members with default `null`.

**Two things that will bite if missed:**

1. **`SameIdentity` must not change.** It decides when a new segment starts. If it compared titles, every browser tab switch and document rename would close one segment and open another — multiplying rows for no analytical gain. Titles are attributes of a segment, not boundaries.
2. **`ResolveForegroundApp` caches by PID** (`_cachedPid` / `_cachedByPid`), returning the cached `ForegroundApp` when the same process is still foreground. The cache exists because *process* resolution is expensive. The **title is not** — so read it fresh on every sample and apply it to the cached value with a `with` expression. Caching the title would serve a stale one across every tab switch.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/ForegroundAppTests.cs`:

```csharp
using Daylane.Models;

namespace Daylane.Tests;

public class ForegroundAppTests
{
    private static ForegroundApp App(string process, string? title = null) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title };

    [Fact]
    public void SameIdentity_IgnoresTheWindowTitle()
    {
        // A title change must NOT open a new segment: browser tab switches would otherwise
        // multiply rows for no analytical gain.
        Assert.True(App("chrome", "Inbox").SameIdentity(App("chrome", "Calendar")));
    }

    [Fact]
    public void SameIdentity_StillDistinguishesProcesses()
        => Assert.False(App("chrome", "x").SameIdentity(App("firefox", "x")));

    [Fact]
    public void WindowTitle_DefaultsToNull()
        => Assert.Null(new ForegroundApp("chrome", @"C:\chrome.exe", "chrome", false).WindowTitle);

    [Fact]
    public void UrlHost_DefaultsToNull()
        => Assert.Null(new ForegroundApp("chrome", @"C:\chrome.exe", "chrome", false).UrlHost);

    [Fact]
    public void WithExpression_ReplacesTitleWithoutChangingIdentity()
    {
        ForegroundApp original = App("chrome", "Inbox");
        ForegroundApp retitled = original with { WindowTitle = "Calendar" };

        Assert.Equal("Calendar", retitled.WindowTitle);
        Assert.True(original.SameIdentity(retitled));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter ForegroundAppTests`
Expected: compile error — `ForegroundApp` has no `WindowTitle`.

- [ ] **Step 3: Add the members**

In `Models/ForegroundApp.cs`, add two init-only members to the record struct, leaving the positional parameters and `SameIdentity` untouched:

```csharp
    /// <summary>Foreground window title, when title capture is on. Null otherwise.
    /// Deliberately absent from SameIdentity: a retitle must not start a new segment.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>Browser site host, never a full URL. Filled at segment-open only.</summary>
    public string? UrlHost { get; init; }
```

- [ ] **Step 4: Read the title in the tracker**

In `Services/ForegroundTracker.cs`, add the P/Invoke beside the existing `user32` imports:

```csharp
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
```

Add a title reader:

```csharp
    private static string? ReadWindowTitle(IntPtr hwnd)
    {
        try
        {
            var buffer = new char[512];
            int length = GetWindowTextW(hwnd, buffer, buffer.Length);
            if (length <= 0)
            {
                return null;
            }

            string title = new string(buffer, 0, length).Trim();
            return title.Length == 0 ? null : title;
        }
        catch (Exception)
        {
            // A title is never worth failing a poll over.
            return null;
        }
    }
```

Then in `ResolveForegroundApp`, apply the freshly-read title to both the cached and the newly-resolved result. The cache keeps holding only the expensive process resolution:

```csharp
        string? title = ReadWindowTitle(hwnd);

        if (processId == _cachedPid && _cachedByPid is { } cached)
        {
            return cached with { WindowTitle = title };
        }

        ForegroundApp resolved = ResolveProcess(processId);
        _cachedPid = processId;
        _cachedByPid = resolved;
        return resolved with { WindowTitle = title };
```

Note the cache stores the *untitled* resolution, so a stale title can never be served.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 155 tests.

- [ ] **Step 6: Commit**

```bash
git add Models/ForegroundApp.cs Services/ForegroundTracker.cs Daylane.Tests/ForegroundAppTests.cs
git commit -S -m "Read the foreground window title on each sample

Titles are attributes of a segment, not boundaries, so SameIdentity is
unchanged. The PID cache keeps holding only the process resolution: a
cached title would go stale on every tab switch."
```

---

### Task 6: Persist title and Excluded on segment open

**Files:**
- Modify: `Services/DailyStatsStore.cs`
- Modify: `Services/TrackingService.cs`
- Test: `Daylane.Tests/TitlePersistenceTests.cs`

**Interfaces:**
- Consumes: `IgnoreRules.IsExcluded`, `PrivacyKeywords.Suppresses`, `ForegroundApp.WindowTitle`, settings from Task 1.
- Produces: `OpenSegment` writes `WindowTitle`, `UrlHost` and `Excluded`; `TrackingService` decides what the segment is allowed to carry.

**The decision belongs in `TrackingService`, not the store.** The store persists what it is given; the service applies policy. That keeps the two rule systems in one readable place.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/TitlePersistenceTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;
using Microsoft.Data.Sqlite;

namespace Daylane.Tests;

public class TitlePersistenceTests
{
    private static ForegroundApp App(string process, string? title) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title };

    private static (string? Title, long Excluded) ReadRow(TempDatabase temp)
    {
        using var connection = temp.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT WindowTitle, Excluded FROM ActivitySegment ORDER BY Id DESC LIMIT 1;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1));
    }

    [Fact]
    public void OpenSegment_PersistsTheTitle()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        store.OpenSegment(App("chrome", "Inbox - Mail"), DateTime.UtcNow);

        Assert.Equal("Inbox - Mail", ReadRow(temp).Title);
    }

    [Fact]
    public void OpenSegment_WithNoTitle_PersistsNull()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        store.OpenSegment(App("chrome", null), DateTime.UtcNow);

        Assert.Null(ReadRow(temp).Title);
    }

    [Fact]
    public void OpenSegment_MarksExcludedWhenAsked()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        store.OpenSegment(App("Solitaire", "Klondike"), DateTime.UtcNow, excluded: true);

        Assert.Equal(1L, ReadRow(temp).Excluded);
    }

    [Fact]
    public void OpenSegment_DefaultsToNotExcluded()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);

        store.OpenSegment(App("chrome", "x"), DateTime.UtcNow);

        Assert.Equal(0L, ReadRow(temp).Excluded);
    }

    [Fact]
    public void ExclusionAndStorage_AgreeOnTheSameTitle()
    {
        // The live decision and Task 9's recompute must reach the same verdict, and the
        // recompute can only ever see the stored title. So a stripped title means a
        // title-keyword rule does not match — live or on recompute.
        var rules = new[] { new IgnoreRule("chrome", "mail") };
        var captured = App("chrome", "Inbox - mail");

        // Titles off: the stored title is null, so the keyword rule cannot match.
        ForegroundApp stored = captured with { WindowTitle = null, UrlHost = null };
        Assert.False(IgnoreRules.IsExcluded(stored.ProcessName, stored.WindowTitle, rules));

        // Titles on: the stored title is the real one, so it matches.
        Assert.True(IgnoreRules.IsExcluded(captured.ProcessName, captured.WindowTitle, rules));

        // A whole-process rule works either way.
        var wholeProcess = new[] { new IgnoreRule("chrome", null) };
        Assert.True(IgnoreRules.IsExcluded(stored.ProcessName, stored.WindowTitle, wholeProcess));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter TitlePersistenceTests`
Expected: FAIL — `WindowTitle` is null on every row, and `OpenSegment` has no `excluded` parameter.

- [ ] **Step 3: Persist the new columns**

In `Services/DailyStatsStore.cs`, change `OpenSegment`'s signature and insert:

```csharp
    public long OpenSegment(ForegroundApp app, DateTime startUtc, bool excluded = false)
```

Add `WindowTitle, UrlHost, Excluded` to the insert's column list and `$title, $host, $excluded` to its values, binding:

```csharp
            command.Parameters.AddWithValue("$title", (object?)app.WindowTitle ?? DBNull.Value);
            command.Parameters.AddWithValue("$host", (object?)app.UrlHost ?? DBNull.Value);
            command.Parameters.AddWithValue("$excluded", excluded ? 1 : 0);
```

Leave `DeviceId`, `UpdatedAt`, `LocalDate` and `LocalHour` exactly as they are — they are already correct.

- [ ] **Step 4: Apply policy in the tracking service**

In `Services/TrackingService.cs`, where the segment is opened from `OnForegroundChanged`, decide what the segment may carry:

```csharp
    private ForegroundApp ApplyCapturePolicy(ForegroundApp app, out bool excluded)
    {
        DaylaneSettings settings = Settings.Current;

        ForegroundApp stored = app;

        if (!settings.RecordWindowTitles)
        {
            stored = app with { WindowTitle = null, UrlHost = null };
        }
        else if (PrivacyKeywords.Suppresses(app.WindowTitle, app.UrlHost, settings.PrivacyKeywords))
        {
            stored = app with { WindowTitle = null, UrlHost = null };
        }

        // Evaluated against the title that will be STORED, not the one just read from the
        // window. Task 9's recompute can only ever see the stored title, so judging live
        // capture by a richer title would make the two disagree: a row excluded now by a
        // title keyword would silently un-exclude itself on the next unrelated rule edit.
        // Consequence, and it is the intended one: title-keyword rules require title capture
        // to be on. Whole-process rules (TitleKeyword = null) work regardless.
        excluded = IgnoreRules.IsExcluded(stored.ProcessName, stored.WindowTitle, settings.IgnoreRules);

        return stored;
    }
```

**This ordering is load-bearing and was corrected during the pre-flight scan.** The obvious arrangement — evaluate exclusion first, against the real title, so that privacy suppression cannot "defeat" an ignore rule — produces a live result the recompute in Task 9 can never reproduce, because the recompute reads the stored title and the stored title is `NULL`. The row would flip from excluded to counted the next time any rule changed, silently. Consistency between live capture and recompute wins; a user who wants a window excluded regardless of title capture uses a whole-process rule.

Call it at segment open and pass the result through.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 160 tests.

- [ ] **Step 6: Commit**

```bash
git add Services/DailyStatsStore.cs Services/TrackingService.cs Daylane.Tests/TitlePersistenceTests.cs
git commit -S -m "Persist window titles and the excluded flag

Policy lives in TrackingService and persistence in the store. Exclusion
is evaluated before privacy suppression strips the title, so suppressing
a window cannot silently start counting time the user excluded."
```

---

### Task 7: Default-off proof

A task of its own, because this is the test that proves the privacy promise to anyone who upgrades and changes nothing. It is small, and it is the one a reviewer should be able to point at.

**Files:**
- Test: `Daylane.Tests/CaptureDefaultsTests.cs`

**Interfaces:** consumes everything from Tasks 1–6.

- [ ] **Step 1: Write the test**

Create `Daylane.Tests/CaptureDefaultsTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class CaptureDefaultsTests
{
    [Fact]
    public void WithDefaultSettings_TitleCaptureIsOff()
    {
        using var temp = new TempDatabase();
        using (var connection = temp.Open())
        {
            Migrations.Apply(connection, temp.DatabasePath);
        }

        var settings = new SettingsService(temp.ConnectionString).Current;

        // The promise: an upgraded install that changes nothing records what it recorded before.
        Assert.False(settings.RecordWindowTitles);
        Assert.False(settings.RecordBrowserHost);
    }

    [Fact]
    public void CapturePolicy_WithTitlesOff_StripsTitleAndHost()
    {
        var app = new ForegroundApp("chrome", @"C:\chrome.exe", "chrome", false)
        {
            WindowTitle = "Inbox",
            UrlHost = "mail.example.com"
        };

        var settings = new DaylaneSettings().Normalize();
        Assert.False(settings.RecordWindowTitles);

        // Mirrors TrackingService.ApplyCapturePolicy's first branch.
        ForegroundApp stored = settings.RecordWindowTitles ? app : app with { WindowTitle = null, UrlHost = null };

        Assert.Null(stored.WindowTitle);
        Assert.Null(stored.UrlHost);
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter CaptureDefaultsTests`
Expected: PASS, 2 tests (this asserts behaviour Tasks 1–6 already deliver).

- [ ] **Step 3: Commit**

```bash
git add Daylane.Tests/CaptureDefaultsTests.cs
git commit -S -m "Pin the default-off capture promise with a test"
```

---

### Task 8: Browser host extraction

The riskiest task. Read the whole thing before starting.

**Files:**
- Create: `Services/BrowserHost.cs`
- Modify: `Services/TrackingService.cs`
- Test: `Daylane.Tests/BrowserHostTests.cs`

**Interfaces:**
- Produces:
  - `internal static bool BrowserHost.IsBrowser(string processName)`
  - `internal static string? BrowserHost.HostFromUrl(string? candidate)`
  - `internal static string? BrowserHost.TryGetForegroundHost()` — the UI Automation walk

**Design, ported from Hindsight's `capture/browser_url/windows.rs`, whose comments record what already went wrong there:**

- Walk the foreground window with UI Automation, scanning `Edit` and `Document` controls; take the first value that parses as a URL. Try the value pattern, then legacy IAccessible value, then the element name.
- **The tree walk must specify an explicit depth.** Hindsight's matcher defaulted to direct children only and never found anything, because address bars sit deep in the subtree.
- **Do not score candidates or try to identify "the real address bar".** Hindsight rejected that deliberately: a threshold breaks when Chrome renames its omnibox class, or when `chrome://` URLs carry no scheme. An occasional false positive costs one wrong host on one segment.
- **COM failures must not reach the process.** Wrap the entire extraction in `try`/`catch` returning null.
- **Cost is the real risk.** A UIA walk takes tens of milliseconds and the tracker polls every second, so this runs **only at segment open, only for a known browser, only when the setting is on** — never on the poll path.
- **Mechanism:** use COM interop against `CUIAutomation`. Do **not** take a WPF dependency to get `System.Windows.Automation`. If COM interop proves impractical here, stop and report it rather than pulling WPF into an Avalonia app.

- [ ] **Step 1: Write the failing tests**

`IsBrowser` and `HostFromUrl` are pure and fully testable. The UIA walk needs a real browser window and is **inspection-only** — say so in your report rather than mocking a fake automation tree.

Create `Daylane.Tests/BrowserHostTests.cs`:

```csharp
using Daylane.Services;

namespace Daylane.Tests;

public class BrowserHostTests
{
    [Theory]
    [InlineData("chrome")]
    [InlineData("Chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("brave")]
    public void IsBrowser_RecognisesKnownBrowsers(string process)
        => Assert.True(BrowserHost.IsBrowser(process));

    [Theory]
    [InlineData("devenv")]
    [InlineData("WindowsTerminal")]
    [InlineData("")]
    public void IsBrowser_RejectsEverythingElse(string process)
        => Assert.False(BrowserHost.IsBrowser(process));

    [Theory]
    [InlineData("https://mail.example.com/u/0/inbox?q=secret", "mail.example.com")]
    [InlineData("http://example.com", "example.com")]
    [InlineData("https://example.com:8443/path", "example.com")]
    [InlineData("https://sub.domain.example.co.uk/x", "sub.domain.example.co.uk")]
    public void HostFromUrl_KeepsOnlyTheHost(string url, string expected)
        => Assert.Equal(expected, BrowserHost.HostFromUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("just some window title")]
    public void HostFromUrl_ReturnsNullForJunk(string? candidate)
        => Assert.Null(BrowserHost.HostFromUrl(candidate));

    [Fact]
    public void HostFromUrl_NeverLeaksThePath()
    {
        string? host = BrowserHost.HostFromUrl("https://example.com/very/secret/path?token=abc");

        Assert.Equal("example.com", host);
        Assert.DoesNotContain("secret", host);
        Assert.DoesNotContain("token", host);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter BrowserHostTests`
Expected: compile error — `BrowserHost` does not exist.

- [ ] **Step 3: Implement the pure parts**

Create `Services/BrowserHost.cs` with the browser list and URL parsing first:

```csharp
namespace Daylane.Services;

internal static class BrowserHost
{
    /// A fixed list rather than a heuristic: guessing wrong means running an expensive UI
    /// Automation walk against every non-browser window. Adding a browser is a one-line change.
    private static readonly string[] KnownBrowsers =
        ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen"];

    internal static bool IsBrowser(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return KnownBrowsers.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Host only — never the path, query or fragment. The rest is not truncated at
    /// display time, it is never persisted.</summary>
    internal static string? HostFromUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 180 tests.

- [ ] **Step 5: Add the UI Automation walk**

Add `TryGetForegroundHost()` to the same file, using COM interop against `CUIAutomation`. It must:

1. `GetForegroundWindow()`; return null on zero.
2. Create the automation object and get the element from the handle.
3. Build a condition matching `ControlType.Edit` **or** `ControlType.Document`, and search **descendants** — not children.
4. For each match, try the value pattern, then legacy IAccessible value, then the name; return the first that `HostFromUrl` accepts.
5. Wrap everything in `try`/`catch (Exception)` returning null. A COM failure must never reach the caller.

Write the P/Invoke and interop declarations at the bottom of the class, matching `Services/IdleMonitor.cs`'s convention.

- [ ] **Step 6: Enrich at segment open**

In `Services/TrackingService.cs`, extend `ApplyCapturePolicy` so the host is fetched exactly once per segment, and only when it can be used:

```csharp
        if (settings.RecordBrowserHost && BrowserHost.IsBrowser(app.ProcessName))
        {
            app = app with { UrlHost = BrowserHost.TryGetForegroundHost() };
        }
```

Place it **before** the privacy check, so a suppressed segment discards the host rather than storing it.

- [ ] **Step 7: Verify the build and full suite**

Run: `dotnet build Daylane.csproj` then `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: build clean, suite green.

- [ ] **Step 8: Commit**

```bash
git add Services/BrowserHost.cs Services/TrackingService.cs Daylane.Tests/BrowserHostTests.cs
git commit -S -m "Extract the browser site host at segment open

Host only, never the path. The UI Automation walk runs once per segment
for a known browser rather than on the one-second poll, and swallows COM
failures rather than letting them reach the process."
```

---

### Task 9: Recompute Excluded when rules change

Without this, a mistaken rule is permanent in effect even after deletion — the reversibility the spec promises does not exist.

**Files:**
- Modify: `Services/DailyStatsStore.cs`
- Modify: `Services/TrackingService.cs`
- Test: `Daylane.Tests/ExcludedRecomputeTests.cs`

**Interfaces:**
- Produces: `internal int DailyStatsStore.RecomputeExcluded(IReadOnlyList<IgnoreRule> rules)` — returns rows changed.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/ExcludedRecomputeTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class ExcludedRecomputeTests
{
    private static ForegroundApp App(string process, string? title) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title };

    private static long ExcludedCount(TempDatabase temp)
    {
        using var connection = temp.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActivitySegment WHERE Excluded = 1;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void AddingARule_ExcludesMatchingHistory()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        store.OpenSegment(App("WindowsTerminal", "npm download running"), DateTime.UtcNow);
        store.OpenSegment(App("WindowsTerminal", "editing a file"), DateTime.UtcNow);

        store.RecomputeExcluded(new[] { new IgnoreRule("WindowsTerminal", "download") });

        Assert.Equal(1L, ExcludedCount(temp));
    }

    [Fact]
    public void RemovingAllRules_RestoresPreviouslyExcludedTime()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        store.OpenSegment(App("Solitaire", "Klondike"), DateTime.UtcNow, excluded: true);
        Assert.Equal(1L, ExcludedCount(temp));

        store.RecomputeExcluded(Array.Empty<IgnoreRule>());

        // This is the reversibility promise: a mistaken rule must not be permanent.
        Assert.Equal(0L, ExcludedCount(temp));
    }

    [Fact]
    public void RowsWithNoTitle_OnlyMatchWholeProcessRules()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        store.OpenSegment(App("chrome", null), DateTime.UtcNow);

        store.RecomputeExcluded(new[] { new IgnoreRule("chrome", "mail") });
        Assert.Equal(0L, ExcludedCount(temp));

        store.RecomputeExcluded(new[] { new IgnoreRule("chrome", null) });
        Assert.Equal(1L, ExcludedCount(temp));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter ExcludedRecomputeTests`
Expected: compile error — `RecomputeExcluded` does not exist.

- [ ] **Step 3: Implement the recompute**

In `Services/DailyStatsStore.cs`:

```csharp
    /// <summary>
    /// Re-evaluates every activity row against the current rules. Rule edits are rare, so a
    /// full-table pass is the right trade against carrying rule evaluation into every read.
    /// One transaction: a failure must not leave half the table judged by the old rules.
    /// </summary>
    internal int RecomputeExcluded(IReadOnlyList<IgnoreRule> rules)
    {
        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var rows = new List<(long Id, string ProcessName, string? Title, long Excluded)>();
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT Id, ProcessName, WindowTitle, Excluded FROM ActivitySegment;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3)));
                }
            }

            int changed = 0;
            foreach (var row in rows)
            {
                long want = IgnoreRules.IsExcluded(row.ProcessName, row.Title, rules) ? 1 : 0;
                if (want == row.Excluded)
                {
                    continue;
                }

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE ActivitySegment SET Excluded = $excluded, UpdatedAt = $now WHERE Id = $id;";
                update.Parameters.AddWithValue("$excluded", want);
                update.Parameters.AddWithValue("$now", Timestamps.ToUtcText(DateTime.UtcNow));
                update.Parameters.AddWithValue("$id", row.Id);
                update.ExecuteNonQuery();
                changed++;
            }

            transaction.Commit();
            return changed;
        }
    }
```

Note it stamps `UpdatedAt`, matching every other write path.

- [ ] **Step 4: Trigger it on rule change**

In `Services/TrackingService.cs`'s settings-changed handler, recompute when the rule list actually differs from the one last applied — not on every settings write, or typing in an unrelated numeric field would trigger a full-table pass. Keep the last-applied list in a field and compare with `SequenceEqual` (records give you structural equality for free).

Wrap the call in `try`/`catch`, logging and continuing: a failed recompute must not take down a settings save.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 183 tests.

- [ ] **Step 6: Commit**

```bash
git add Services/DailyStatsStore.cs Services/TrackingService.cs Daylane.Tests/ExcludedRecomputeTests.cs
git commit -S -m "Recompute Excluded when ignore rules change

Deleting a rule restores the time it excluded. Without this the
reversibility the feature promises would not exist, and a mistaken rule
would be permanent in effect."
```

---

### Task 10: App-detail queries

**Files:**
- Create: `Models/TitleUsageSummary.cs`
- Modify: `Services/DailyStatsStore.cs`
- Test: `Daylane.Tests/AppDetailQueryTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class TitleUsageSummary` with `Title` (`string?`), `UrlHost` (`string?`), `Duration` (`TimeSpan`), `SessionCount` (`int`), `IsExcluded` (`bool`)
  - `internal IReadOnlyList<TitleUsageSummary> DailyStatsStore.GetTitleUsage(string exePath, DateTime rangeStartLocal, DateTime rangeEndLocal)`

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/AppDetailQueryTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class AppDetailQueryTests
{
    private static ForegroundApp App(string process, string? title, string? host = null) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title, UrlHost = host };

    [Fact]
    public void GetTitleUsage_GroupsByTitleAndSumsDuration()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-10);

        long a = store.OpenSegment(App("chrome", "Inbox"), start);
        store.CloseSegment(a, start.AddMinutes(2), 0, 0);
        long b = store.OpenSegment(App("chrome", "Inbox"), start.AddMinutes(3));
        store.CloseSegment(b, start.AddMinutes(6), 0, 0);
        long c = store.OpenSegment(App("chrome", "Calendar"), start.AddMinutes(7));
        store.CloseSegment(c, start.AddMinutes(8), 0, 0);

        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe",
            DateTime.Now.Date, DateTime.Now.Date.AddDays(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal("Inbox", rows[0].Title);
        Assert.Equal(2, rows[0].SessionCount);
        Assert.Equal(TimeSpan.FromMinutes(5), rows[0].Duration);
        Assert.Equal("Calendar", rows[1].Title);
    }

    [Fact]
    public void GetTitleUsage_MarksExcludedRowsWithoutHidingThem()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);

        long id = store.OpenSegment(App("chrome", "Ignored"), start, excluded: true);
        store.CloseSegment(id, start.AddMinutes(1), 0, 0);

        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe",
            DateTime.Now.Date, DateTime.Now.Date.AddDays(1));

        // Hiding excluded rows entirely would make the rule that excluded them undiscoverable.
        Assert.Single(rows);
        Assert.True(rows[0].IsExcluded);
    }

    [Fact]
    public void GetTitleUsage_CarriesTheHostForBrowserRows()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);

        long id = store.OpenSegment(App("chrome", "Inbox", "mail.example.com"), start);
        store.CloseSegment(id, start.AddMinutes(1), 0, 0);

        var rows = store.GetTitleUsage(@"C:\Apps\chrome.exe",
            DateTime.Now.Date, DateTime.Now.Date.AddDays(1));

        Assert.Equal("mail.example.com", rows[0].UrlHost);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter AppDetailQueryTests`
Expected: compile error — `GetTitleUsage` does not exist.

- [ ] **Step 3: Implement the model and query**

Create `Models/TitleUsageSummary.cs`:

```csharp
namespace Daylane.Models;

internal sealed class TitleUsageSummary
{
    public string? Title { get; init; }
    public string? UrlHost { get; init; }
    public TimeSpan Duration { get; init; }
    public int SessionCount { get; init; }
    public bool IsExcluded { get; init; }
}
```

Add `GetTitleUsage` to `DailyStatsStore`, following the existing range-query pattern in that file (`GetAppUsage` is the closest sibling — copy its range handling, its `ToUtcText` bounds, and its `EffectiveEnd` treatment of open segments rather than inventing new ones). Group by `WindowTitle` and `UrlHost`, ordered by summed duration descending.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 186 tests.

- [ ] **Step 5: Commit**

```bash
git add Models/TitleUsageSummary.cs Services/DailyStatsStore.cs Daylane.Tests/AppDetailQueryTests.cs
git commit -S -m "Add per-title usage queries for app detail

Excluded rows are returned and marked rather than filtered out: hiding
them would make the rule that excluded them undiscoverable."
```

---

### Task 11: Title search

**Files:**
- Modify: `Services/DailyStatsStore.cs`
- Test: `Daylane.Tests/TitleSearchTests.cs`

**Interfaces:**
- Produces: `internal IReadOnlyList<ActivitySegment> DailyStatsStore.SearchTitles(string query, DateTime rangeStartLocal, DateTime rangeEndLocal, int limit = 200)`

Plain `LIKE` over the `IX_ActivitySegment_LocalDate_Title` index. **No FTS** — FTS5 arrives in sub-project 6 for screen-memory text, and a second search implementation now would mean two to maintain.

- [ ] **Step 1: Write the failing tests**

Create `Daylane.Tests/TitleSearchTests.cs`:

```csharp
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Tests;

public class TitleSearchTests
{
    private static ForegroundApp App(string process, string? title, string? host = null) =>
        new(process, $@"C:\Apps\{process}.exe", process, false) { WindowTitle = title, UrlHost = host };

    private static DailyStatsStore Seed(TempDatabase temp)
    {
        var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-20);
        long a = store.OpenSegment(App("chrome", "Quarterly Budget Review"), start);
        store.CloseSegment(a, start.AddMinutes(5), 0, 0);
        long b = store.OpenSegment(App("chrome", "Inbox", "mail.example.com"), start.AddMinutes(6));
        store.CloseSegment(b, start.AddMinutes(9), 0, 0);
        long c = store.OpenSegment(App("devenv", null), start.AddMinutes(10));
        store.CloseSegment(c, start.AddMinutes(12), 0, 0);
        return store;
    }

    [Fact]
    public void SearchTitles_MatchesOnTitleSubstringCaseInsensitively()
    {
        using var temp = new TempDatabase();
        using var store = Seed(temp);

        var hits = store.SearchTitles("budget", DateTime.Now.Date, DateTime.Now.Date.AddDays(1));

        Assert.Single(hits);
        Assert.Equal("Quarterly Budget Review", hits[0].WindowTitle);
    }

    [Fact]
    public void SearchTitles_MatchesOnHost()
    {
        using var temp = new TempDatabase();
        using var store = Seed(temp);

        var hits = store.SearchTitles("mail.example", DateTime.Now.Date, DateTime.Now.Date.AddDays(1));

        Assert.Single(hits);
        Assert.Equal("Inbox", hits[0].WindowTitle);
    }

    [Fact]
    public void SearchTitles_IgnoresRowsWithNoTitle()
    {
        using var temp = new TempDatabase();
        using var store = Seed(temp);

        var hits = store.SearchTitles("devenv", DateTime.Now.Date, DateTime.Now.Date.AddDays(1));

        Assert.Empty(hits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SearchTitles_WithBlankQuery_ReturnsNothing(string query)
    {
        using var temp = new TempDatabase();
        using var store = Seed(temp);

        // A blank query must not degenerate into "match every row".
        Assert.Empty(store.SearchTitles(query, DateTime.Now.Date, DateTime.Now.Date.AddDays(1)));
    }

    [Fact]
    public void SearchTitles_EscapesWildcards()
    {
        using var temp = new TempDatabase();
        using var store = new DailyStatsStore(temp.DatabasePath);
        DateTime start = DateTime.UtcNow.AddMinutes(-5);
        long id = store.OpenSegment(App("chrome", "plain title"), start);
        store.CloseSegment(id, start.AddMinutes(1), 0, 0);

        // "%" must be a literal, not "match anything".
        Assert.Empty(store.SearchTitles("%", DateTime.Now.Date, DateTime.Now.Date.AddDays(1)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter TitleSearchTests`
Expected: compile error — `SearchTitles` does not exist.

- [ ] **Step 3: Implement search**

Add `SearchTitles` to `DailyStatsStore`. Return empty immediately for a blank query. Escape `%`, `_` and the escape character itself in the user's input and use `LIKE $q ESCAPE '\'`, matching on `WindowTitle` or `UrlHost`, filtered by the same local-date range pattern the other range queries use, ordered by `StartUtc` descending, capped at `limit`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj`
Expected: PASS, 192 tests.

- [ ] **Step 5: Commit**

```bash
git add Services/DailyStatsStore.cs Daylane.Tests/TitleSearchTests.cs
git commit -S -m "Add title and host search

LIKE over the v3 index rather than FTS: sub-project 6 brings FTS5 for
screen-memory text, and two search implementations would be two to
maintain. Wildcards are escaped and a blank query matches nothing."
```

---

### Task 12: Settings UI — the Privacy group

**Files:**
- Modify: `ViewModels/MainWindowViewModel.cs`
- Modify: `MainWindow.axaml`
- Test: `Daylane.Tests/PrivacySettingsViewModelTests.cs`

**Interfaces:**
- Consumes: settings from Task 1, `SettingsChangeNotifier` (foundation).
- Produces: view-model properties `RecordWindowTitles`, `RecordBrowserHost`, `CanRecordBrowserHost`, plus keyword and rule list editing commands.

**Follow the existing Settings pattern exactly** — the tab already has Appearance, Tracking and Startup groups built from `Border Classes="panel"` sections, each row a label + description + control, every colour a `{DynamicResource ...}` token. Add a fourth group titled **Privacy**.

The colour-literal gate must stay clean:

```
grep -rnE "Brushes\.[A-Za-z]+|Color\.(Parse|FromRgb)|Colors\.|#[0-9A-Fa-f]{6}" Controls/ ViewModels/ App.axaml MainWindow.axaml
```

Expect only `App.axaml`'s own token definitions and `TimelinePalette`'s deliberate magenta fallback.

- [ ] **Step 1: Write the failing test**

Create `Daylane.Tests/PrivacySettingsViewModelTests.cs`:

```csharp
using Daylane.Models;

namespace Daylane.Tests;

public class PrivacySettingsViewModelTests
{
    [Fact]
    public void BrowserHost_IsOnlyMeaningfulWhenTitlesAreOn()
    {
        var off = new DaylaneSettings().Normalize();
        var on = (new DaylaneSettings() with { RecordWindowTitles = true }).Normalize();

        // Mirrors the CanRecordBrowserHost gate in the view model: the host is derived during
        // the same sample as the title, so it cannot be captured without it.
        Assert.False(off.RecordWindowTitles);
        Assert.True(on.RecordWindowTitles);
    }

    [Fact]
    public void TurningTitlesOff_AlsoClearsBrowserHost()
    {
        var settings = (new DaylaneSettings() with
        {
            RecordWindowTitles = true,
            RecordBrowserHost = true
        }).Normalize();

        var afterOff = (settings with { RecordWindowTitles = false }).Normalize();

        // Leaving RecordBrowserHost true while titles are off would be a setting that reads
        // as on and does nothing.
        Assert.False(afterOff.RecordBrowserHost);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Daylane.Tests/Daylane.Tests.csproj --filter PrivacySettingsViewModelTests`
Expected: FAIL on the second test — `Normalize()` does not yet clear `RecordBrowserHost`.

- [ ] **Step 3: Make the dependent setting honest**

In `Models/DaylaneSettings.cs`, extend `Normalize()`:

```csharp
        // A host cannot be captured without a title — they come from the same sample. Leaving
        // this true while titles are off would be a switch that reads as on and does nothing.
        RecordBrowserHost = RecordWindowTitles && RecordBrowserHost,
```

- [ ] **Step 4: Add the view-model surface**

In `ViewModels/MainWindowViewModel.cs`, add properties following the existing settings-property pattern exactly (getter reads `_settings.Current.X`, setter calls `_settings.Update`, then `OnPropertyChanged()`):

- `RecordWindowTitles` (bool) — when set false, also raise `OnPropertyChanged(nameof(RecordBrowserHost))` and `nameof(CanRecordBrowserHost)`, since `Normalize` may have cleared the former.
- `RecordBrowserHost` (bool).
- `CanRecordBrowserHost` => `_settings.Current.RecordWindowTitles` — bound to the host toggle's `IsEnabled`.
- `PrivacyKeywords` / `IgnoreRules` as observable collections, with add and remove commands that write the whole list back through `Update`.

Add the new property names to `SettingsChangeNotifier`'s re-raise list, or a change made elsewhere will not refresh these controls.

- [ ] **Step 5: Add the Privacy panel**

In `MainWindow.axaml`, add a fourth `Border Classes="panel"` section headed **Privacy**, after Startup:

- **Record window titles** — `ToggleSwitch` bound to `RecordWindowTitles`. Description: what it stores and that it is off by default.
- **Record browser site** — `ToggleSwitch` bound to `RecordBrowserHost`, `IsEnabled="{Binding CanRecordBrowserHost}"`. Description must say **host only, never the full address**.
- **Private keywords** — a list editor bound to `PrivacyKeywords`. Description: a matching title or site means nothing is recorded for that window, but the time still counts.
- **Ignored windows** — a list editor bound to `IgnoreRules`, each row showing process plus optional keyword. Description: the time stops counting; the entry can be removed to restore it.

Every colour must be a `{DynamicResource ...}` token.

- [ ] **Step 6: Run the gate and the suite**

Run the colour-literal gate above (expect only the two known hits), then `dotnet test Daylane.Tests/Daylane.Tests.csproj` and `dotnet build Daylane.csproj`.
Expected: gate clean, suite green, build clean.

- [ ] **Step 7: Commit**

```bash
git add Models/DaylaneSettings.cs ViewModels/MainWindowViewModel.cs MainWindow.axaml Daylane.Tests/PrivacySettingsViewModelTests.cs
git commit -S -m "Add the Privacy settings group

Normalize clears the browser-host switch when titles are off, so it can
never read as on while doing nothing."
```

---

### Task 13: App-detail and search UI

**Files:**
- Modify: `ViewModels/MainWindowViewModel.cs`
- Modify: `MainWindow.axaml`

No new unit tests — this is presentation over queries already covered by Tasks 10 and 11. Say so in your report rather than adding tests that only restate the view model.

- [ ] **Step 1: Add the detail surface to the view model**

- `SelectedAppTitles` (`ObservableCollection<TitleUsageItemViewModel>`) populated from `GetTitleUsage` when an app row is selected.
- For a browser app, group by `UrlHost` with titles nested; for anything else, a flat title list.
- Each item exposes display title, duration text, session count, and `IsExcluded` for styling.
- An empty state for an app with no captured titles, whose text says titles may be off rather than implying there was no activity — that distinction is the difference between a bug report and a setting.

- [ ] **Step 2: Add the detail panel to the Day view**

Extend the existing selection panel (currently "No selection / Click a timeline block or activity row to inspect it"). When an app is selected, show its title breakdown there. Excluded rows are shown with a muted style and a marker; their duration must not contribute to the panel's total.

- [ ] **Step 3: Add search**

A search box in the Day view header bound to a `SearchQuery` property, results from `SearchTitles` for the current period, grouped by app, each row showing time and title. Selecting a result selects that segment.

- [ ] **Step 4: Verify**

Run the colour-literal gate, `dotnet build Daylane.csproj`, and the full suite. **Do not launch the app** unless the controller confirms the user's Daylane is closed.

- [ ] **Step 5: Commit**

```bash
git add ViewModels/MainWindowViewModel.cs MainWindow.axaml
git commit -S -m "Add app detail and title search to the Day view

Excluded rows are visible but marked and excluded from the total. The
empty state says titles may be off rather than implying no activity."
```

---

### Task 14: Update the README

The README currently promises something this sub-project makes false. Shipping the capture and fixing the docs later is not acceptable.

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Rewrite the privacy section**

The current text reads:

> Stored: process name, exe path, time ranges, key/click **counts**. Not stored: keystrokes, window titles, screenshots, or mouse coordinates.

**Window titles and browser sites must move out of the "not stored" list** into an explicit opt-in description. Keystrokes, screenshots and mouse coordinates stay — they are still true. The new text must state:

- Both are **off by default**; an upgraded install records nothing new until you turn them on.
- Only the **site host** is stored for browsers, never the full address.
- Private keywords suppress recording for matching windows.
- Ignored windows stop counting toward totals and can be un-ignored.

Verify each claim against the source, not against this plan — the exact settings labels live in `MainWindow.axaml`, and the plan's prose has been wrong before on this project.

- [ ] **Step 2: Add the features to the Features list**

Add app detail and search. Use the in-app labels.

- [ ] **Step 3: Verify**

Re-read the whole README and check every claim still holds, not only the parts you changed. Then `dotnet test Daylane.Tests/Daylane.Tests.csproj` to confirm a docs change left the suite untouched.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -S -m "Document window title and browser site capture

Both move out of the not-stored list into an explicit opt-in
description; keystrokes, screenshots and mouse coordinates stay."
```

---

## Self-Review

**Spec coverage.** Every section maps to a task:

| Spec section | Task(s) |
|---|---|
| §4 privacy posture, default-off | 1, 6, 7, 14 |
| §5 ignore rules, the process-name requirement, empty-keyword hazard | 2 |
| §5 reversibility recompute | 9 |
| §5 privacy vs ignore orthogonality | 2, 3, 6 |
| §6.1 title capture, `SameIdentity` unchanged, PID-cache staleness | 5 |
| §6.2 browser host, UIA walk, depth, no scoring, COM safety, cost | 8 |
| §6.3 app detail, group by site, excluded shown | 10, 13 |
| §6.4 search, no FTS | 11, 13 |
| §6.5 settings | 12 |
| §7 schema v3 | 4 |
| §8 error handling | 5 (title), 8 (UIA/URL), 2 (malformed rule), 9 (recompute) |
| §9 testing | every task |
| §10 done criteria | 12 step 6, 13 step 4, 14 |

**Two gaps found and closed while reviewing:**

- The spec says the browser-host setting is "gated behind title capture in the UI", but a UI-only gate leaves the stored value able to say `true` while titles are off. Task 12 step 3 moves the guarantee into `Normalize()` so it holds regardless of UI.
- Nothing in the spec said what happens when *both* an ignore rule and a privacy keyword match the same window. Task 6 step 4 fixes the order — exclusion is evaluated before suppression strips the title — because the reverse would silently start counting time the user asked to drop.

**Type consistency.** `IgnoreRule(ProcessName, TitleKeyword)` is identical across Tasks 1, 2, 9 and 12. `IgnoreRules.IsExcluded(string, string?, IReadOnlyList<IgnoreRule>)` matches between Tasks 2, 6 and 9. `PrivacyKeywords.Suppresses(string?, string?, IReadOnlyList<string>)` matches between 3 and 6. `ForegroundApp.WindowTitle` / `.UrlHost` are used identically in 5, 6, 8, 10 and 11. `OpenSegment(ForegroundApp, DateTime, bool excluded = false)` is consistent from Task 6 onward.

**Test count** rises across tasks 1–12: 124 → 127 → 140 → 148 → 150 → 155 → 160 → 162 → 180 → 183 → 186 → 192 → 194. Tasks 13 and 14 add none by design. If a task's full-suite run reports fewer than its step says, a test was dropped rather than added — find it before moving on.
