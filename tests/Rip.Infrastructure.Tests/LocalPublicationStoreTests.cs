using Rip.Application;
using Rip.Domain;
using Rip.Infrastructure;

namespace Rip.Infrastructure.Tests;

public sealed class LocalPublicationStoreTests
{
    [Theory]
    [InlineData(OutputContainer.Mp4)]
    [InlineData(OutputContainer.UnifiMp4)]
    [InlineData(OutputContainer.Matroska)]
    public async Task Publishes_each_supported_container_to_verified_output_and_consumes_source(OutputContainer container)
    {
        using var fixture = Fixture.Create();
        var artifact = fixture.Register(container == OutputContainer.Matroska ? "video.mkv" : "video.mp4", container, "synthetic staged bytes");
        var diagnostics = new TestDiagnostics();
        var published = new PublishedOutputRegistry();
        var result = await new LocalPublicationStore(fixture.Registry, published, diagnostics).PublishAsync(
            artifact,
            new OutputOptions(fixture.Destination, "safe video", container),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        var output = result.Value!;
        Assert.Equal($"safe video.{(container == OutputContainer.Matroska ? "mkv" : "mp4")}", output.FileName);
        Assert.Matches("^output-[0-9a-f]{32}$", output.OutputKey);
        Assert.Equal(22, output.LengthBytes);
        Assert.True(published.TryResolve(output, out var finalPath));
        Assert.Equal(output.FileName, Path.GetFileName(finalPath));
        Assert.Equal(output.LengthBytes, new FileInfo(finalPath).Length);
        Assert.False(fixture.SourceExists);
        Assert.False(fixture.Registry.Owns(artifact));
        Assert.Empty(diagnostics.Errors);
        Assert.Empty(Directory.EnumerateFiles(fixture.Destination, ".unifi-publication-*.tmp"));
    }

    [Fact]
    public async Task Existing_final_and_allow_overwrite_are_typed_conflicts_without_touching_data()
    {
        using var fixture = Fixture.Create();
        var artifact = fixture.Register("video.mp4", OutputContainer.Mp4, "source bytes");
        var final = Path.Combine(fixture.Destination, "same.mp4");
        File.WriteAllText(final, "pre-existing");
        var store = new LocalPublicationStore(fixture.Registry, new PublishedOutputRegistry(), new TestDiagnostics());

        var collision = await store.PublishAsync(artifact, new OutputOptions(fixture.Destination, "same", OutputContainer.UnifiMp4), CancellationToken.None);
        Assert.False(collision.IsSuccess);
        Assert.Equal(DownloadErrorCode.PublicationConflict, collision.Error!.Code);
        Assert.Equal("pre-existing", File.ReadAllText(final));
        Assert.True(fixture.SourceExists);

        var overwrite = await store.PublishAsync(artifact, new OutputOptions(fixture.Destination, "new", OutputContainer.UnifiMp4, AllowOverwrite: true), CancellationToken.None);
        Assert.False(overwrite.IsSuccess);
        Assert.Equal(DownloadErrorCode.PublicationConflict, overwrite.Error!.Code);
        Assert.True(fixture.SourceExists);
        Assert.DoesNotContain("/", overwrite.Error.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relative_missing_and_reparse_destinations_fail_before_copy()
    {
        using var fixture = Fixture.Create();
        var artifact = fixture.Register("video.mp4", OutputContainer.Mp4, "source bytes");
        var store = new LocalPublicationStore(fixture.Registry, new PublishedOutputRegistry(), new TestDiagnostics());

        foreach (var directory in new[] { "relative", Path.Combine(fixture.Root, "missing") })
        {
            var result = await store.PublishAsync(artifact, new OutputOptions(directory, "name", OutputContainer.UnifiMp4), CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
            Assert.True(fixture.SourceExists);
        }

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(fixture.Root, "destination-link");
            Directory.CreateSymbolicLink(link, fixture.Destination);
            var result = await store.PublishAsync(artifact, new OutputOptions(link, "name", OutputContainer.UnifiMp4), CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
            Assert.True(fixture.SourceExists);
        }
    }

    [Fact]
    public async Task Registry_rejects_forged_mismatched_unverified_empty_and_traversal_artifacts()
    {
        using var fixture = Fixture.Create();
        var path = fixture.WriteSource("real.mp4", "source bytes");
        var valid = new StagedArtifact("stage-authentic", "real.mp4", OutputContainer.Mp4, 12, true);
        Assert.True(fixture.Registry.Register(valid, path));
        Assert.False(fixture.Registry.TryResolve(valid with { LengthBytes = 99 }, out _));
        Assert.False(fixture.Registry.TryResolve(valid with { FileName = "other.mp4" }, out _));
        Assert.False(fixture.Registry.Register(new StagedArtifact("stage-unverified", "x.mp4", OutputContainer.Mp4, 1, false), path));
        Assert.False(fixture.Registry.Register(new StagedArtifact("stage-empty", "x.mp4", OutputContainer.Mp4, 0, true), path));
        Assert.False(fixture.Registry.Register(new StagedArtifact("stage-path", "../x.mp4", OutputContainer.Mp4, 12, true), path));
        Assert.False(fixture.Registry.Register(new StagedArtifact("stage-path", "x.mp4", OutputContainer.Mp4, 12, true), Path.Combine(fixture.Root, "outside.mp4")));
    }

    [Fact]
    public async Task Registered_staged_root_replacement_fails_closed_without_publishing_or_consuming_outside_source()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = Fixture.Create();
        var artifact = fixture.Register("video.mp4", OutputContainer.Mp4, "source bytes");
        var outsideRoot = Path.Combine(fixture.Root, "outside-stage");
        Directory.CreateDirectory(outsideRoot);
        var outsideSource = Path.Combine(outsideRoot, "video.mp4");
        File.WriteAllText(outsideSource, "swapped bytes");
        var originalStage = fixture.Stage + "-original";
        Directory.Move(fixture.Stage, originalStage);
        Directory.CreateSymbolicLink(fixture.Stage, outsideRoot);

        // The guard is deliberately before publication; the assertion after publication is independent.
        Assert.True(File.Exists(outsideSource));
        var outsideBefore = File.ReadAllText(outsideSource);
        var result = await new LocalPublicationStore(fixture.Registry, new PublishedOutputRegistry(), new TestDiagnostics())
            .PublishAsync(artifact, new OutputOptions(fixture.Destination, "replaced-root", OutputContainer.UnifiMp4), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.InvalidRequest, result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Destination, "replaced-root.mp4")));
        Assert.True(File.Exists(outsideSource));
        Assert.Equal(outsideBefore, File.ReadAllText(outsideSource));
        Assert.True(File.Exists(Path.Combine(originalStage, "video.mp4")));
    }

    [Fact]
    public void Registered_published_parent_replacement_fails_closed_without_returning_outside_path()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "published");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "published.mp4");
        File.WriteAllText(path, "published bytes");
        var output = new VerifiedLocalMp4("published.mp4", "output-" + Guid.NewGuid().ToString("N"), new FileInfo(path).Length);
        var registry = new PublishedOutputRegistry();
        Assert.True(registry.Register(output, path));

        var outsideParent = Path.Combine(fixture.Root, "outside-published");
        Directory.CreateDirectory(outsideParent);
        var outsidePath = Path.Combine(outsideParent, "published.mp4");
        File.WriteAllText(outsidePath, "outside bytes");
        var originalParent = parent + "-original";
        Directory.Move(parent, originalParent);
        Directory.CreateSymbolicLink(parent, outsideParent);

        // The outside sentinel must exist before lookup and remain unchanged afterward.
        Assert.True(File.Exists(outsidePath));
        var outsideBefore = File.ReadAllText(outsidePath);
        Assert.False(registry.TryResolve(output, out _));
        Assert.False(registry.Contains(output));
        Assert.True(File.Exists(outsidePath));
        Assert.Equal(outsideBefore, File.ReadAllText(outsidePath));
        Assert.True(File.Exists(Path.Combine(originalParent, "published.mp4")));
    }

    [Fact]
    public void Registered_published_intermediate_directory_replacement_fails_closed()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = Fixture.Create();
        var parent = Path.Combine(fixture.Root, "published", "nested");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "published.mp4");
        File.WriteAllText(path, "published bytes");
        var output = new VerifiedLocalMp4("published.mp4", "output-" + Guid.NewGuid().ToString("N"), new FileInfo(path).Length);
        var registry = new PublishedOutputRegistry();
        Assert.True(registry.Register(output, path));

        var outsideParent = Path.Combine(fixture.Root, "outside-published-nested");
        Directory.CreateDirectory(outsideParent);
        var outsidePath = Path.Combine(outsideParent, "nested", "published.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(outsidePath)!);
        File.WriteAllText(outsidePath, "outside bytes");
        var originalIntermediate = Path.Combine(fixture.Root, "published-original");
        Directory.Move(Path.Combine(fixture.Root, "published"), originalIntermediate);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "published"), outsideParent);

        Assert.True(File.Exists(outsidePath));
        var outsideBefore = File.ReadAllText(outsidePath);
        Assert.False(registry.TryResolve(output, out _));
        Assert.True(File.Exists(outsidePath));
        Assert.Equal(outsideBefore, File.ReadAllText(outsidePath));
        Assert.True(File.Exists(Path.Combine(originalIntermediate, "nested", "published.mp4")));
    }

    [Fact]
    public void Registered_nested_staged_directory_replacement_fails_closed_and_consume_is_conservative()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = Fixture.Create();
        var nested = Path.Combine(fixture.Stage, "nested");
        Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "video.mp4");
        File.WriteAllText(path, "nested bytes");
        var artifact = new StagedArtifact("nested-" + Guid.NewGuid().ToString("N"), "video.mp4", OutputContainer.Mp4, new FileInfo(path).Length, true);
        Assert.True(fixture.Registry.Register(artifact, path));

        var outsideRoot = Path.Combine(fixture.Root, "outside-nested");
        Directory.CreateDirectory(outsideRoot);
        var outsidePath = Path.Combine(outsideRoot, "video.mp4");
        File.WriteAllText(outsidePath, "outside nested");
        var originalNested = nested + "-original";
        Directory.Move(nested, originalNested);
        Directory.CreateSymbolicLink(nested, outsideRoot);

        Assert.True(File.Exists(outsidePath));
        var outsideBefore = File.ReadAllText(outsidePath);
        Assert.False(fixture.Registry.TryResolve(artifact, out _));
        Assert.False(fixture.Registry.Consume(artifact));
        Assert.True(File.Exists(outsidePath));
        Assert.Equal(outsideBefore, File.ReadAllText(outsidePath));
        Assert.True(File.Exists(Path.Combine(originalNested, "video.mp4")));
    }

    [Fact]
    public void Consume_treats_missing_file_in_intact_tree_as_complete_cleanup()
    {
        using var fixture = Fixture.Create();
        var artifact = fixture.Register("video.mp4", OutputContainer.Mp4, "source bytes");
        File.Delete(Path.Combine(fixture.Stage, "video.mp4"));

        Assert.True(fixture.Registry.Consume(artifact));
        Assert.False(fixture.Registry.Owns(artifact));
    }

    [Fact]
    public async Task Cancellation_removes_private_temporary_and_owned_source()
    {
        using var fixture = Fixture.Create();
        var artifact = fixture.Register("video.mp4", OutputContainer.Mp4, new string('x', 256 * 1024));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new LocalPublicationStore(fixture.Registry, new PublishedOutputRegistry(), new TestDiagnostics()).PublishAsync(
            artifact,
            new OutputOptions(fixture.Destination, "cancelled", OutputContainer.UnifiMp4),
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadErrorCode.Cancelled, result.Error!.Code);
        Assert.False(fixture.SourceExists);
        Assert.Empty(Directory.EnumerateFiles(fixture.Destination, ".unifi-publication-*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(fixture.Destination, "cancelled.mp4"));
    }

    [Fact]
    public async Task Post_commit_cleanup_warning_preserves_output_and_opaque_registration()
    {
        using var fixture = Fixture.Create(deleteFile: static _ => false);
        var artifact = fixture.Register("video.mp4", OutputContainer.Mp4, "source bytes");
        var diagnostics = new TestDiagnostics();
        var registry = new PublishedOutputRegistry();
        var result = await new LocalPublicationStore(fixture.Registry, registry, diagnostics).PublishAsync(
            artifact,
            new OutputOptions(fixture.Destination, "warning", OutputContainer.UnifiMp4),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        var output = result.Value!;
        Assert.True(File.Exists(Path.Combine(fixture.Destination, output.FileName)));
        Assert.True(fixture.SourceExists);
        Assert.False(fixture.Registry.Owns(artifact));
        Assert.True(registry.TryResolve(output, out _));
        var warning = Assert.Single(diagnostics.Errors);
        Assert.Equal(DownloadStage.Publishing, warning.Stage);
        Assert.Equal("diag-publication-cleanup-incomplete", warning.Diagnostic.Value);
        Assert.DoesNotContain(fixture.Root, warning.UserMessage, StringComparison.Ordinal);
    }

    private sealed class TestDiagnostics : IDiagnostics
    {
        public List<SafeDownloadError> Errors { get; } = [];
        public void Report(SafeDownloadError downloadError) => Errors.Add(downloadError);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, Func<string, bool>? deleteFile)
        {
            Root = root;
            Destination = Path.Combine(root, "destination");
            Stage = Path.Combine(root, "stage");
            Directory.CreateDirectory(Destination);
            Directory.CreateDirectory(Stage);
            Registry = new StagedArtifactRegistry(Stage, deleteFile);
        }

        public string Root { get; }
        public string Destination { get; }
        public string Stage { get; }
        public StagedArtifactRegistry Registry { get; }
        public bool SourceExists => Directory.EnumerateFiles(Stage).Any();

        public static Fixture Create(Func<string, bool>? deleteFile = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "unifi-publication-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root, deleteFile);
        }

        public StagedArtifact Register(string fileName, OutputContainer container, string contents)
        {
            var path = WriteSource(fileName, contents);
            var artifact = new StagedArtifact("stage-" + Guid.NewGuid().ToString("N"), fileName, container, new FileInfo(path).Length, true);
            Assert.True(Registry.Register(artifact, path));
            return artifact;
        }

        public string WriteSource(string fileName, string contents)
        {
            var path = Path.Combine(Stage, fileName);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
