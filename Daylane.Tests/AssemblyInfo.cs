// Assembly-wide test settings. These deliberately live here rather than inside any one test
// file, so their scope is discoverable and deleting a single test class cannot silently change
// how the whole suite runs.

// Test collections run serially across the entire assembly.
//
// ThemedControlPaletteTests drives a real Avalonia Application through Avalonia.Headless. That
// platform can be set up only once per process, and its dispatcher belongs to the headless
// session's own UI thread, so it does not survive xunit running test classes on several threads
// at once -- the symptom is "The calling thread cannot access this object because a different
// thread owns it", and it appears only in a full-suite run, never when that class is run under a
// filter by itself.
//
// TrackerPauseResumeTests likewise starts real System.Threading.Timer polling, which is quieter
// without other classes running alongside it.
//
// The suite runs in ~2s serially, so this costs little. Remove it only if the headless Avalonia
// tests go away.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
