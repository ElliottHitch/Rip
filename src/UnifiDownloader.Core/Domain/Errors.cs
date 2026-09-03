using System.Text.RegularExpressions;

namespace UnifiDownloader.Domain;

public enum DownloadErrorCode
{
    InvalidRequest,
    UnsupportedFormat,
    ProviderUnavailable,
    AccessDenied,
    RateLimited,
    MissingTool,
    MediaProcessingFailed,
    PublicationConflict,
    Cancelled,
    Unknown
}

public enum DownloadStage
{
    Validating,
    Metadata,
    Resolving,
    Downloading,
    Processing,
    Publishing,
    Opening
}

public enum RetryAction
{
    None,
    RefreshStream,
    RetryAfterDelay,
    UserActionRequired
}

public readonly record struct RedactedDiagnosticToken
{
    private static readonly Regex SafePattern = new("^diag-[a-z0-9-]{1,48}$", RegexOptions.CultureInvariant);

    public RedactedDiagnosticToken(string value)
    {
        if (!SafePattern.IsMatch(value))
        {
            throw new ArgumentException("Diagnostic tokens must be opaque diag-* identifiers.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record SafeDownloadError
{
    private SafeDownloadError(
        DownloadErrorCode code,
        DownloadStage stage,
        string userMessage,
        RetryAction retry,
        RedactedDiagnosticToken diagnostic)
    {
        Code = code;
        Stage = stage;
        UserMessage = userMessage;
        Retry = retry;
        Diagnostic = diagnostic;
    }

    public DownloadErrorCode Code { get; }
    public DownloadStage Stage { get; }
    public string UserMessage { get; }
    public RetryAction Retry { get; }
    public RedactedDiagnosticToken Diagnostic { get; }

    public static SafeDownloadError Create(
        DownloadErrorCode code,
        DownloadStage stage,
        string userMessage,
        RetryAction retry,
        RedactedDiagnosticToken diagnostic)
        => new(code, stage, ErrorRedactor.Redact(userMessage), retry, diagnostic);
}

public static partial class ErrorRedactor
{
    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();

    [GeneratedRegex(@"(?i)(?:cookie|token|signature|password|secret)\s*[=:]\s*[^\s,;]+")]
    private static partial Regex SensitiveAssignment();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])['""]?(?:authorization|proxy-authorization)['""]?\s*[=:]\s*(?:[^\s,;""'\[]+\s+)?(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|\[[^\]]*\]|[^\s,;}]+)")]
    private static partial Regex AuthorizationAssignment();

    [GeneratedRegex(@"(?i)(?<![A-Za-z0-9])(?:[A-Z]:[\\/]|\\\\)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![A-Za-z0-9])/(?!/)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPath();

    public static string Redact(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = Url().Replace(message, "[redacted-url]");
        safe = AuthorizationAssignment().Replace(safe, "[redacted-sensitive-value]");
        safe = SensitiveAssignment().Replace(safe, "[redacted-sensitive-value]");
        safe = WindowsPath().Replace(safe, "[redacted-path]");
        return UnixPath().Replace(safe, "[redacted-path]");
    }
}

/// <summary>Fixed, bounded errors used when the application boundary catches an unexpected condition.</summary>
public static class SafeDownloadErrors
{
    public static SafeDownloadError InvalidRequest() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Validating,
        "The download request is invalid.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-application-request-invalid"));

    public static SafeDownloadError Cancelled(DownloadStage stage) => SafeDownloadError.Create(
        DownloadErrorCode.Cancelled,
        stage,
        "The download operation was cancelled.",
        RetryAction.None,
        new RedactedDiagnosticToken("diag-application-cancelled"));

    public static SafeDownloadError Unexpected(DownloadStage stage) => SafeDownloadError.Create(
        DownloadErrorCode.Unknown,
        stage,
        "The download operation failed unexpectedly.",
        RetryAction.None,
        new RedactedDiagnosticToken("diag-application-unexpected"));

    public static SafeDownloadError InvalidMetadata() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Metadata,
        "The provider returned invalid metadata.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-application-metadata-invalid"));

    public static SafeDownloadError InvalidProcessingResult() => SafeDownloadError.Create(
        DownloadErrorCode.MediaProcessingFailed,
        DownloadStage.Processing,
        "The local media processing operation did not produce a verified output.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-application-processing-invalid"));

    public static SafeDownloadError InvalidStagedInputs() => SafeDownloadError.Create(
        DownloadErrorCode.InvalidRequest,
        DownloadStage.Downloading,
        "The staged media inputs are invalid.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-application-staged-inputs-invalid"));

    public static SafeDownloadError InvalidPublicationResult() => SafeDownloadError.Create(
        DownloadErrorCode.PublicationConflict,
        DownloadStage.Publishing,
        "Publication did not produce a verified local file.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-application-publication-invalid"));

    public static SafeDownloadError CleanupIncomplete() => SafeDownloadError.Create(
        DownloadErrorCode.Unknown,
        DownloadStage.Downloading,
        "Staged media cleanup did not complete.",
        RetryAction.UserActionRequired,
        new RedactedDiagnosticToken("diag-application-cleanup-incomplete"));

    public static SafeDownloadError ObserverFailed() => SafeDownloadError.Create(
        DownloadErrorCode.Unknown,
        DownloadStage.Validating,
        "Download progress reporting failed.",
        RetryAction.None,
        new RedactedDiagnosticToken("diag-application-observer-failed"));
}
