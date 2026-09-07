using System.Security;
using Rip.Application;
using Rip.Domain;

namespace Rip.Infrastructure;

/// <summary>
/// Resolves only handles issued by <see cref="LocalStreamStager"/> and delegates to the
/// accepted path-only FFmpeg adapter. No caller-supplied output path is accepted.
/// </summary>
public sealed class StagedFfmpegProcessor : IStagedMediaProcessor
{
    private readonly FfmpegProcessAdapter adapter;
    private readonly LocalStreamStager stager;
    private readonly FfmpegStageTarget stageTarget;

    public StagedFfmpegProcessor(
        FfmpegProcessAdapter adapter,
        LocalStreamStager stager,
        FfmpegStageTarget stageTarget)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.stager = stager ?? throw new ArgumentNullException(nameof(stager));
        this.stageTarget = stageTarget ?? throw new ArgumentNullException(nameof(stageTarget));
    }

    public async ValueTask<ProviderResult<MediaProcessingResult>> ProcessAsync(
        MediaPlan plan,
        LocalMediaInputs inputs,
        CancellationToken cancellationToken)
    {
        if (!TryValidateInputs(plan, inputs, out var video, out var audio))
        {
            return Failure<MediaProcessingResult>(SafeInfrastructureErrors.InvalidLocalStreamRequest());
        }

        try
        {
            var ffmpegInputs = new FfmpegInputSet(video, audio);
            return await adapter.ProcessAsync(plan, ffmpegInputs, stageTarget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            // Infrastructure details, including paths, never cross the Core result boundary.
            return Failure<MediaProcessingResult>(SafeInfrastructureErrors.InvalidLocalStreamRequest());
        }
    }

    private bool TryValidateInputs(
        MediaPlan plan,
        LocalMediaInputs inputs,
        out string? video,
        out string? audio)
    {
        video = null;
        audio = null;
        if (plan is null || inputs is null || plan.Request is null || plan.Characteristics is null)
        {
            return false;
        }

        var handles = new List<LocalMediaInputHandle>(2);
        if (inputs.Video is not null)
        {
            if (inputs.Video.Channel != LocalMediaChannel.Video ||
                !plan.Characteristics.HasVideo || string.IsNullOrWhiteSpace(plan.VideoFormatId) ||
                !TryAddUnique(inputs.Video, handles) ||
                !stager.TryResolve(plan, inputs.Video, out video))
            {
                return false;
            }
        }
        else if (plan.Characteristics.HasVideo || plan.VideoFormatId is not null)
        {
            return false;
        }

        if (inputs.Audio is not null)
        {
            if (inputs.Audio.Channel != LocalMediaChannel.Audio ||
                !plan.Characteristics.HasAudio || string.IsNullOrWhiteSpace(plan.AudioFormatId) ||
                !TryAddUnique(inputs.Audio, handles) ||
                !stager.TryResolve(plan, inputs.Audio, out audio))
            {
                return false;
            }
        }
        else if (!plan.IsProgressive && (plan.Characteristics.HasAudio || plan.AudioFormatId is not null))
        {
            return false;
        }

        return handles.Count > 0;
    }

    private static bool TryAddUnique(LocalMediaInputHandle handle, List<LocalMediaInputHandle> handles)
    {
        if (!handle.Verified || handle.LengthBytes <= 0 ||
            handles.Any(existing => existing.InputKey == handle.InputKey))
        {
            return false;
        }

        handles.Add(handle);
        return true;
    }

    private static ProviderResult<T> Failure<T>(SafeDownloadError error) => new(default, error);
}
