// QLNet's evaluation date (Settings.evaluationDate) is process-global ambient state.
// The production app serializes all QLNet work behind PricingService's lock; the test
// harness must likewise not run curve-building tests concurrently.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
