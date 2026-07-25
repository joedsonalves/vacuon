using Xunit;

// xUnit runs test CLASSES in parallel, and the selected language is process-wide state —
// deliberately so, since the app shows one language at a time. The two classes that switch
// to Portuguese and switch back therefore poisoned whatever else happened to be running:
// CommandHeuristicsTests and SnapshotDescriptionTests failed at random, one per run, on
// assertions against translated text.
//
// A flaky test is worse than a failing one, because it teaches you to re-run instead of to
// look. The whole suite finishes in about 200 ms, so serialising it costs nothing worth
// having.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
