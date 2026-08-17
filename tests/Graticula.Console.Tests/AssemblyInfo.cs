using Xunit;

// <b>One browser at a time, in an assembly whose tests each launch one.</b> xUnit
// runs collections in parallel by default, and four concurrent Chromes against one
// development server measures the machine rather than the console — the same
// contention D-60 is about, arriving here for a different reason.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
