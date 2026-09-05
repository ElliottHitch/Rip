using System.ComponentModel;
using Rip.Application;
using Rip.Domain;
using Rip.Infrastructure;

namespace Rip.Infrastructure.Tests;

public sealed class LocalFileOpenerTests
{
    [Fact]
    public async Task Opens_freshly_reverified_registered_file_as_encoded_local_uri()
    {
        using var fixture = Fixture.Create("clip space%ユニコード.mp4", "verified local bytes");
        var launcher = new FakeLauncher();
        var opener = new SystemLocalFileOpener(fixture.Registry, launcher);

        var result = await opener.OpenAsync(fixture.Output, CancellationToken.None);

        Assert.True(result.Opened);
        Assert.Null(result.Error);
        var uri = Assert.Single(launcher.Uris);
        Assert.True(uri.IsFile);
        Assert.Equal(Uri.UriSchemeFile, uri.Scheme);
        Assert.Equal(new Uri(fixture.FilePath, UriKind.Absolute).AbsoluteUri, uri.AbsoluteUri);
        Assert.Contains("%20", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("%25", uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("%E3%83%A6", uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Before, File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public async Task System_launcher_rejects_remote_uri_without_starting_a_process()
    {
        var result = await new SystemLocalFileLauncher().LaunchAsync(
            new Uri("https://example.invalid/video.mp4"),
            CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task Rejects_null_forged_and_unregistered_outputs_without_launching()
    {
        using var fixture = Fixture.Create("clip.mp4", "verified local bytes");
        var launcher = new FakeLauncher();
        var opener = new SystemLocalFileOpener(fixture.Registry, launcher);

        var nullResult = await opener.OpenAsync(null!, CancellationToken.None);
        var forged = new VerifiedLocalMp4(
            fixture.Output.FileName,
            "output-forged",
            fixture.Output.LengthBytes);
        var forgedResult = await opener.OpenAsync(forged, CancellationToken.None);
        var unregistered = new VerifiedLocalMp4(
            fixture.Output.FileName,
            "output-unregistered",
            fixture.Output.LengthBytes);
        var unregisteredResult = await opener.OpenAsync(unregistered, CancellationToken.None);

        AssertOpeningFailure(nullResult);
        AssertOpeningFailure(forgedResult);
        AssertOpeningFailure(unregisteredResult);
        Assert.Empty(launcher.Uris);
        Assert.Equal(fixture.Before, File.ReadAllText(fixture.FilePath));
    }

    [Fact]
    public async Task Rejects_deleted_moved_replaced_zero_length_and_non_regular_outputs_without_launching()
    {
        using var fixture = Fixture.Create("clip.mp4", "verified local bytes");
        var launcher = new FakeLauncher();
        var opener = new SystemLocalFileOpener(fixture.Registry, launcher);

        File.Delete(fixture.FilePath);
        var deletedResult = await opener.OpenAsync(fixture.Output, CancellationToken.None);
        AssertOpeningFailure(deletedResult);
        Assert.Empty(launcher.Uris);

        using var movedFixture = Fixture.Create("moved.mp4", "verified local bytes");
        var movedLauncher = new FakeLauncher();
        var movedOpener = new SystemLocalFileOpener(movedFixture.Registry, movedLauncher);
        var movedPath = movedFixture.FilePath + ".moved";
        File.Move(movedFixture.FilePath, movedPath);
        var movedResult = await movedOpener.OpenAsync(movedFixture.Output, CancellationToken.None);
        AssertOpeningFailure(movedResult);
        Assert.Empty(movedLauncher.Uris);
        File.Move(movedPath, movedFixture.FilePath);

        using var replacedFixture = Fixture.Create("replaced.mp4", "verified local bytes");
        var replacedLauncher = new FakeLauncher();
        var replacedOpener = new SystemLocalFileOpener(replacedFixture.Registry, replacedLauncher);
        File.Delete(replacedFixture.FilePath);
        File.WriteAllText(replacedFixture.FilePath, "different length");
        var replacedResult = await replacedOpener.OpenAsync(replacedFixture.Output, CancellationToken.None);
        AssertOpeningFailure(replacedResult);
        Assert.Empty(replacedLauncher.Uris);

        using var zeroLengthFixture = Fixture.Create("zero.mp4", "verified local bytes");
        var zeroLauncher = new FakeLauncher();
        var zeroOpener = new SystemLocalFileOpener(zeroLengthFixture.Registry, zeroLauncher);
        File.WriteAllText(zeroLengthFixture.FilePath, string.Empty);
        var zeroResult = await zeroOpener.OpenAsync(zeroLengthFixture.Output, CancellationToken.None);
        AssertOpeningFailure(zeroResult);
        Assert.Empty(zeroLauncher.Uris);

        using var directoryFixture = Fixture.Create("directory.mp4", "verified local bytes");
        var directoryLauncher = new FakeLauncher();
        var directoryOpener = new SystemLocalFileOpener(directoryFixture.Registry, directoryLauncher);
        File.Delete(directoryFixture.FilePath);
        Directory.CreateDirectory(directoryFixture.FilePath);
        var directoryResult = await directoryOpener.OpenAsync(directoryFixture.Output, CancellationToken.None);
        AssertOpeningFailure(directoryResult);
        Assert.Empty(directoryLauncher.Uris);

        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedLocalMp4("zero.mp4", "output-zero", 0));
    }

    [Fact]
    public async Task Cancellation_before_launch_is_safe_and_does_not_call_launcher()
    {
        using var fixture = Fixture.Create("cancel.mp4", "verified local bytes");
        var launcher = new FakeLauncher();
        var opener = new SystemLocalFileOpener(fixture.Registry, launcher);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await opener.OpenAsync(fixture.Output, cancellation.Token);

        Assert.False(result.Opened);
        Assert.NotNull(result.Error);
        Assert.Equal(DownloadErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(DownloadStage.Opening, result.Error.Stage);
        Assert.Equal("diag-local-file-open-cancelled", result.Error.Diagnostic.Value);
        Assert.Empty(launcher.Uris);
    }

    [Fact]
    public async Task Launcher_failure_and_expected_exception_are_safe_without_details()
    {
        using var fixture = Fixture.Create("failure.mp4", "verified local bytes");
        var failedLauncher = new FakeLauncher { ReturnValue = false };
        var failedResult = await new SystemLocalFileOpener(fixture.Registry, failedLauncher)
            .OpenAsync(fixture.Output, CancellationToken.None);
        AssertOpeningFailure(failedResult);
        Assert.Single(failedLauncher.Uris);

        var throwingLauncher = new FakeLauncher { Exception = new Win32Exception("secret child detail") };
        var throwingResult = await new SystemLocalFileOpener(fixture.Registry, throwingLauncher)
            .OpenAsync(fixture.Output, CancellationToken.None);
        AssertOpeningFailure(throwingResult);
        Assert.Single(throwingLauncher.Uris);
        Assert.DoesNotContain("secret", throwingResult.Error!.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("child", throwingResult.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Before, File.ReadAllText(fixture.FilePath));
    }

    private static void AssertOpeningFailure(OpenResult result)
    {
        Assert.False(result.Opened);
        Assert.NotNull(result.Error);
        Assert.Equal(DownloadStage.Opening, result.Error!.Stage);
        Assert.Equal(DownloadErrorCode.Unknown, result.Error.Code);
        Assert.Equal("diag-local-file-open-failed", result.Error.Diagnostic.Value);
        Assert.DoesNotContain("/", result.Error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", result.Error.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeLauncher : ILocalFileLauncher
    {
        public List<Uri> Uris { get; } = [];
        public bool ReturnValue { get; init; } = true;
        public Exception? Exception { get; init; }

        public ValueTask<bool> LaunchAsync(Uri localFileUri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uris.Add(localFileUri);
            if (Exception is not null) throw Exception;
            return ValueTask.FromResult(ReturnValue);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string fileName, string contents)
        {
            Root = root;
            FilePath = Path.Combine(root, fileName);
            Before = contents;
            File.WriteAllText(FilePath, contents);
            Output = new VerifiedLocalMp4(fileName, "output-" + Guid.NewGuid().ToString("N"), new FileInfo(FilePath).Length);
            Registry = new PublishedOutputRegistry();
            Assert.True(Registry.Register(Output, FilePath));
        }

        public string Root { get; }
        public string FilePath { get; }
        public string Before { get; }
        public VerifiedLocalMp4 Output { get; }
        public PublishedOutputRegistry Registry { get; }

        public static Fixture Create(string fileName, string contents)
        {
            var root = Path.Combine(Path.GetTempPath(), "unifi-local-opener-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root, fileName, contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
