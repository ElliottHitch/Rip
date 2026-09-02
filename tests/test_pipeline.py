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


class FakeYoutubeDL:
    extracts = 0
    downloads = []
    download_failures = []
    metadata_failures = []
    metadata_type = None
    metadata_error = "Unable to extract metadata"
    download_error_status = 403

    def __init__(self, options):
        self.options = options

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
            "formats": FORMATS,
            **({"_type": type(self).metadata_type} if type(self).metadata_type else {}),
        }

    def download(self, urls):
        label = "video" if self.options["format"] == "video-old" else "audio"
        type(self).downloads.append(label)
        path = self.options["outtmpl"].replace("%(ext)s", "mp4")
        with open(path, "wb") as output:
            output.write(label.encode("ascii"))
        if len(type(self).downloads) in type(self).download_failures:
            status = type(self).download_error_status
            raise FakeYtdlpError(
                f"ERROR: HTTP Error {status}: Forbidden https://googlevideo.example/a?sig=secret"
            )
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
        FakeYoutubeDL.downloads = []
        FakeYoutubeDL.download_failures = []
        FakeYoutubeDL.metadata_failures = []
        FakeYoutubeDL.metadata_type = None
        FakeYoutubeDL.metadata_error = "Unable to extract metadata"
        FakeYoutubeDL.download_error_status = 403
        self.original_yt_dlp = app.yt_dlp
        app.yt_dlp = SimpleNamespace(YoutubeDL=FakeYoutubeDL)

    def tearDown(self):
        app.yt_dlp = self.original_yt_dlp
        self.temp.cleanup()
        self.worker_temp.cleanup()

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

    def test_cleanup_warning_preserves_completed_output_status(self):
        self.downloader.root = SimpleNamespace(after=lambda _delay, callback: callback())
        self.downloader._reset_controls = Mock()
        self.downloader._download_media = Mock(return_value=("video", "audio", {}, {}, {}))
        self.downloader._transcode_and_mux = Mock(return_value=os.path.join(self.temp.name, "Completed.mp4"))
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
        existing = os.path.join(self.temp.name, "Unsafe Title.mp4")
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
        self.assertEqual(os.path.basename(output), "Unsafe Title (1).mp4")
        self.assertEqual(sorted(os.listdir(self.temp.name)), ["Unsafe Title (1).mp4", "Unsafe Title.mp4"])

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
