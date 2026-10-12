// How the project's tests run beside each other: a class is a collection of its own, its tests one
// at a time, and three collections run at once, no more. Every class that is not in the shared
// collection creates a database of its own per test, and its first start in it compiles the
// store's catalog comparison, which a small SQL Server grants memory for a few at a time: more
// than a few at once stalled it. Three is well inside what it served before.
[assembly: Xunit.v3.Parallelization(MaxThreads = 3)]
