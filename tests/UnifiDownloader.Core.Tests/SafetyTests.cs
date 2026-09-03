using UnifiDownloader.Application;
using UnifiDownloader.Domain;

namespace UnifiDownloader.Core.Tests;

public sealed class SafetyTests
{
    private static readonly string[] ProcessArguments = ["https://example.test/stream?signature=sentinel"];

    [Fact]
    public void Safe_errors_redact_urls_paths_and_sensitive_assignments()
    {
        var error = SafeDownloadError.Create(
            DownloadErrorCode.ProviderUnavailable,
            DownloadStage.Metadata,
            "failed at https://example.test/watch?v=sentinel signature=SIGNATURE_SENTINEL cookie=COOKIE_SENTINEL token=TOKEN_SENTINEL password=PASSWORD_SENTINEL Authorization: Bearer AUTHORIZATION_SENTINEL Proxy-Authorization: Bearer PROXY_AUTHORIZATION_SENTINEL path=/secret/profile destination=/home/sentinel/profile temp=/tmp/sentinel home=/Users/sentinel Windows=C:\\Users\\sentinel\\profile UNC=\\\\server\\share\\sentinel",
            RetryAction.None,
            new RedactedDiagnosticToken("diag-safe"));

        Assert.DoesNotContain("https://", error.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COOKIE_SENTINEL", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SIGNATURE_SENTINEL", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN_SENTINEL", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSWORD_SENTINEL", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTHORIZATION_SENTINEL", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("PROXY_AUTHORIZATION_SENTINEL", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/secret/profile", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/sentinel", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/sentinel", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/sentinel", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\sentinel", error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\server\\share\\sentinel", error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("[redacted-url]", error.UserMessage, StringComparison.Ordinal);
        Assert.Equal("diag-safe", error.Diagnostic.Value);
    }

    [Fact]
    public void Diagnostic_tokens_reject_paths_and_raw_sensitive_shapes()
    {
        Assert.Throws<ArgumentException>(() => new RedactedDiagnosticToken("/tmp/sentinel"));
        Assert.Throws<ArgumentException>(() => new RedactedDiagnosticToken("cookie=COOKIE_SENTINEL"));
    }

    [Fact]
    public void Opaque_media_source_does_not_display_its_address()
    {
        var source = new MediaSource(new Uri("https://example.test/stream?signature=sentinel"));

        Assert.Equal("[media-source]", source.ToString());
    }

    [Fact]
    public void Progress_activity_and_process_diagnostics_are_redacted_at_the_boundary()
    {
        var progress = new DownloadProgress(
            new RunIdentity(Guid.Parse("33333333-3333-3333-3333-333333333333"), 0),
            DownloadStage.Downloading,
            1,
            .5,
            "request https://example.test/watch?v=sentinel cookie=COOKIE_SENTINEL");
        var process = new ProcessResult(1, false, "child stderr token=TOKEN_SENTINEL");

        Assert.DoesNotContain("https://", progress.Activity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COOKIE_SENTINEL", progress.Activity, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN_SENTINEL", process.SafeDiagnosticMessage, StringComparison.Ordinal);
        Assert.Equal("[process-spec]", new ProcessSpec("yt-dlp", ProcessArguments, TimeSpan.FromMinutes(1)).ToString());
    }

    [Theory]
    [InlineData("Authorization: Bearer AUTHORIZATION_HEADER_SENTINEL", "AUTHORIZATION_HEADER_SENTINEL")]
    [InlineData("Authorization: Bearer [Basic AUTHORIZATION_BRACKET_SENTINEL]", "AUTHORIZATION_BRACKET_SENTINEL")]
    [InlineData("authorization=AUTHORIZATION_ASSIGNMENT_SENTINEL", "AUTHORIZATION_ASSIGNMENT_SENTINEL")]
    [InlineData("aUtHoRiZaTiOn: Basic AUTHORIZATION_CASE_SENTINEL", "AUTHORIZATION_CASE_SENTINEL")]
    [InlineData("Authorization:Bearer AUTHORIZATION_NOSPACE_SENTINEL", "AUTHORIZATION_NOSPACE_SENTINEL")]
    [InlineData("Authorization : Bearer AUTHORIZATION_SPACED_SENTINEL", "AUTHORIZATION_SPACED_SENTINEL")]
    [InlineData("AUTHORIZATION = Bearer AUTHORIZATION_SCHEME_SENTINEL", "AUTHORIZATION_SCHEME_SENTINEL")]
    [InlineData("Proxy-Authorization: Bearer PROXY_HEADER_SENTINEL", "PROXY_HEADER_SENTINEL")]
    [InlineData("pRoXy-AuThOrIzAtIoN=PROXY_ASSIGNMENT_SENTINEL", "PROXY_ASSIGNMENT_SENTINEL")]
    [InlineData("Authorization: Bearer \"AUTHORIZATION_QUOTED_SENTINEL\"", "AUTHORIZATION_QUOTED_SENTINEL")]
    [InlineData("Authorization: \"Bearer AUTHORIZATION_QUOTED_WHOLE_SENTINEL\"", "AUTHORIZATION_QUOTED_WHOLE_SENTINEL")]
    [InlineData("Authorization: 'Bearer AUTHORIZATION_SINGLE_QUOTED_SENTINEL'", "AUTHORIZATION_SINGLE_QUOTED_SENTINEL")]
    [InlineData("Proxy-Authorization: [Basic PROXY_BRACKET_SENTINEL]", "PROXY_BRACKET_SENTINEL")]
    [InlineData("{\"Authorization\": \"Bearer AUTH_JSON_BOUNDARY_SENTINEL\"}", "AUTH_JSON_BOUNDARY_SENTINEL")]
    [InlineData("{\"authorization\":\"AUTH_JSON_LOWER_SENTINEL\"}", "AUTH_JSON_LOWER_SENTINEL")]
    [InlineData("{\"AUTHORIZATION\" : \"Basic AUTH_JSON_UPPER_SENTINEL\"}", "AUTH_JSON_UPPER_SENTINEL")]
    [InlineData("{\"Proxy-Authorization\": \"Bearer PROXY_JSON_BOUNDARY_SENTINEL\"}", "PROXY_JSON_BOUNDARY_SENTINEL")]
    [InlineData("{\"pRoXy-AuThOrIzAtIoN\":'PROXY_JSON_MIXED_SENTINEL'}", "PROXY_JSON_MIXED_SENTINEL")]
    [InlineData("{\"Authorization\":\"Bearer \\\"quoted\\\" AUTH_JSON_ESCAPED_SENTINEL\"}", "AUTH_JSON_ESCAPED_SENTINEL")]
    public void Authorization_values_are_redacted_at_every_safe_boundary(string message, string sentinel)
    {
        Assert.Contains(sentinel, message, StringComparison.Ordinal);

        var error = SafeDownloadError.Create(
            DownloadErrorCode.AccessDenied,
            DownloadStage.Metadata,
            message,
            RetryAction.None,
            new RedactedDiagnosticToken("diag-auth"));
        var progress = new DownloadProgress(
            new RunIdentity(Guid.Parse("44444444-4444-4444-4444-444444444444"), 0),
            DownloadStage.Metadata,
            1,
            .5,
            message);
        var process = new ProcessResult(1, false, message);

        Assert.DoesNotContain(sentinel, error.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, progress.Activity, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, process.SafeDiagnosticMessage, StringComparison.Ordinal);
        Assert.Contains("[redacted-sensitive-value]", error.UserMessage, StringComparison.Ordinal);
        Assert.Contains("[redacted-sensitive-value]", progress.Activity, StringComparison.Ordinal);
        Assert.Contains("[redacted-sensitive-value]", process.SafeDiagnosticMessage, StringComparison.Ordinal);
    }
}
