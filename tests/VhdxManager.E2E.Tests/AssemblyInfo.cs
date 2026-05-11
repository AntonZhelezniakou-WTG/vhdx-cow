// Assembly-wide NUnit configuration for the E2E test project.
//
// Every test drives the single Hyper-V VM `VhdxManagerE2E` through one
// PSSession. Letting NUnit fan out across worker threads would have two
// fixtures racing over `Restore-VMSnapshot`, the shared session, and the
// guest filesystem. We force strictly serial execution at three layers
// (assembly, .runsettings, per-fixture `[Parallelizable(ParallelScope.None)]`)
// so a test author can't accidentally re-enable parallelism by adding an
// attribute somewhere.

using NUnit.Framework;

[assembly: LevelOfParallelism(1)]
[assembly: NonParallelizable]
