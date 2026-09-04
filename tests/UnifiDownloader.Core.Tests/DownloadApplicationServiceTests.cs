using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Core.Tests;

public sealed class DownloadApplicationServiceTests
{
    private static readonly string[] MetadataOnlyCalls = ["metadata"];
    private static readonly string[] SuccessfulCalls = ["metadata", "resolve", "stage", "process", "publish", "release"];

    [Fact]
    public async Task Metadata_operation_is_terminal_and_does_not_stage_or_publish()
    {
        var ports = FakePorts.Create(DownloadOperation.Metadata);
        ports.Metadata = new MetadataSnapshot(
            "A title https://remote.test/watch?v=secret path=/private/secret",
            TimeSpan.FromMinutes(2),
            "uploader",
            null);

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.MetadataSucceeded, result.Status);
        Assert.NotNull(result.Metadata);
        Assert.Null(result.PublishedFile);
        Assert.Null(result.Error);
        Assert.Equal(MetadataOnlyCalls, ports.Calls);
        Assert.DoesNotContain("secret", result.Metadata!.Title, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<DownloadMetadataCompleted>(ports.Observer.Events.Last());
        var terminal = Assert.IsType<DownloadMetadataCompleted>(ports.Observer.Events.Last());
        Assert.Equal(result.Run, terminal.Run);
        Assert.Equal(3, terminal.Sequence);
        Assert.Empty(ports.Diagnostics.Errors);
    }

    [Theory]
    [InlineData(23d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Invalid_frame_rate_target_fails_before_provider_side_effects(double target)
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.Request = ports.Request with
        {
            Output = ports.Request.Output with { FrameRateTarget = target }
        };

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Failed, result.Status);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Equal(DownloadStage.Validating, result.Error.Stage);
        Assert.Empty(ports.Calls);
    }

