using FileLocker.Core.Security;
using Xunit;

namespace FileLocker.Core.Tests;

public class LockoutTrackerTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly LockoutTracker _tracker;

    public LockoutTrackerTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerLockoutTests_");
        _tracker = new LockoutTracker(Path.Combine(_tempDir.FullName, "lockout.json"));
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void CheckStatus_ForNeverSeenUuid_IsNotLockedOut()
    {
        var status = _tracker.CheckStatus(Guid.NewGuid().ToString());

        Assert.False(status.IsLockedOut);
        Assert.Null(status.RemainingLockout);
    }

    [Fact]
    public void RecordFailedAttempt_BelowThreshold_DoesNotLockOut()
    {
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 4; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }

        Assert.False(_tracker.CheckStatus(uuid).IsLockedOut);
    }

    [Fact]
    public void RecordFailedAttempt_ReachingThreshold_LocksOut()
    {
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }

        var status = _tracker.CheckStatus(uuid);

        Assert.True(status.IsLockedOut);
        Assert.True(status.RemainingLockout > TimeSpan.Zero);
    }

    [Fact]
    public void RecordSuccess_ClearsLockoutState()
    {
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }

        _tracker.RecordSuccess(uuid);

        Assert.False(_tracker.CheckStatus(uuid).IsLockedOut);
    }

    [Fact]
    public void RecordFailedAttempt_RepeatedLockouts_EscalatesDuration()
    {
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }
        var firstLockout = _tracker.CheckStatus(uuid).RemainingLockout!.Value;

        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }
        var secondLockout = _tracker.CheckStatus(uuid).RemainingLockout!.Value;

        Assert.True(secondLockout > firstLockout);
    }

    [Fact]
    public void DifferentUuids_AreLockedOutIndependently()
    {
        var uuidA = Guid.NewGuid().ToString();
        var uuidB = Guid.NewGuid().ToString();

        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuidA);
        }

        Assert.True(_tracker.CheckStatus(uuidA).IsLockedOut);
        Assert.False(_tracker.CheckStatus(uuidB).IsLockedOut);
    }
}