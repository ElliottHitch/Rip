using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Infrastructure;

public enum ToolKey
{
    YtDlp,
    Deno,
    Ffmpeg,
    Ffprobe
}

public sealed record ToolConfiguration(
    ToolKey Key,
    string AssetName,
    string SourceRepository,
    string Version,
    string TargetRid,
    string ExpectedSha256,
    bool IsVerified,
    string ExecutablePath,
    string? ManifestSha256 = null,
    string? ApiSha256 = null)
{
    public override string ToString() => "[tool-configuration]";
}

public enum ToolValidationFailure
{
    MissingPath,
    NotAFile,
    NotExecutable,
    WrongTool,
    WrongAsset,
    WrongRepository,
    WrongVersion,
    WrongTargetRid,
    UnsupportedTargetRid,
    MalformedHash,
    Unverified,
    HashMismatch,
    ConflictingDigest,
    ManifestDigestMismatch,
    ApiDigestMismatch,
    MissingTrustedExpectation
}

public sealed record ToolValidationResult(
    bool IsValid,
    IReadOnlyList<ToolValidationFailure> Failures)
{
    public static ToolValidationResult Valid { get; } = new(true, Array.Empty<ToolValidationFailure>());
}

public sealed record ToolExpectation(
    ToolKey Key,
    string AssetName,
    string SourceRepository,
    string Version,
    string TargetRid,
    string? ExpectedSha256 = null,
    bool RequireVerified = true)
{
    public override string ToString() => "[tool-expectation]";
}