    [Theory]
    [InlineData(24d)]
    [InlineData(25d)]
    [InlineData(30d)]
    public async Task Valid_frame_rate_target_reaches_the_typed_output_contract(double target)
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.Request = ports.Request with
        {
            Output = ports.Request.Output with { FrameRateTarget = target }
        };

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Published, result.Status);
        Assert.Equal(target, ports.Publisher.Output!.FrameRateTarget);
    }

    [Fact]
    public async Task Video_success_publishes_before_one_non_cancelled_release_and_completes()
    {
        var ports = FakePorts.Create(DownloadOperation.Video);

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Published, result.Status);
        Assert.True(result.IsPublished);
        Assert.NotNull(result.PublishedFile);
        Assert.True(result.CleanupComplete);
        Assert.Equal(SuccessfulCalls, ports.Calls);
        Assert.Equal(1, ports.Stager.ReleaseCalls);
        Assert.False(ports.Stager.ReleaseTokenWasCancelled);
        Assert.Single(ports.Observer.Events.OfType<DownloadCompleted>());
        Assert.False(ports.Publisher.Output!.AllowOverwrite);
        Assert.Equal(DownloadStage.Publishing, ports.Observer.Events.Last().Stage);
        Assert.True(ports.Observer.Events.Zip(ports.Observer.Events.Skip(1), (first, second) => first.Sequence < second.Sequence).All(pair => pair));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing-expected")]
    [InlineData("unexpected-channel")]
    [InlineData("unverified")]
    [InlineData("nonpositive")]
    [InlineData("duplicate")]
    public async Task Invalid_successful_stage_values_fail_before_processing_or_publication(string shape)
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.Stager.InputsOverride = shape switch
        {
            "empty" => new LocalMediaInputs(),
            "missing-expected" => new LocalMediaInputs(Audio: AudioHandle()),
            "unexpected-channel" => new LocalMediaInputs(Video: VideoHandle()),
            "unverified" => new LocalMediaInputs(Video: new LocalMediaInputHandle("video-input", LocalMediaChannel.Video, 10, verified: false), Audio: AudioHandle()),
            "nonpositive" => new LocalMediaInputs(Video: InvalidHandle(LocalMediaChannel.Video, 0, verified: true), Audio: AudioHandle()),
            "duplicate" => new LocalMediaInputs(
                Video: VideoHandle("same-input"),
                Audio: AudioHandle("same-input")),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        if (shape == "unexpected-channel")
        {
            ports.Request = ports.Request with { Operation = DownloadOperation.Audio };
        }

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Failed, result.Status);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Equal(DownloadStage.Downloading, result.Error.Stage);
        Assert.Equal("diag-application-staged-inputs-invalid", result.Error.Diagnostic.Value);
        Assert.Equal(["metadata", "resolve", "stage", "release"], ports.Calls);
        Assert.Equal(0, ports.Processor.Calls);
        Assert.Equal(0, ports.Publisher.Calls);
        Assert.Equal(1, ports.Stager.ReleaseCalls);
        Assert.False(ports.Stager.ReleaseTokenWasCancelled);
        Assert.Empty(ports.Observer.Events.OfType<DownloadCompleted>());
        Assert.Null(result.PublishedFile);
    }

    [Fact]
    public async Task Null_successful_stage_value_is_rejected_and_still_released_once()
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.Stager.ReturnNullInputs = true;

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Failed, result.Status);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        Assert.Equal(["metadata", "resolve", "stage", "release"], ports.Calls);
        Assert.Equal(0, ports.Processor.Calls);
        Assert.Equal(0, ports.Publisher.Calls);
        Assert.Equal(1, ports.Stager.ReleaseCalls);
        Assert.False(ports.Stager.ReleaseTokenWasCancelled);
    }

    [Fact]
    public async Task Audio_success_uses_audio_plan_and_preserves_run_scoped_browser_selection()
    {
        var ports = FakePorts.Create(DownloadOperation.Audio);
        var browser = BrowserSessionSelection.Create(BrowserKind.Firefox);
        ports.Request = ports.Request with { BrowserSession = browser };

        var result = await ports.Service.RunAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.True(result.IsPublished);
        Assert.Same(browser, ports.Provider.MetadataBrowser);
        Assert.Same(browser, ports.Provider.ResolveRequest!.BrowserSession);
        Assert.Equal(DownloadOperation.Audio, ports.Provider.ResolveRequest.Operation);
        Assert.Same(browser, ports.Stager.Plan!.Request.BrowserSession);
        Assert.Same(browser, ports.Processor.Plan!.Request.BrowserSession);
        Assert.Equal(LocalMediaChannel.Audio, ports.Stager.Inputs!.Audio!.Channel);
        Assert.Null(ports.Stager.Inputs.Video);
    }

    [Fact]
    public async Task One_access_denied_stage_refreshes_once_then_stages_fresh_plan()
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.Stager.StageErrors.Enqueue(AccessDeniedError());

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Published, result.Status);
        Assert.Equal(2, ports.Provider.ResolveCalls);
        Assert.Equal(2, ports.Stager.StageCalls);
        Assert.Equal(["metadata", "resolve", "stage", "resolve", "stage", "process", "publish", "release"], ports.Calls);
    }

    [Fact]
    public async Task Rate_limit_does_not_enter_the_refresh_loop()
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.Stager.StageErrors.Enqueue(SafeDownloadError.Create(
            DownloadErrorCode.RateLimited,
            DownloadStage.Downloading,
            "rate limited",
            RetryAction.UserActionRequired,
            new RedactedDiagnosticToken("diag-rate-limited")));

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Failed, result.Status);
        Assert.Equal(1, ports.Provider.ResolveCalls);
        Assert.Equal(1, ports.Stager.StageCalls);
        Assert.Equal(["metadata", "resolve", "stage"], ports.Calls);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task Refresh_failure_return_throw_and_cancellation_are_safe(string mode)
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.Stager.StageErrors.Enqueue(AccessDeniedError());
        ports.RefreshMode = mode;
        using var cancellation = new CancellationTokenSource();
        ports.RefreshCancellation = cancellation;

        var result = await ports.Service.ExecuteAsync(ports.Request, cancellation.Token);

        Assert.Equal(mode == "cancel" ? DownloadRunStatus.Cancelled : DownloadRunStatus.Failed, result.Status);
        Assert.Equal(mode == "cancel" ? DownloadErrorCode.Cancelled : DownloadErrorCode.ProviderUnavailable, result.Error!.Code);
        Assert.Equal(2, ports.Provider.ResolveCalls);
        Assert.Equal(1, ports.Stager.StageCalls);
        Assert.DoesNotContain("https://", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("resolve")]
    [InlineData("stage")]
    [InlineData("process")]
    [InlineData("publish")]
    public async Task Typed_port_failures_are_safe_and_staged_inputs_are_released(string failure)
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.FailAt = failure;

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Null(result.PublishedFile);
        Assert.DoesNotContain("https://", result.Error!.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/secret", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ports.Diagnostics.Errors, error => error == result.Error);
        if (failure is "process" or "publish")
        {
            Assert.Equal(1, ports.Stager.ReleaseCalls);
            Assert.False(ports.Stager.ReleaseTokenWasCancelled);
        }
        else
        {
            Assert.Equal(0, ports.Stager.ReleaseCalls);
        }
    }

    [Fact]
    public async Task Cancellation_before_publication_is_typed_and_still_releases_inputs()
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        using var cancellation = new CancellationTokenSource();
        ports.Processor.AfterCall = cancellation.Cancel;

        var result = await ports.Service.ExecuteAsync(ports.Request, cancellation.Token);

        Assert.Equal(DownloadRunStatus.Cancelled, result.Status);
        Assert.Equal(DownloadErrorCode.Cancelled, result.Error!.Code);
        Assert.Null(result.PublishedFile);
        Assert.Equal(0, ports.Publisher.Calls);
        Assert.Equal(1, ports.Stager.ReleaseCalls);
        Assert.False(ports.Stager.ReleaseTokenWasCancelled);
        Assert.IsType<DownloadCancelled>(ports.Observer.Events.Last());
    }

    [Fact]
    public async Task Publication_is_commit_point_when_cancellation_happens_after_publish()
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        using var cancellation = new CancellationTokenSource();
        ports.Publisher.AfterCall = cancellation.Cancel;

        var result = await ports.Service.ExecuteAsync(ports.Request, cancellation.Token);

        Assert.Equal(DownloadRunStatus.Published, result.Status);
        Assert.NotNull(result.PublishedFile);
        Assert.Equal(1, ports.Publisher.Calls);
        Assert.IsType<DownloadCompleted>(ports.Observer.Events.Last());
    }

    [Fact]
    public async Task Cleanup_warning_does_not_turn_publication_into_failure()
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.Stager.ReleaseResult = new ProviderResult<StageReleaseResult>(null, Error("cleanup"));

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Published, result.Status);
        Assert.False(result.CleanupComplete);
        Assert.IsType<DownloadCompleted>(ports.Observer.Events.Last());
        Assert.False(((DownloadCompleted)ports.Observer.Events.Last()).CleanupComplete);
        Assert.Contains(ports.Diagnostics.Errors, error => error.Diagnostic.Value == "diag-application-cleanup-incomplete");
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("process")]
    [InlineData("publish")]
    public async Task Unexpected_port_exceptions_become_fixed_safe_errors(string throwAt)
    {
        var ports = FakePorts.Create(DownloadOperation.Video);
        ports.ThrowAt = throwAt;

        var result = await ports.Service.ExecuteAsync(ports.Request, TestContext.Current.CancellationToken);

        Assert.Equal(DownloadRunStatus.Failed, result.Status);
        Assert.Equal(DownloadErrorCode.Unknown, result.Error!.Code);
        Assert.Equal("The download operation failed unexpectedly.", result.Error.UserMessage);
        Assert.Equal("diag-application-unexpected", result.Error.Diagnostic.Value);
        Assert.DoesNotContain("exception", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
        if (throwAt is "process" or "publish")
        {
            Assert.Equal(1, ports.Stager.ReleaseCalls);
        }
    }

    [Fact]
    public async Task Every_invocation_gets_a_fresh_run_identity()
    {
        var firstPorts = FakePorts.Create(DownloadOperation.Metadata);
        var secondPorts = FakePorts.Create(DownloadOperation.Metadata);

        var first = await firstPorts.Service.ExecuteAsync(firstPorts.Request, TestContext.Current.CancellationToken);
        var second = await secondPorts.Service.ExecuteAsync(secondPorts.Request, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Run, second.Run);
        Assert.All(firstPorts.Observer.Events, @event => Assert.Equal(first.Run, @event.Run));
        Assert.All(secondPorts.Observer.Events, @event => Assert.Equal(second.Run, @event.Run));
    }

    private static SafeDownloadError Error(string diagnosticSuffix) => SafeDownloadError.Create(
        DownloadErrorCode.ProviderUnavailable,
        DownloadStage.Downloading,
        $"safe failure https://remote.test/{diagnosticSuffix} path=/secret/{diagnosticSuffix}",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-test-failure"));

    private static SafeDownloadError AccessDeniedError() => SafeDownloadError.Create(
        DownloadErrorCode.AccessDenied,
        DownloadStage.Downloading,
        "stream access denied",
        RetryAction.RefreshStream,
        new RedactedDiagnosticToken("diag-access-denied"));

    private static LocalMediaInputHandle VideoHandle(string key = "video-input") =>
        new(key, LocalMediaChannel.Video, 10, verified: true);

    private static LocalMediaInputHandle AudioHandle(string key = "audio-input") =>
        new(key, LocalMediaChannel.Audio, 10, verified: true);

    private static LocalMediaInputHandle InvalidHandle(LocalMediaChannel channel, long lengthBytes, bool verified)
    {
        var handle = (LocalMediaInputHandle)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(LocalMediaInputHandle));
        var type = typeof(LocalMediaInputHandle);
        type.GetField("<InputKey>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(handle, "invalid-input");
        type.GetField("<Channel>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(handle, channel);
        type.GetField("<LengthBytes>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(handle, lengthBytes);
        type.GetField("<Verified>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(handle, verified);
        return handle;
    }

    private sealed class FakePorts
    {
        private FakePorts(DownloadOperation operation)
        {
            Request = new DownloadRequest(
                new VideoReference(new Uri("https://video.test/watch?id=opaque")),
                operation,
                new OutputOptions("output", "video", OutputContainer.UnifiMp4),
                BrowserSessionSelection.Create(BrowserKind.Chrome));
            Provider = new FakeProvider(this);
            Stager = new FakeStager(this);
            Processor = new FakeProcessor(this);
            Publisher = new FakePublisher(this);
            Observer = new FakeObserver();
            Diagnostics = new FakeDiagnostics();
            Service = new DownloadApplicationService(Provider, Stager, Processor, Publisher, Observer, Diagnostics);
        }

        public static FakePorts Create(DownloadOperation operation) => new(operation);
        public DownloadRequest Request { get; set; }
        public string? FailAt { get; set; }
        public string? ThrowAt { get; set; }
        public List<string> Calls { get; } = [];
        public FakeProvider Provider { get; }
        public FakeStager Stager { get; }
        public FakeProcessor Processor { get; }
        public FakePublisher Publisher { get; }
        public FakeObserver Observer { get; }
        public FakeDiagnostics Diagnostics { get; }
        public DownloadApplicationService Service { get; }
        public string? RefreshMode { get; set; }
        public CancellationTokenSource? RefreshCancellation { get; set; }
        public MetadataSnapshot Metadata { get; set; } = new("Fixture title", TimeSpan.FromSeconds(5), "Fixture uploader", null);

        public MediaPlan Plan => new(
            Request,
            Request.Operation == DownloadOperation.Audio
                ? new MediaCharacteristics(OutputContainer.Mp4, VideoCodec.Unknown, AudioCodec.Aac, false, true)
                : new MediaCharacteristics(OutputContainer.Mp4, VideoCodec.H264, AudioCodec.Aac, true, true),
            Request.Operation == DownloadOperation.Audio ? null : "video-format",
            Request.Operation == DownloadOperation.Audio ? "audio-format" : "audio-format");
    }

    private sealed class FakeProvider(FakePorts ports) : IVideoProvider
    {
        public BrowserSessionSelection? MetadataBrowser { get; private set; }
        public DownloadRequest? ResolveRequest { get; private set; }
        public int ResolveCalls { get; private set; }

        public ValueTask<ProviderResult<MetadataSnapshot>> ReadMetadataAsync(
            VideoReference video,
            BrowserSessionSelection? browserSession,
            CancellationToken cancellationToken)
        {
            ports.Calls.Add("metadata");
            MetadataBrowser = browserSession;
            if (ports.ThrowAt == "provider") throw new InvalidOperationException("exception=/secret/provider");
            return ports.FailAt == "provider"
                ? ValueTask.FromResult(new ProviderResult<MetadataSnapshot>(null, Error("provider")))
                : ValueTask.FromResult(new ProviderResult<MetadataSnapshot>(ports.Metadata, null));
        }

        public ValueTask<ProviderResult<MediaPlan>> ResolveMediaAsync(
            DownloadRequest request,
            MetadataSnapshot metadata,
            CancellationToken cancellationToken)
        {
            ports.Calls.Add("resolve");
            ResolveCalls++;
            ResolveRequest = request;
            if (ports.ThrowAt == "resolve") throw new InvalidOperationException("exception=/secret/resolve");
            if (ResolveCalls > 1 && ports.RefreshMode == "throw") throw new InvalidOperationException("refresh exception=/secret/refresh");
            if (ResolveCalls > 1 && ports.RefreshMode == "cancel")
            {
                ports.RefreshCancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (ResolveCalls > 1 && ports.RefreshMode == "return")
                return ValueTask.FromResult(new ProviderResult<MediaPlan>(null, Error("refresh")));
            return ports.FailAt == "resolve"
                ? ValueTask.FromResult(new ProviderResult<MediaPlan>(null, Error("resolve")))
                : ValueTask.FromResult(new ProviderResult<MediaPlan>(ports.Plan, null));
        }
    }

    private sealed class FakeStager(FakePorts ports) : ILocalStreamStager
    {
        public MediaPlan? Plan { get; private set; }
        public LocalMediaInputs? Inputs { get; private set; }
        public LocalMediaInputs? InputsOverride { get; set; }
        public bool ReturnNullInputs { get; set; }
        public int StageCalls { get; private set; }
        public Queue<SafeDownloadError> StageErrors { get; } = new();
        public int ReleaseCalls { get; private set; }
        public bool ReleaseTokenWasCancelled { get; private set; }
        public ProviderResult<StageReleaseResult> ReleaseResult { get; set; } = new(new StageReleaseResult(1, true), null);

        public ValueTask<ProviderResult<LocalMediaInputs>> StageAsync(MediaPlan plan, CancellationToken cancellationToken)
        {
            ports.Calls.Add("stage");
            StageCalls++;
            Plan = plan;
            if (ports.ThrowAt == "stage") throw new InvalidOperationException("exception=/secret/stage");
            if (ports.FailAt == "stage")
            {
                return ValueTask.FromResult(new ProviderResult<LocalMediaInputs>(null, Error("stage")));
            }
            if (StageErrors.TryDequeue(out var stageError))
            {
                return ValueTask.FromResult(new ProviderResult<LocalMediaInputs>(null, stageError));
            }

            var inputs = InputsOverride ?? (plan.Request.Operation == DownloadOperation.Audio
                ? new LocalMediaInputs(Audio: new LocalMediaInputHandle("audio-input", LocalMediaChannel.Audio, 10, true))
                : new LocalMediaInputs(
                    new LocalMediaInputHandle("video-input", LocalMediaChannel.Video, 10, true),
                    new LocalMediaInputHandle("audio-input", LocalMediaChannel.Audio, 10, true)));
            Inputs = inputs;
            if (ReturnNullInputs)
            {
                return ValueTask.FromResult(new ProviderResult<LocalMediaInputs>(null, null));
            }

            return ValueTask.FromResult(new ProviderResult<LocalMediaInputs>(inputs, null));
        }

        public ValueTask<ProviderResult<StageReleaseResult>> ReleaseAsync(LocalMediaInputs inputs, CancellationToken cancellationToken)
        {
            ports.Calls.Add("release");
            ReleaseCalls++;
            ReleaseTokenWasCancelled = cancellationToken.IsCancellationRequested;
            return ValueTask.FromResult(ReleaseResult);
        }
    }

    private sealed class FakeProcessor(FakePorts ports) : IStagedMediaProcessor
    {
        public MediaPlan? Plan { get; private set; }
        public int Calls { get; private set; }
        public Action? AfterCall { get; set; }

        public ValueTask<ProviderResult<MediaProcessingResult>> ProcessAsync(
            MediaPlan plan,
            LocalMediaInputs inputs,
            CancellationToken cancellationToken)
        {
            ports.Calls.Add("process");
            Calls++;
            Plan = plan;
            AfterCall?.Invoke();
            if (ports.ThrowAt == "process") throw new InvalidOperationException("exception=/secret/process");
            if (ports.FailAt == "process")
            {
                return ValueTask.FromResult(new ProviderResult<MediaProcessingResult>(null, Error("process")));
            }

            return ValueTask.FromResult(new ProviderResult<MediaProcessingResult>(
                new MediaProcessingResult(
                    new StagedArtifact("stage-key", "video.mp4", OutputContainer.Mp4, 20, true),
                    true),
                null));
        }
    }

    private sealed class FakePublisher(FakePorts ports) : IPublicationStore
    {
        public int Calls { get; private set; }
        public OutputOptions? Output { get; private set; }
        public Action? AfterCall { get; set; }

        public ValueTask<ProviderResult<VerifiedLocalMp4>> PublishAsync(
            StagedArtifact artifact,
            OutputOptions output,
            CancellationToken cancellationToken)
        {
            ports.Calls.Add("publish");
            Calls++;
            Output = output;
            AfterCall?.Invoke();
            if (ports.ThrowAt == "publish") throw new InvalidOperationException("exception=/secret/publish");
            return ports.FailAt == "publish"
                ? ValueTask.FromResult(new ProviderResult<VerifiedLocalMp4>(null, Error("publish")))
                : ValueTask.FromResult(new ProviderResult<VerifiedLocalMp4>(
                    new VerifiedLocalMp4("video.mp4", "output-key", 20), null));
        }
    }

    private sealed class FakeObserver : IDownloadObserver
    {
        public List<DownloadEvent> Events { get; } = [];

        public ValueTask ObserveAsync(DownloadEvent downloadEvent, CancellationToken cancellationToken)
        {
            Events.Add(downloadEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDiagnostics : IDiagnostics
    {
        public List<SafeDownloadError> Errors { get; } = [];

        public void Report(SafeDownloadError downloadError) => Errors.Add(downloadError);
    }
}
