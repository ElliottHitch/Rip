using Rip.Application;
using Rip.Domain;

namespace Rip.Core.Tests;

public sealed class LifecycleTests
{
    private static readonly RunIdentity Run = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 0);
    private static readonly RunIdentity OtherRun = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), 0);

    [Fact]
    public void Nonterminal_progress_then_one_terminal_event_is_accepted()
    {
        var initial = new LifecycleSnapshot(Run);
        var progress = LifecycleReducer.Apply(initial,
            new DownloadProgress(Run, DownloadStage.Downloading, 1, .5, "halfway"));
        var completed = LifecycleReducer.Apply(progress.State,
            new DownloadCompleted(Run, DownloadStage.Publishing, 2,
                new StagedArtifact("stage-1", "video.mp4", OutputContainer.Mp4, 100, true)));
        var lateProgress = LifecycleReducer.Apply(completed.State,
            new DownloadProgress(Run, DownloadStage.Downloading, 3, .9, "late"));

        Assert.True(progress.Accepted);
        Assert.True(completed.Accepted);
        Assert.Equal(LifecycleStatus.Completed, completed.State.Status);
        Assert.True(completed.State.IsTerminal);
        Assert.False(lateProgress.Accepted);
    }

    [Fact]
    public void Stale_run_events_are_rejected_without_mutating_state()
    {
        var initial = new LifecycleSnapshot(Run);
        var result = LifecycleReducer.Apply(initial,
            new DownloadProgress(OtherRun, DownloadStage.Downloading, 1, .1, "stale"));

        Assert.False(result.Accepted);
        Assert.Equal(initial, result.State);
        Assert.Contains("stale", result.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Older_generation_is_rejected_even_when_run_id_matches()
    {
        var current = new RunIdentity(Run.RunId, 1);
        var initial = new LifecycleSnapshot(current);
        var stale = LifecycleReducer.Apply(initial,
            new DownloadProgress(new RunIdentity(Run.RunId, 0), DownloadStage.Downloading, 1, .1, "stale"));

        Assert.False(stale.Accepted);
        Assert.Equal(initial, stale.State);
    }

    [Fact]
    public void Cancellation_request_blocks_progress_and_accepts_cancelled_terminal_event()
    {
        var requested = LifecycleReducer.RequestCancellation(new LifecycleSnapshot(Run));
        var progress = LifecycleReducer.Apply(requested,
            new DownloadProgress(Run, DownloadStage.Downloading, 1, .1, "not accepted"));
        var cancelled = LifecycleReducer.Apply(requested,
            new DownloadCancelled(Run, DownloadStage.Downloading, 1));

        Assert.True(requested.CancellationRequested);
        Assert.False(progress.Accepted);
        Assert.True(cancelled.Accepted);
        Assert.Equal(LifecycleStatus.Cancelled, cancelled.State.Status);
    }

    [Fact]
    public void Duplicate_or_out_of_order_sequences_are_rejected()
    {
        var initial = new LifecycleSnapshot(Run);
        var first = LifecycleReducer.Apply(initial,
            new DownloadProgress(Run, DownloadStage.Downloading, 2, .2, "first"));
        var duplicate = LifecycleReducer.Apply(first.State,
            new DownloadProgress(Run, DownloadStage.Downloading, 2, .3, "duplicate"));
        var older = LifecycleReducer.Apply(first.State,
            new DownloadProgress(Run, DownloadStage.Downloading, 1, .1, "older"));

        Assert.True(first.Accepted);
        Assert.False(duplicate.Accepted);
        Assert.False(older.Accepted);
        Assert.Equal(2, first.State.LastSequence);
    }

    [Fact]
    public void Event_types_make_terminal_truth_explicit()
    {
        Assert.IsType<DownloadProgress>(new DownloadProgress(Run, DownloadStage.Downloading, 1, .1, "activity"));
        var completed = Assert.IsType<DownloadCompleted>(new DownloadCompleted(Run, DownloadStage.Publishing, 2,
            new StagedArtifact("stage-1", "video.mp4", OutputContainer.Mp4, 100, true)));
        Assert.True(completed.CleanupComplete);
        Assert.False(new DownloadCompleted(Run, DownloadStage.Publishing, 2,
            new StagedArtifact("stage-1", "video.mp4", OutputContainer.Mp4, 100, true), cleanupComplete: false).CleanupComplete);
        Assert.IsType<DownloadCancelled>(new DownloadCancelled(Run, DownloadStage.Downloading, 2));
        Assert.IsType<DownloadFailed>(new DownloadFailed(Run, DownloadStage.Downloading, 2,
            SafeDownloadError.Create(DownloadErrorCode.Unknown, DownloadStage.Downloading,
                "safe failure", RetryAction.None, new RedactedDiagnosticToken("diag-test"))));
        Assert.IsType<DownloadMetadataCompleted>(new DownloadMetadataCompleted(Run, DownloadStage.Metadata, 2,
            new MetadataSnapshot("title", null, null, null)));
    }

    [Fact]
    public void Metadata_terminal_event_reduces_to_completed_without_changing_stale_rules()
    {
        var result = LifecycleReducer.Apply(new LifecycleSnapshot(Run),
            new DownloadMetadataCompleted(Run, DownloadStage.Metadata, 1,
                new MetadataSnapshot("title", null, null, null)));

        Assert.True(result.Accepted);
        Assert.Equal(LifecycleStatus.Completed, result.State.Status);
        Assert.True(result.State.IsTerminal);
    }
}
