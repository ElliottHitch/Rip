import os
import sys
import tempfile
import threading
import time
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import app


FORMATS = [
    {
        "format_id": "video-old",
        "vcodec": "avc1",
        "acodec": "none",
        "height": 1080,
        "fps": 30,
        "tbr": 4000,
        "filesize": 100,
    },
    {
        "format_id": "audio-old",
        "vcodec": "none",
        "acodec": "mp4a.40.2",
        "abr": 192,
        "asr": 48000,
        "filesize": 50,
    },
]


class FakeYtdlpError(Exception):
    pass


class FakeVar:
    def __init__(self, value=None):
        self.value = value

    def get(self):
        return self.value

    def set(self, value):
        self.value = value


class FakeControl:
    def __init__(self):
        self.configure_calls = []

    def configure(self, **kwargs):
        self.configure_calls.append(kwargs)

    def focus_set(self):
        pass


class FakeYoutubeDL:
    extracts = 0
    options_history = []
    downloads = []
    formats = FORMATS
    download_failures = []
    metadata_failures = []
    metadata_type = None
    metadata_error = "Unable to extract metadata"
    download_error_status = 403
    download_error_message = None

    def __init__(self, options):
        self.options = options
        type(self).options_history.append(dict(options))

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, traceback):
        return False

    def extract_info(self, url, download=False):
        type(self).extracts += 1
        if type(self).extracts in getattr(type(self), "metadata_failures", []):
            raise FakeYtdlpError(type(self).metadata_error)
        return {
            "title": "Example",
            "duration": 1,
            "formats": type(self).formats,
            **({"_type": type(self).metadata_type} if type(self).metadata_type else {}),
        }

    def download(self, urls):
        format_id = self.options["format"]
        label = "combined" if format_id == "18" else ("video" if format_id == "video-old" else "audio")
        type(self).downloads.append(label)
        path = self.options["outtmpl"].replace("%(ext)s", "mp4")
        with open(path, "wb") as output:
            output.write(label.encode("ascii"))
        if len(type(self).downloads) in type(self).download_failures:
            status = type(self).download_error_status
            message = type(self).download_error_message or (
                f"ERROR: HTTP Error {status}: Forbidden https://googlevideo.example/a?sig=secret"
            )
            raise FakeYtdlpError(message)
        for hook in self.options.get("progress_hooks", []):
            hook({"status": "finished", "filename": path})