public static class ToolManifestValidator
{
    private static readonly Regex Sha256Pattern = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);

    public static ToolValidationResult Validate(
        ToolConfiguration configuration,
        ToolExpectation? expectation = null,
        IReadOnlySet<string>? allowedRepositories = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var failures = new List<ToolValidationFailure>();

        if (string.IsNullOrWhiteSpace(configuration.ExecutablePath) || !Path.IsPathFullyQualified(configuration.ExecutablePath))
        {
            failures.Add(ToolValidationFailure.MissingPath);
        }
        else if (!File.Exists(configuration.ExecutablePath))
        {
            failures.Add(ToolValidationFailure.NotAFile);
        }
        else if (!IsExecutable(configuration.ExecutablePath))
        {
            failures.Add(ToolValidationFailure.NotExecutable);
        }

        if (configuration.ExpectedSha256 is null || !Sha256Pattern.IsMatch(configuration.ExpectedSha256))
        {
            failures.Add(ToolValidationFailure.MalformedHash);
        }

        if (!configuration.IsVerified)
        {
            failures.Add(ToolValidationFailure.Unverified);
        }

        if (!Enum.IsDefined(configuration.Key)) failures.Add(ToolValidationFailure.WrongTool);
        if (string.IsNullOrWhiteSpace(configuration.AssetName)) failures.Add(ToolValidationFailure.WrongAsset);
        if (string.IsNullOrWhiteSpace(configuration.SourceRepository)) failures.Add(ToolValidationFailure.WrongRepository);
        if (string.IsNullOrWhiteSpace(configuration.Version)) failures.Add(ToolValidationFailure.WrongVersion);
        if (string.IsNullOrWhiteSpace(configuration.TargetRid)) failures.Add(ToolValidationFailure.WrongTargetRid);

        if (expectation is not null)
        {
            if (configuration.Key != expectation.Key) failures.Add(ToolValidationFailure.WrongTool);
            if (!string.Equals(configuration.AssetName, expectation.AssetName, StringComparison.Ordinal)) failures.Add(ToolValidationFailure.WrongAsset);
            if (!string.Equals(configuration.SourceRepository, expectation.SourceRepository, StringComparison.Ordinal)) failures.Add(ToolValidationFailure.WrongRepository);
            if (!string.Equals(configuration.Version, expectation.Version, StringComparison.Ordinal)) failures.Add(ToolValidationFailure.WrongVersion);
            if (!string.Equals(configuration.TargetRid, expectation.TargetRid, StringComparison.Ordinal)) failures.Add(ToolValidationFailure.WrongTargetRid);
            if (string.IsNullOrWhiteSpace(expectation.ExpectedSha256) || !Sha256Pattern.IsMatch(expectation.ExpectedSha256))
            {
                failures.Add(ToolValidationFailure.MissingTrustedExpectation);
            }
            else if (!string.Equals(configuration.ExpectedSha256, expectation.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(ToolValidationFailure.HashMismatch);
            }

            if (!expectation.RequireVerified)
            {
                failures.Add(ToolValidationFailure.MissingTrustedExpectation);
            }

            if (!configuration.IsVerified)
            {
                failures.Add(ToolValidationFailure.Unverified);
            }
        }

        var repositories = allowedRepositories ?? new HashSet<string>(StringComparer.Ordinal)
        {
            OfficialToolFixtures.YtDlpRepository
        };
        if (!repositories.Contains(configuration.SourceRepository))
        {
            failures.Add(ToolValidationFailure.WrongRepository);
        }

        var integrityHash = expectation?.ExpectedSha256 ?? configuration.ExpectedSha256;
        ValidateDigest(configuration.ManifestSha256, integrityHash, ToolValidationFailure.ManifestDigestMismatch, failures);
        ValidateDigest(configuration.ApiSha256, integrityHash, ToolValidationFailure.ApiDigestMismatch, failures);
        if (configuration.ManifestSha256 is not null && configuration.ApiSha256 is not null &&
            !string.Equals(configuration.ManifestSha256, configuration.ApiSha256, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(ToolValidationFailure.ConflictingDigest);
        }

        if (integrityHash is not null && Sha256Pattern.IsMatch(integrityHash) &&
            Path.IsPathFullyQualified(configuration.ExecutablePath) &&
            File.Exists(configuration.ExecutablePath))
        {
            try
            {
                using var executable = File.OpenRead(configuration.ExecutablePath);
                var actualHash = Convert.ToHexString(SHA256.HashData(executable));
                if (!string.Equals(actualHash, integrityHash, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(ToolValidationFailure.HashMismatch);
                }
            }
            catch (IOException)
            {
                failures.Add(ToolValidationFailure.NotAFile);
            }
            catch (UnauthorizedAccessException)
            {
                failures.Add(ToolValidationFailure.NotAFile);
            }
        }

        return failures.Count == 0 ? ToolValidationResult.Valid : new(false, failures);
    }

    private static void ValidateDigest(
        string? digest,
        string? expected,
        ToolValidationFailure failure,
        List<ToolValidationFailure> failures)
    {
        if (digest is null) return;
        if (!Sha256Pattern.IsMatch(digest) || !string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(failure);
        }
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public static class OfficialToolFixtures
{
    public const string YtDlpRepository = "https://github.com/yt-dlp/yt-dlp";
    public const string YtDlpVersion = "2026.08.19";
    public const string WindowsX64YtDlpAsset = "yt-dlp.exe";
    public const string WindowsX64YtDlpSha256 = "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a";
    public const string LinuxX64YtDlpAsset = "yt-dlp_linux";
    public const string LinuxX64YtDlpSha256 = "58162f9bfdc27458ea47bfcb311cf47028f17d8154a8bf7d689861d46399230a";
}

internal static class SafeInfrastructureErrors
{
    public static SafeDownloadError MissingTool(ToolKey key) => SafeDownloadError.Create(
        DownloadErrorCode.MissingTool,
        DownloadStage.Validating,
        $"The configured {key} tool is unavailable or failed verification.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-tool-unavailable"));

    public static SafeDownloadError ProviderUnavailable() => SafeDownloadError.Create(
        DownloadErrorCode.ProviderUnavailable,
        DownloadStage.Processing,
        "The local tool reported an unsuccessful operation.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-process-failed"));

    public static SafeDownloadError ProcessExitedNonzero() => SafeDownloadError.Create(
        DownloadErrorCode.ProviderUnavailable,
        DownloadStage.Processing,
        "The local tool reported an unsuccessful operation.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-process-failed"));

    public static SafeDownloadError StructuredProcessFailure(string code) => code switch
    {
        "access-denied" => SafeDownloadError.Create(
            DownloadErrorCode.AccessDenied,
            DownloadStage.Downloading,
            "The media stream was not available.",
            RetryAction.RefreshStream,
            new RedactedDiagnosticToken("diag-process-access-denied")),
        "rate-limited" => SafeDownloadError.Create(
            DownloadErrorCode.RateLimited,
            DownloadStage.Downloading,
            "The provider is temporarily rate limiting this request.",
            RetryAction.UserActionRequired,
            new RedactedDiagnosticToken("diag-process-rate-limited")),
        "media-processing-failed" => SafeDownloadError.Create(
            DownloadErrorCode.MediaProcessingFailed,
            DownloadStage.Processing,
            "The local media processing operation failed.",
            RetryAction.UserActionRequired,
            new RedactedDiagnosticToken("diag-process-media-failed")),
        _ => ProviderUnavailable()
    };

    public static SafeDownloadError Cancelled(bool cleanupCertain) => SafeDownloadError.Create(
        DownloadErrorCode.Cancelled,
        DownloadStage.Processing,
        cleanupCertain
            ? "The operation was cancelled and direct-process cleanup completed."
            : "The operation was cancelled; process-tree cleanup status is uncertain.",
        RetryAction.None,
        new RedactedDiagnosticToken(cleanupCertain ? "diag-process-cancelled" : "diag-process-cancelled-uncertain"));

    public static SafeDownloadError TimedOut(bool cleanupCertain) => SafeDownloadError.Create(
        DownloadErrorCode.Unknown,
        DownloadStage.Processing,
        cleanupCertain
            ? "The local tool did not finish within the allowed time; direct-process cleanup completed."
            : "The local tool did not finish within the allowed time; process-tree cleanup status is uncertain.",
        RetryAction.RetryAfterDelay,
        new RedactedDiagnosticToken(cleanupCertain ? "diag-process-timeout" : "diag-process-timeout-uncertain"));

    public static SafeDownloadError InvalidProviderResponse(DownloadStage stage) => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        stage,
        "The local tool returned an invalid media description.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-provider-response-invalid"));

    public static SafeDownloadError UnsupportedProviderFormat(DownloadStage stage) => SafeDownloadError.Create(
        DownloadErrorCode.UnsupportedFormat,
        stage,
        "The local tool returned no supported media format.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-provider-format-unsupported"));

    public static SafeDownloadError UnsupportedProviderOperation() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Resolving,
        "The requested media operation is not supported.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-provider-operation-unsupported"));

    public static SafeDownloadError InvalidMediaProcessingRequest() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Processing,
        "The local media processing request is invalid or unavailable.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-media-processing-request-invalid"));

    public static SafeDownloadError UnsupportedMediaProcessingFormat() => SafeDownloadError.Create(
        DownloadErrorCode.UnsupportedFormat,
        DownloadStage.Processing,
        "The requested media format cannot be processed by the local adapter.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-media-processing-format-unsupported"));

    public static SafeDownloadError OutputVerificationFailed() => SafeDownloadError.Create(
        DownloadErrorCode.MediaProcessingFailed,
        DownloadStage.Processing,
        "The local media processing operation did not produce a verified output.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-media-processing-output-invalid"));

    public static SafeDownloadError InvalidLocalStreamRequest() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Downloading,
        "The local media staging request is invalid or unavailable.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-local-stream-request-invalid"));

    public static SafeDownloadError LocalStreamUnavailable() => SafeDownloadError.Create(
        DownloadErrorCode.ProviderUnavailable,
        DownloadStage.Downloading,
        "The media stream was not available.",
        RetryAction.RefreshStream,
        new RedactedDiagnosticToken("diag-local-stream-unavailable"));

    public static SafeDownloadError LocalStreamTooLarge() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Downloading,
        "The media stream exceeds the configured staging limit.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-local-stream-too-large"));

    public static SafeDownloadError LocalStreamEmpty() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Downloading,
        "The media stream was empty or could not be verified.",
        RetryAction.RefreshStream,
        new RedactedDiagnosticToken("diag-local-stream-empty"));

    public static SafeDownloadError LocalStreamCancelled(bool cleanupCertain) => SafeDownloadError.Create(
        DownloadErrorCode.Cancelled,
        DownloadStage.Downloading,
        cleanupCertain
            ? "The media staging operation was cancelled and cleanup completed."
            : "The media staging operation was cancelled; cleanup status is uncertain.",
        RetryAction.None,
        new RedactedDiagnosticToken(cleanupCertain
            ? "diag-local-stream-cancelled"
            : "diag-local-stream-cancelled-uncertain"));

    public static SafeDownloadError StageReleaseFailed() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Downloading,
        "The staged media cleanup operation could not be completed.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-local-stream-cleanup-failed"));

    public static SafeDownloadError InvalidPublicationRequest() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Publishing,
        "The local publication request is invalid or unavailable.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-publication-request-invalid"));

    public static SafeDownloadError PublicationConflict() => SafeDownloadError.Create(
        DownloadErrorCode.PublicationConflict,
        DownloadStage.Publishing,
        "A file with that name already exists. Nothing was overwritten.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-publication-conflict"));

    public static SafeDownloadError PublicationCancelled() => SafeDownloadError.Create(
        DownloadErrorCode.Cancelled,
        DownloadStage.Publishing,
        "The publication operation was cancelled. No published output was reported.",
        RetryAction.None,
        new RedactedDiagnosticToken("diag-publication-cancelled"));

    public static SafeDownloadError PublicationCleanupIncomplete() => SafeDownloadError.Create(
        DownloadErrorCode.Unknown,
        DownloadStage.Publishing,
        "Published output was preserved, but staged media cleanup did not complete.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-publication-cleanup-incomplete"));

    public static SafeDownloadError LocalFileOpenFailed() => SafeDownloadError.Create(
        DownloadErrorCode.Unknown,
        DownloadStage.Opening,
        "The published local file could not be opened.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-local-file-open-failed"));

    public static SafeDownloadError LocalFileOpenCancelled() => SafeDownloadError.Create(
        DownloadErrorCode.Cancelled,
        DownloadStage.Opening,
        "Opening the published local file was cancelled.",
        RetryAction.None,
        new RedactedDiagnosticToken("diag-local-file-open-cancelled"));
}
