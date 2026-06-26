using Xunit;

// Integration test classes each spin up the API and run EF migrations against the
// shared test database; serialize collections so two classes don't migrate concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
