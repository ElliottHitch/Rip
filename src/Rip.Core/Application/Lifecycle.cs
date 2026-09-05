using Rip.Domain;

namespace Rip.Application;

public readonly record struct RunIdentity(Guid RunId, int Generation)
{
    public static RunIdentity New() => new(Guid.NewGuid(), 0);
}

public abstract record DownloadEvent(RunIdentity Run, DownloadStage Stage, long Sequence);
public sealed record DownloadProgress : DownloadEvent
{
    public DownloadProgress(
        RunIdentity run,
        DownloadStage stage,
        long sequence,
        double fraction,
        string activity)
        : base(run, stage, sequence)
    {
        Fraction = fraction;
        Activity = ErrorRedactor.Redact(activity);
    }

    public double Fraction { get; }
    public string Activity { get; }
}
public sealed record DownloadCompleted(
    RunIdentity Run,
    DownloadStage Stage,
    long Sequence,
    StagedArtifact Artifact)
    : DownloadEvent(Run, Stage, Sequence)
{
    public DownloadCompleted(
        RunIdentity run,
        DownloadStage stage,
        long sequence,
        StagedArtifact artifact,
        bool cleanupComplete)
        : this(run, stage, sequence, artifact)
    {
        CleanupComplete = cleanupComplete;
    }

    public bool CleanupComplete { get; init; } = true;
}

public sealed record DownloadMetadataCompleted : DownloadEvent
{
    public DownloadMetadataCompleted(
        RunIdentity run,
        DownloadStage stage,
        long sequence,
        MetadataSnapshot metadata)
        : base(run, stage, sequence)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public MetadataSnapshot Metadata { get; }
}
public sealed record DownloadCancelled(RunIdentity Run, DownloadStage Stage, long Sequence)
    : DownloadEvent(Run, Stage, Sequence);
public sealed record DownloadFailed(RunIdentity Run, DownloadStage Stage, long Sequence, SafeDownloadError Error)
    : DownloadEvent(Run, Stage, Sequence);

public enum LifecycleStatus
{
    Running,
    Completed,
    Cancelled,
    Failed
}

public sealed record LifecycleSnapshot(
    RunIdentity Run,
    LifecycleStatus Status = LifecycleStatus.Running,
    long LastSequence = 0,
    bool CancellationRequested = false)
{
    public bool IsTerminal => Status is LifecycleStatus.Completed or LifecycleStatus.Cancelled or LifecycleStatus.Failed;
}

public sealed record EventApplication(bool Accepted, LifecycleSnapshot State, string? RejectionReason);

public static class LifecycleReducer
{
    public static LifecycleSnapshot RequestCancellation(LifecycleSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.IsTerminal ? state : state with { CancellationRequested = true };
    }

    public static EventApplication Apply(LifecycleSnapshot state, DownloadEvent @event)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(@event);

        if (@event.Run != state.Run)
        {
            return Reject(state, "The event belongs to a stale or different run.");
        }

        if (state.IsTerminal)
        {
            return Reject(state, "Terminal state cannot receive another event.");
        }

        if (@event.Sequence <= state.LastSequence)
        {
            return Reject(state, "Event sequences must increase monotonically.");
        }

        if (@event is DownloadProgress progress)
        {
            if (progress.Fraction is < 0 or > 1)
            {
                return Reject(state, "Progress must be between zero and one.");
            }

            if (state.CancellationRequested)
            {
                return Reject(state, "Progress is not accepted after cancellation was requested.");
            }

            return Accept(state with { LastSequence = @event.Sequence });
        }

        var nextStatus = @event switch
        {
            DownloadCompleted => LifecycleStatus.Completed,
            DownloadMetadataCompleted => LifecycleStatus.Completed,
            DownloadCancelled => LifecycleStatus.Cancelled,
            DownloadFailed => LifecycleStatus.Failed,
            _ => (LifecycleStatus?)null
        };

        return nextStatus is { } terminal
            ? Accept(state with { Status = terminal, LastSequence = @event.Sequence })
            : Reject(state, "Unknown event type.");
    }

    private static EventApplication Accept(LifecycleSnapshot state) => new(true, state, null);
    private static EventApplication Reject(LifecycleSnapshot state, string reason) => new(false, state, reason);
}