class PipelineTestCase(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.worker_temp = tempfile.TemporaryDirectory()
        self.downloader = object.__new__(app.YouTubeDownloaderApp)
        self.downloader.cancel_requested = False
        self.downloader.active_process = None
        self.downloader.env_status = app.EnvironmentStatus(
            yt_dlp_available=True,
            yt_dlp_version="test",
            ffmpeg_path="ffmpeg",
        )
        self.downloader._set_stage = Mock()
        self.downloader._set_status = Mock()
        self.downloader._set_progress = Mock()
        self.downloader._append_log = Mock()
        self.downloader._wait_before_retry = Mock()
        FakeYoutubeDL.extracts = 0
        FakeYoutubeDL.options_history = []
        FakeYoutubeDL.downloads = []
        FakeYoutubeDL.formats = FORMATS
        FakeYoutubeDL.download_failures = []
        FakeYoutubeDL.metadata_failures = []
        FakeYoutubeDL.metadata_type = None
        FakeYoutubeDL.metadata_error = "Unable to extract metadata"
        FakeYoutubeDL.download_error_status = 403
        FakeYoutubeDL.download_error_message = None
        self.original_yt_dlp = app.yt_dlp
        app.yt_dlp = SimpleNamespace(YoutubeDL=FakeYoutubeDL)

    def tearDown(self):
        app.yt_dlp = self.original_yt_dlp
        self.temp.cleanup()
        self.worker_temp.cleanup()

    def _assert_no_sensitive_exception_chain(self, exception):
        self.assertIsNone(exception.__context__)
        self.assertIsNone(exception.__cause__)
        traceback = exception.__traceback__
        while traceback:
            frame_values = repr(traceback.tb_frame.f_locals)
            self.assertNotIn("secret-cookie", frame_values)
            self.assertNotIn("/secret/profile", frame_values)
            traceback = traceback.tb_next

    def test_403_refreshes_metadata_and_retries_streams(self):
        FakeYoutubeDL.download_failures = [1]
        result = self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self.assertEqual(FakeYoutubeDL.extracts, 2)
        self.assertEqual(FakeYoutubeDL.downloads, ["video", "video", "audio"])
        self.assertTrue(os.path.isfile(result[0]))
        self.assertTrue(os.path.isfile(result[1]))
        self.downloader._wait_before_retry.assert_called_once_with(5)
        transition_statuses = [item.args[0] for item in self.downloader._set_status.call_args_list]
        self.assertTrue(any("best-effort" in status for status in transition_statuses))
        self.assertTrue(any("cannot bypass" in status for status in transition_statuses))

    def test_403_exhaustion_is_bounded_and_cleans_streams(self):
        FakeYoutubeDL.download_failures = [1, 2]
        with self.assertRaises(app.StreamDownloadError) as raised:
            self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self.assertEqual(raised.exception.status_code, 403)
        self.assertEqual(FakeYoutubeDL.extracts, 2)
        self.assertEqual(FakeYoutubeDL.downloads, ["video", "video"])
        self.assertEqual(os.listdir(self.worker_temp.name), [])
        self.assertIn("HTTP 403", raised.exception.user_message)

    def test_403_copy_is_best_effort_and_restriction_aware(self):
        error = app.StreamDownloadError("video", "HTTP Error 403", status_code=403)
        self.assertIn("best-effort", error.user_message)
        self.assertIn("access", error.user_message.lower())
        self.assertIn("does not bypass", error.user_message)

    def test_default_download_never_adds_browser_cookie_option(self):
        self.downloader._active_browser_options = {}
        self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self.assertTrue(FakeYoutubeDL.options_history)
        self.assertTrue(all("cookiesfrombrowser" not in options for options in FakeYoutubeDL.options_history))

    def test_progressive_fallback_selects_and_downloads_one_combined_format(self):
        FakeYoutubeDL.formats = [
            {
                "format_id": "18",
                "vcodec": "avc1",
                "acodec": "mp4a.40.2",
                "height": 720,
                "fps": 30,
                "tbr": 1800,
            }
        ]

        result = self.downloader._download_media("https://example.test/video", self.worker_temp.name)

        self.assertEqual(FakeYoutubeDL.downloads, ["combined"])
        self.assertEqual(result[0], result[1])
        self.assertEqual(FakeYoutubeDL.options_history[-1]["format"], "18")
        self.assertEqual(FakeYoutubeDL.options_history[-1]["max_filesize"], app.MAX_FILE_BYTES)
        self.assertTrue(any("muxed video and audio" in call.args[0] for call in self.downloader._append_log.call_args_list))

    def test_environment_probe_reports_ejs_and_deno_readiness_without_network(self):
        with patch.object(app.importlib.util, "find_spec", return_value=object()), patch.object(
            app.shutil,
            "which",
            side_effect=lambda name: {"deno": "/tools/deno", "ffmpeg": "/tools/ffmpeg", "ffprobe": "/tools/ffprobe"}.get(name),
        ), patch.object(app.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="deno 2.3.1\n", stderr="")):
            status = app.probe_environment()
        self.assertTrue(status.ejs_available)
        self.assertEqual(status.deno_path, "/tools/deno")
        self.assertEqual(status.deno_version, "2.3.1")
        self.assertTrue(status.deno_ready)
        self.assertEqual(status.issues, [])

        with patch.object(app.importlib.util, "find_spec", return_value=None), patch.object(
            app.shutil, "which", side_effect=lambda name: "/tools/deno" if name == "deno" else None
        ), patch.object(
            app.subprocess, "run", return_value=SimpleNamespace(returncode=0, stdout="deno 2.2.0\n", stderr="")
        ):
            missing = app.probe_environment()
        self.assertFalse(missing.ejs_available)
        self.assertEqual(missing.deno_path, "/tools/deno")
        self.assertEqual(missing.deno_version, "2.2.0")
        self.assertFalse(missing.deno_ready)
        self.assertTrue(any("EJS" in issue for issue in missing.issues))
        self.assertTrue(any("Deno" in issue for issue in missing.issues))

    def test_opt_in_browser_option_is_propagated_to_metadata_and_both_streams(self):
        self.downloader._active_browser_options = app.build_browser_session_options(
            "Chrome", "Profile With Spaces"
        )
        self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self.assertEqual(len(FakeYoutubeDL.options_history), 3)
        self.assertTrue(
            all(
                options.get("cookiesfrombrowser") == (
                    "chrome",
                    "Profile With Spaces",
                    None,
                    None,
                )
                for options in FakeYoutubeDL.options_history
            )
        )

    def test_browser_option_blank_profile_uses_ytdlp_default_profile(self):
        options = app.build_browser_session_options("Firefox", "")
        self.assertEqual(options, {"cookiesfrombrowser": ("firefox", None, None, None)})
        self.assertRaises(ValueError, app.build_browser_session_options, "Select a browser", None)

    def test_browser_setup_error_is_actionable_and_redacted(self):
        self.downloader._active_browser_options = app.build_browser_session_options(
            "Chrome", "/secret/profile"
        )
        FakeYoutubeDL.metadata_failures = [1]
        FakeYoutubeDL.metadata_error = (
            "Could not decrypt browser cookies at /secret/profile; cookie=secret-cookie"
        )
        with self.assertRaises(app.BrowserSessionError) as raised:
            self.downloader._resolve_media("https://example.test/video")
        self.assertIn("could not be decrypted", raised.exception.user_message)
        self._assert_no_sensitive_exception_chain(raised.exception)
        self.assertNotIn("secret-cookie", str(raised.exception))
        self.assertNotIn("/secret/profile", str(raised.exception))
        self.assertNotIn("secret-cookie", raised.exception.detail)
        self.assertNotIn("/secret/profile", raised.exception.detail)
        self.assertEqual(self.downloader._active_browser_options, {})

    def test_browser_stream_403_keeps_bounded_retry_and_safe_access_copy(self):
        self.downloader._active_browser_options = app.build_browser_session_options(
            "Chrome", "/secret/profile"
        )
        FakeYoutubeDL.download_failures = [1, 2]
        FakeYoutubeDL.download_error_message = (
            "HTTP Error 403: Forbidden cookie=secret-cookie https://googlevideo.example/a?sig=secret"
        )
        with self.assertRaises(app.StreamDownloadError) as raised:
            self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self.assertEqual(raised.exception.status_code, 403)
        self.assertEqual(raised.exception.user_message, app.BROWSER_SESSION_ACCESS_MESSAGE)
        self._assert_no_sensitive_exception_chain(raised.exception)
        self.assertNotIn("secret-cookie", raised.exception.detail)
        self.assertNotIn("googlevideo.example", raised.exception.detail)
        self.assertEqual(self.downloader._wait_before_retry.call_count, 1)
        self.assertEqual(len(FakeYoutubeDL.options_history), 4)
        self.assertTrue(
            all(
                options.get("cookiesfrombrowser") == (
                    "chrome",
                    "/secret/profile",
                    None,
                    None,
                )
                for options in FakeYoutubeDL.options_history
            )
        )
        self.assertEqual(self.downloader._active_browser_options, {})

    def test_browser_metadata_access_error_has_no_exception_chain_or_sensitive_diagnostics(self):
        self.downloader._active_browser_options = app.build_browser_session_options("Chrome")
        FakeYoutubeDL.metadata_failures = [1]
        FakeYoutubeDL.metadata_error = (
            "request failed for cookie=secret-cookie at /secret/profile?token=secret"
        )
        with self.assertRaises(app.PipelineError) as raised:
            self.downloader._resolve_media("https://example.test/video")
        exception = raised.exception
        self._assert_no_sensitive_exception_chain(exception)
        for visible in (str(exception), exception.detail, exception.user_message):
            self.assertNotIn("secret-cookie", visible)
            self.assertNotIn("/secret/profile", visible)
        self.assertNotIn("secret-cookie", " ".join(call.args[0] for call in self.downloader._append_log.call_args_list))

    def test_browser_metadata_403_is_safe_and_not_retried(self):
        self.downloader._active_browser_options = app.build_browser_session_options("Chrome")
        FakeYoutubeDL.metadata_failures = [1]
        FakeYoutubeDL.metadata_error = (
            "HTTP Error 403: Forbidden cookie=secret-cookie at /secret/profile"
        )
        with self.assertRaises(app.PipelineError) as raised:
            self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        exception = raised.exception
        self._assert_no_sensitive_exception_chain(exception)
        self.assertEqual(exception.user_message, app.BROWSER_SESSION_ACCESS_MESSAGE)
        self.assertFalse(exception.retryable)
        self.assertEqual(FakeYoutubeDL.extracts, 1)
        self.assertEqual(FakeYoutubeDL.downloads, [])
        self.assertNotIn("secret-cookie", exception.detail)
        self.assertNotIn("/secret/profile", exception.detail)

    def test_browser_stream_setup_error_has_no_exception_chain_or_sensitive_diagnostics(self):
        self.downloader._active_browser_options = app.build_browser_session_options(
            "Chrome", "/secret/profile"
        )
        FakeYoutubeDL.download_failures = [1]
        FakeYoutubeDL.download_error_message = (
            "Could not decrypt browser cookies from /secret/profile; cookie=secret-cookie"
        )
        with self.assertRaises(app.BrowserSessionError) as raised:
            self.downloader._download_stream(
                "https://example.test/video", "video-old", self.worker_temp.name, "video"
            )
        exception = raised.exception
        self._assert_no_sensitive_exception_chain(exception)
        for visible in (str(exception), exception.detail, exception.user_message):
            self.assertNotIn("secret-cookie", visible)
            self.assertNotIn("/secret/profile", visible)
        self.assertEqual(self.downloader._active_browser_options, {})

    def test_selected_profile_metadata_decryption_has_clean_traceback_locals(self):
        self.downloader._active_browser_options = app.build_browser_session_options(
            "Chrome", "/secret/profile"
        )
        FakeYoutubeDL.metadata_failures = [1]
        FakeYoutubeDL.metadata_error = (
            "browser decryption failed for /secret/profile; cookie=secret-cookie"
        )
        with self.assertRaises(app.BrowserSessionError) as raised:
            self.downloader._resolve_media("https://example.test/video")
        self._assert_no_sensitive_exception_chain(raised.exception)
        self.assertEqual(self.downloader._active_browser_options, {})

    def test_selected_profile_stream_decryption_has_clean_traceback_locals(self):
        self.downloader._active_browser_options = app.build_browser_session_options(
            "Chrome", "/secret/profile"
        )
        FakeYoutubeDL.download_failures = [1]
        FakeYoutubeDL.download_error_message = (
            "browser decryption failed for /secret/profile; cookie=secret-cookie"
        )
        with self.assertRaises(app.BrowserSessionError) as raised:
            self.downloader._download_stream(
                "https://example.test/video", "video-old", self.worker_temp.name, "video"
            )
        self._assert_no_sensitive_exception_chain(raised.exception)
        self.assertEqual(self.downloader._active_browser_options, {})

    def test_selected_profile_stream_access_conversion_keeps_retry_options_and_clean_locals(self):
        selected_options = app.build_browser_session_options("Chrome", "/secret/profile")
        self.downloader._active_browser_options = selected_options
        FakeYoutubeDL.download_failures = [1, 2]
        FakeYoutubeDL.download_error_message = (
            "HTTP Error 403 for /secret/profile; cookie=secret-cookie"
        )
        with self.assertRaises(app.StreamDownloadError) as raised:
            self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self._assert_no_sensitive_exception_chain(raised.exception)
        self.assertEqual(raised.exception.status_code, 403)
        self.assertEqual(self.downloader._wait_before_retry.call_count, 1)
        self.assertEqual(len(FakeYoutubeDL.options_history), 4)
        self.assertTrue(
            all(options.get("cookiesfrombrowser") == selected_options["cookiesfrombrowser"]
                for options in FakeYoutubeDL.options_history)
        )
        self.assertEqual(self.downloader._active_browser_options, {})

    def test_browser_failure_status_and_activity_log_are_redacted(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        self.downloader._reset_controls = Mock()
        self.downloader._active_browser_options = app.build_browser_session_options("Chrome")
        FakeYoutubeDL.metadata_failures = [1]
        FakeYoutubeDL.metadata_error = (
            "Could not decrypt browser cookies at /secret/profile; cookie=secret-cookie"
        )
        with patch.object(app.messagebox, "showerror") as showerror, patch.object(
            app.tempfile, "mkdtemp", return_value=self.worker_temp.name
        ):
            self.downloader._run_pipeline("https://example.test/video", self.temp.name)
        visible_status = " ".join(call.args[0] for call in self.downloader._set_status.call_args_list)
        activity_log = " ".join(call.args[0] for call in self.downloader._append_log.call_args_list)
        self.assertIn("browser session", visible_status.casefold())
        self.assertIn("browser session", activity_log.casefold())
        self.assertNotIn("secret-cookie", visible_status + activity_log)
        self.assertNotIn("/secret/profile", visible_status + activity_log)
        showerror.assert_called_once_with(
            "Browser session unavailable", app.BROWSER_SESSION_DECRYPTION_MESSAGE
        )

    def _set_up_browser_control_fixtures(self):
        self.downloader.browser_session_var = FakeVar(False)
        self.downloader.browser_var = FakeVar(app.SELECT_BROWSER_LABEL)
        self.downloader.profile_var = FakeVar("")
        self.downloader.browser_session_helper_var = FakeVar("")
        self.downloader.browser_session_checkbutton = FakeControl()
        self.downloader.browser_combobox = FakeControl()
        self.downloader.profile_entry = FakeControl()
        self.downloader.download_button = FakeControl()
        self.downloader.worker_thread = None
        self.downloader._browser_session_consent = False
        self.downloader._browser_session_locked = False

    def test_browser_controls_are_off_by_default_and_lock_during_run(self):
        self._set_up_browser_control_fixtures()
        self.downloader._update_browser_controls()
        self.assertEqual(self.downloader.browser_combobox.configure_calls[-1]["state"], "disabled")
        self.assertEqual(self.downloader.profile_entry.configure_calls[-1]["state"], "disabled")
        self.assertEqual(self.downloader.download_button.configure_calls[-1]["state"], "normal")

        self.downloader.browser_session_var.set(True)
        self.downloader._browser_session_consent = True
        self.downloader._update_browser_controls()
        self.assertEqual(self.downloader.browser_combobox.configure_calls[-1]["state"], "readonly")
        self.assertEqual(self.downloader.profile_entry.configure_calls[-1]["state"], "normal")
        self.assertEqual(self.downloader.download_button.configure_calls[-1]["state"], "disabled")

        self.downloader.browser_var.set("Chrome")
        self.downloader._browser_selection_changed()
        self.assertEqual(self.downloader.download_button.configure_calls[-1]["state"], "normal")
        self.downloader._lock_browser_session_controls()
        self.assertEqual(self.downloader.browser_combobox.configure_calls[-1]["state"], "disabled")
        self.assertEqual(self.downloader.browser_session_checkbutton.configure_calls[-1]["state"], "disabled")

    def test_browser_consent_decline_is_safe_and_reset_clears_in_memory_selection(self):
        self._set_up_browser_control_fixtures()
        self.downloader._set_status = Mock()
        self.downloader._show_browser_session_consent = Mock(return_value=False)
        self.downloader.browser_session_var.set(True)
        self.downloader._toggle_browser_session()
        self.assertFalse(self.downloader.browser_session_var.get())
        self.assertFalse(self.downloader._browser_session_consent)
        self.downloader._show_browser_session_consent.assert_called_once_with()
        self.assertIn("No browser data was read", self.downloader._set_status.call_args.args[0])

        self.downloader.browser_session_var.set(True)
        self.downloader._browser_session_consent = True
        self.downloader.browser_var.set("Chrome")
        self.downloader.profile_var.set("Profile")
        self.downloader._active_browser_options = app.build_browser_session_options("Chrome", "Profile")
        self.downloader._reset_browser_session_controls()
        self.assertFalse(self.downloader.browser_session_var.get())
        self.assertEqual(self.downloader.browser_var.get(), app.SELECT_BROWSER_LABEL)
        self.assertEqual(self.downloader.profile_var.get(), "")
        self.assertEqual(self.downloader._active_browser_options, {})

    def test_rate_limit_is_not_retried(self):
        FakeYoutubeDL.download_failures = [1]
        FakeYoutubeDL.download_error_status = 429
        with self.assertRaises(app.StreamDownloadError) as raised:
            self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self.assertEqual(raised.exception.status_code, 429)
        self.assertFalse(raised.exception.retryable)
        self.assertEqual(FakeYoutubeDL.extracts, 1)
        self.assertEqual(self.downloader._wait_before_retry.call_count, 0)
        self.assertIn("rate-limited", raised.exception.user_message)
        self.assertIn("will not retry", raised.exception.user_message)

    def test_playlist_metadata_is_rejected_for_single_video_contract(self):
        FakeYoutubeDL.metadata_type = "playlist"
        with self.assertRaises(app.PipelineError) as raised:
            self.downloader._resolve_media("https://example.test/playlist")
        self.assertEqual(raised.exception.stage, "metadata")
        self.assertIn("Playlist URLs are not supported", raised.exception.user_message)
        self.assertIn("single-video", raised.exception.user_message)

    def test_metadata_403_copy_is_access_specific(self):
        FakeYoutubeDL.metadata_failures = [1]
        FakeYoutubeDL.metadata_error = "ERROR: HTTP Error 403: Forbidden"
        with self.assertRaises(app.PipelineError) as raised:
            self.downloader._resolve_media("https://example.test/video")
        self.assertIn("HTTP 403", raised.exception.user_message)
        self.assertIn("access", raised.exception.user_message.lower())
        self.assertIn("does not bypass", raised.exception.user_message)

    def test_completion_status_marks_oversized_output_as_warning(self):
        message, color = app.completion_status("/tmp/large.mp4", oversized=True)
        self.assertIn("Completed with warning", message)
        self.assertIn("5 GB", message)
        self.assertIn("not Unifi-compliant", message)
        self.assertEqual(color, "#ffbf00")

    def test_local_file_uri_encodes_output_path(self):
        output_path = os.path.join(self.temp.name, "final video #1.mp4")
        with open(output_path, "wb") as output:
            output.write(b"mp4")
        uri = app.local_file_uri(output_path)
        self.assertTrue(uri.startswith("file://"))
        self.assertIn("final%20video%20%231.mp4", uri)
        self.assertNotIn("example.test", uri)

    def test_open_browser_button_enables_only_for_verified_mp4(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        self.downloader.open_browser_button = FakeControl()
        valid_path = os.path.join(self.temp.name, "ready.mp4")
        with open(valid_path, "wb") as output:
            output.write(b"mp4")
        self.downloader._set_completed_output(valid_path)
        self.assertEqual(self.downloader.open_browser_button.configure_calls[-1]["state"], "normal")
        self.assertEqual(self.downloader._completed_output_path, valid_path)

        missing_path = os.path.join(self.temp.name, "gone.mp4")
        self.downloader._set_completed_output(missing_path)
        self.assertEqual(self.downloader.open_browser_button.configure_calls[-1]["state"], "disabled")
        self.assertIsNone(self.downloader._completed_output_path)

    def test_open_browser_uses_only_verified_local_file_uri(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        output_path = os.path.join(self.temp.name, "ready video.mp4")
        with open(output_path, "wb") as output:
            output.write(b"mp4")
        self.downloader._completed_output_path = output_path
        with patch.object(app.webbrowser, "open", return_value=True) as launch:
            self.downloader._open_completed_in_browser()
        launch.assert_called_once_with(app.local_file_uri(output_path))
        self.assertIn("Opened ready video.mp4", self.downloader._set_status.call_args.args[0])

    def test_open_browser_handles_missing_output_without_launching(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        self.downloader.open_browser_button = FakeControl()
        self.downloader._completed_output_path = os.path.join(self.temp.name, "missing.mp4")
        with patch.object(app.messagebox, "showerror") as showerror, patch.object(
            app.webbrowser, "open"
        ) as launch:
            self.downloader._open_completed_in_browser()
        launch.assert_not_called()
        showerror.assert_called_once_with("Open in Browser", app.OPEN_BROWSER_MISSING_MESSAGE)
        self.assertIn("no longer available", self.downloader._set_status.call_args.args[0])
        self.assertEqual(self.downloader.open_browser_button.configure_calls[-1]["state"], "disabled")

    def test_open_browser_handles_default_handler_failure_without_crashing(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        output_path = os.path.join(self.temp.name, "ready.mp4")
        with open(output_path, "wb") as output:
            output.write(b"mp4")
        self.downloader._completed_output_path = output_path
        with patch.object(app.messagebox, "showerror") as showerror, patch.object(
            app.webbrowser, "open", side_effect=OSError("no handler")
        ):
            self.downloader._open_completed_in_browser()
        showerror.assert_called_once_with("Open in Browser", app.OPEN_BROWSER_FAILURE_MESSAGE)
        self.assertIn("Couldn't open", self.downloader._set_status.call_args.args[0])

    def test_oversized_staged_output_sets_completion_warning_state(self):
        self.downloader._execute_ffmpeg = Mock(side_effect=self._fake_ffmpeg)
        with patch.object(app, "MAX_FILE_BYTES", 1):
            output = self.downloader._transcode_and_mux(
                "video.webm",
                "audio.webm",
                {"title": "Large", "duration": 1},
                self.temp.name,
                {"vcodec": "vp9", "acodec": "none", "fps": 30},
                {"vcodec": "none", "acodec": "opus", "abr": 128},
            )
        self.assertTrue(os.path.isfile(output))
        self.assertTrue(self.downloader._last_output_warning)

    def test_pipeline_failure_exposes_try_again_action(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        self.downloader._reset_controls = Mock()
        self.downloader._download_media = Mock(
            side_effect=app.PipelineError("metadata", "bad URL", "fixture")
        )
        with patch.object(app.tempfile, "mkdtemp", return_value=self.worker_temp.name):
            self.downloader._run_pipeline("https://example.test/video", self.temp.name)
        self.downloader._reset_controls.assert_called_once_with(retry=True)
        self.assertIn("Error: bad URL", self.downloader._set_status.call_args_list[-1].args[0])
        self.assertIn("Failed", [call.args[0] for call in getattr(self.downloader._set_stage, "call_args_list", [])])

    def test_cleanup_warning_preserves_completed_output_status(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        self.downloader._reset_controls = Mock()
        self.downloader._download_media = Mock(return_value=("video", "audio", {}, {}, {}))
        completed_path = os.path.join(self.temp.name, "Completed.mp4")
        with open(completed_path, "wb") as output:
            output.write(b"completed")
        self.downloader._transcode_and_mux = Mock(return_value=completed_path)
        self.downloader._last_output_warning = False
        with patch.object(app.tempfile, "mkdtemp", return_value=self.worker_temp.name), patch.object(
            app.shutil, "rmtree", side_effect=OSError("busy")
        ):
            self.downloader._run_pipeline("https://example.test/video", self.temp.name)
        final_status = self.downloader._set_status.call_args_list[-1].args
        self.assertIn("Completed: Completed.mp4", final_status[0])
        self.assertIn("temporary files could not be removed", final_status[0])
        self.assertNotIn("Download stopped", final_status[0])
        self.assertEqual(final_status[1], "#ffbf00")

    def test_retry_controls_are_explicit_and_focus_start_action(self):
        self.downloader.download_button = Mock()
        self.downloader.cancel_button = Mock()
        self.downloader._reset_controls(retry=True)
        self.assertIn(
            "Try Again",
            {
                item.kwargs.get("text")
                for item in self.downloader.download_button.configure.call_args_list
            },
        )
        self.downloader.download_button.focus_set.assert_called_once_with()

    def test_unknown_duration_progress_is_indeterminate_not_line_count(self):
        command = [sys.executable, "-c", "print('frame=1 time=00:00:01.00', flush=True)"]
        self.downloader._execute_ffmpeg(command, "Transcoding", None, 20, 98)
        progress_values = [item.args[0] for item in self.downloader._set_progress.call_args_list]
        self.assertIn(None, progress_values)
        self.assertNotIn(21, progress_values)

    def test_cancellation_after_finalization_keeps_truthful_output_state(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        self.downloader._reset_controls = Mock()
        output_path = os.path.join(self.temp.name, "Completed.mp4")
        with open(output_path, "wb") as output:
            output.write(b"completed")
        self.downloader._download_media = Mock(return_value=("video", "audio", {}, {}, {}))

        def finalize_then_cancel(*_args):
            self.downloader.cancel_requested = True
            return output_path

        self.downloader._transcode_and_mux = Mock(side_effect=finalize_then_cancel)
        with patch.object(app.tempfile, "mkdtemp", return_value=self.worker_temp.name):
            self.downloader._run_pipeline("https://example.test/video", self.temp.name)
        final_status = self.downloader._set_status.call_args_list[-1].args[0]
        self.assertIn("Completed", final_status)
        self.assertIn("output preserved", final_status)
        self.assertNotIn("No completed file was saved", final_status)

    def test_metadata_failure_is_not_retried_as_stream_failure(self):
        FakeYoutubeDL.metadata_failures = [1]
        with self.assertRaises(app.PipelineError) as raised:
            self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self.assertEqual(raised.exception.stage, "metadata")
        self.assertFalse(raised.exception.retryable)
        self.assertEqual(FakeYoutubeDL.extracts, 1)
        self.assertEqual(FakeYoutubeDL.downloads, [])

    def test_cancellation_is_not_retried(self):
        self.downloader.cancel_requested = True
        with self.assertRaises(app.DownloadCancelled):
            self.downloader._download_media("https://example.test/video", self.worker_temp.name)
        self.assertEqual(FakeYoutubeDL.extracts, 0)
        self.downloader._wait_before_retry.assert_not_called()

    def test_safe_error_detail_redacts_signed_url(self):
        detail = app.safe_error_detail(
            ValueError("HTTP Error 403: https://googlevideo.example/videoplayback?sig=secret&token=abc")
        )
        self.assertNotIn("googlevideo.example", detail)
        self.assertNotIn("secret", detail)
        self.assertNotIn("abc", detail)
        self.assertIn("<url>", detail)

    def _fake_ffmpeg(self, command, stage_label, duration, start_progress, end_progress):
        with open(command[-1], "wb") as output:
            output.write(b"complete mp4 fixture")

    def test_output_is_staged_then_published_safely(self):
        self.downloader._execute_ffmpeg = Mock(side_effect=self._fake_ffmpeg)
        existing = os.path.join(self.temp.name, "Unsafe Title.mkv")
        with open(existing, "wb") as output:
            output.write(b"existing")
        output = self.downloader._transcode_and_mux(
            "video.webm",
            "audio.webm",
            {"title": "Unsafe: Title", "duration": 1},
            self.temp.name,
            {"vcodec": "vp9", "acodec": "none", "fps": 30},
            {"vcodec": "none", "acodec": "opus", "abr": 128},
        )
        self.assertEqual(os.path.dirname(output), os.path.abspath(self.temp.name))
        self.assertEqual(os.path.basename(output), "Unsafe Title (1).mkv")
        self.assertEqual(sorted(os.listdir(self.temp.name)), ["Unsafe Title (1).mkv", "Unsafe Title.mkv"])

    def test_unifi_compatibility_frame_rate_is_bounded_without_overriding_allowed_source(self):
        setattr(self.downloader, "unifi_compatible_var", FakeVar(True))
        for source_fps in (60, None):
            with self.subTest(source_fps=source_fps):
                self.downloader._execute_ffmpeg = Mock(side_effect=self._fake_ffmpeg)
                self.downloader._transcode_and_mux(
                    "video.webm",
                    "audio.webm",
                    {"title": "FPS", "duration": 1},
                    self.temp.name,
                    {"vcodec": "vp9", "acodec": "none", "fps": source_fps},
                    {"vcodec": "none", "acodec": "opus", "abr": 128},
                )
                command = self.downloader._execute_ffmpeg.call_args.args[0]
                self.assertIn(["-r", "30"], [command[index:index + 2] for index in range(len(command) - 1)])

        self.downloader._execute_ffmpeg = Mock(side_effect=self._fake_ffmpeg)
        self.downloader._transcode_and_mux(
            "video.webm", "audio.webm", {"title": "Allowed FPS", "duration": 1}, self.temp.name,
            {"vcodec": "vp9", "acodec": "none", "fps": 25},
            {"vcodec": "none", "acodec": "opus", "abr": 128},
        )
        command = self.downloader._execute_ffmpeg.call_args.args[0]
        self.assertNotIn("-r", command)

    def test_ffmpeg_failure_leaves_no_partial_output(self):
        def partial_failure(command, stage_label, duration, start_progress, end_progress):
            with open(command[-1], "wb") as output:
                output.write(b"partial")
            raise RuntimeError("exit code 1")

        self.downloader._execute_ffmpeg = Mock(side_effect=partial_failure)
        with self.assertRaises(app.PipelineError) as raised:
            self.downloader._transcode_and_mux(
                "video.webm",
                "audio.webm",
                {"title": "Failure", "duration": 1},
                self.temp.name,
                {"vcodec": "vp9", "acodec": "none", "fps": 30},
                {"vcodec": "none", "acodec": "opus", "abr": 128},
            )
        self.assertEqual(raised.exception.stage, "FFmpeg")
        self.assertEqual(os.listdir(self.temp.name), [])

    def test_ffmpeg_cancellation_leaves_no_partial_output(self):
        self.downloader._execute_ffmpeg = Mock(side_effect=app.DownloadCancelled())
        with self.assertRaises(app.DownloadCancelled):
            self.downloader._transcode_and_mux(
                "video.webm",
                "audio.webm",
                {"title": "Cancelled", "duration": 1},
                self.temp.name,
                {"vcodec": "vp9", "acodec": "none", "fps": 30},
                {"vcodec": "none", "acodec": "opus", "abr": 128},
            )
        self.assertEqual(os.listdir(self.temp.name), [])

    def test_ffmpeg_cancellation_stops_quiet_sigterm_ignoring_child(self):
        line_seen = threading.Event()
        finished = threading.Event()
        errors = []

        def append_log(message):
            line_seen.set()

        self.downloader._append_log = append_log
        command = [
            sys.executable,
            "-c",
            "import signal; print('ready', flush=True); signal.signal(signal.SIGTERM, signal.SIG_IGN); signal.pause()",
        ]

        def run_ffmpeg():
            try:
                self.downloader._execute_ffmpeg(command, "Transcoding", None, 20, 98)
            except BaseException as exc:
                errors.append(exc)
            finally:
                finished.set()

        worker = threading.Thread(target=run_ffmpeg)
        worker.start()
        process = None
        try:
            self.assertTrue(line_seen.wait(1), "quiet child did not produce its initial line")
            process = self.downloader.active_process
            self.assertIsNotNone(process)
            assert process is not None
            self.downloader.cancel_requested = True
            process.terminate()
            started = time.monotonic()
            self.assertTrue(finished.wait(2), "FFmpeg cancellation remained blocked")
            self.assertLessEqual(time.monotonic() - started, 2)
        finally:
            if process is not None and process.poll() is None:
                process.kill()
            worker.join(1)

        self.assertFalse(worker.is_alive())
        self.assertEqual(len(errors), 1)
        self.assertIsInstance(errors[0], app.DownloadCancelled)
        self.assertIsNone(self.downloader.active_process)
        assert process is not None
        self.assertIsNotNone(process.poll())


if __name__ == "__main__":
    unittest.main()
