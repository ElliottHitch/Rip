from __future__ import annotations

import importlib.util
import math
import os
import queue
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import tkinter as tk
import webbrowser
from dataclasses import dataclass, field
from pathlib import Path
from tkinter import ttk, filedialog, messagebox
from typing import Dict, Optional, Tuple

try:
    import yt_dlp
    YT_DLP_IMPORT_ERROR: Optional[ImportError] = None
except ImportError as exc:  # Keep the GUI available to explain the missing dependency.
    yt_dlp = None  # type: ignore[assignment]
    YT_DLP_IMPORT_ERROR = exc

ALLOWED_FPS = {24, 25, 30}
DENO_MIN_VERSION = (2, 3, 0)
TARGET_VIDEO_BITRATE = 40_000_000  # bits per second (40 Mbps)
TARGET_AUDIO_BITRATE = 192_000  # bits per second
MAX_FILE_BYTES = 5 * 1024 * 1024 * 1024  # 5 GB
MAX_DOWNLOAD_ATTEMPTS = 2
RETRY_BACKOFF_SECONDS = (5,)
FFMPEG_CANCEL_GRACE_SECONDS = 0.5
FFMPEG_READER_POLL_SECONDS = 0.1
SELECT_BROWSER_LABEL = "Select a browser"
GENERIC_CONTAINER_EXTENSIONS = {"mkv", "webm", "mp4", "m4v", "mov"}
SUPPORTED_BROWSERS = (
    "Brave",
    "Chrome",
    "Chromium",
    "Edge",
    "Firefox",
    "Opera",
    "Safari",
    "Vivaldi",
    "Whale",
)
_BROWSER_NAMES = {name.casefold(): name.lower() for name in SUPPORTED_BROWSERS}

BROWSER_SESSION_UNAVAILABLE_MESSAGE = (
    "Couldn't read the selected browser session. Check that the browser is installed, "
    "the profile name or path is correct, and the browser is closed, then try again. "
    "The app did not save or export cookies."
)
BROWSER_SESSION_DECRYPTION_MESSAGE = (
    "The selected browser session could not be decrypted in this environment. Check the "
    "browser's OS keyring support and the app's Python/yt-dlp dependencies, then try again "
    "or turn off Use browser session. The app did not save or export cookies."
)
BROWSER_SESSION_ACCESS_MESSAGE = (
    "The selected browser session did not grant access to this video. Confirm that the video "
    "is available to the signed-in account, refresh the page in the same browser, and try "
    "again later. The app does not bypass service restrictions."
)
BROWSER_SESSION_PLATFORM_MESSAGE = (
    "Browser sessions aren't available for the selected browser on this operating system. "
    "Choose another supported browser or turn off Use browser session."
)
BROWSER_SESSION_CONSENT_BODY = (
    "For this download, yt-dlp will read cookies from the selected local browser profile. "
    "Use only an account and media you are authorized to access. Sign in independently in "
    "your browser; this app never asks for your YouTube password, exports or saves cookies, "
    "uploads browser data, or bypasses login, age, region, policy, PO-token, rate, or other "
    "service restrictions. For safety, prefer a dedicated browser profile. Close the browser "
    "before starting if it prevents access to its profile data."
)
OPEN_BROWSER_MISSING_MESSAGE = (
    "The completed file is no longer available. Use Open Folder to locate files or download "
    "the video again."
)
OPEN_BROWSER_FAILURE_MESSAGE = (
    "Couldn't open the completed file in your default browser. Use Open Folder to locate the "
    "MP4 instead."
)


class DownloadCancelled(Exception):
    """Raised when the user cancels an in-progress download/transcode."""


class PipelineError(Exception):
    """An expected failure with a safe user-facing message and pipeline stage."""

    def __init__(self, stage: str, user_message: str, detail: str, *, retryable: bool = False):
        super().__init__(detail)
        self.stage = stage
        self.user_message = user_message
        self.detail = detail
        self.retryable = retryable


class StreamDownloadError(PipelineError):
    """A stream failure that may be recoverable by fresh extraction."""

    def __init__(self, label: str, detail: str, *, status_code: Optional[int] = None):
        label_title = label.capitalize()
        if status_code == 403:
            user_message = (
                f"{label_title} stream access was refused (HTTP 403). One bounded, best-effort "
                "refresh of stream details did not resolve it. This may reflect access, login, "
                "age, region, policy, PO-token, or rate restrictions; the app does not bypass "
                "service restrictions. Check that the video is available to you, then try again."
            )
        elif status_code == 429:
            user_message = (
                f"{label_title} stream was rate-limited (HTTP 429). The app will not retry "
                "automatically. Wait before trying again and avoid repeated requests."
            )
        else:
            user_message = "The connection could not be completed. Check your network connection and try again."
        super().__init__(
            f"{label} download",
            user_message,
            detail,
            retryable=status_code == 403 or status_code is None or 500 <= (status_code or 0) < 600,
        )
        self.label = label
        self.status_code = status_code


class BrowserSessionError(PipelineError):
    """A browser-session setup failure with deliberately generic diagnostics."""

    def __init__(self, stage: str, user_message: str):
        # Never retain yt-dlp's browser/cookie diagnostics in the exception detail. The
        # detail is also written to the Activity Log by the pipeline error handler.
        super().__init__(stage, user_message, "Browser session setup failed.")


def build_browser_session_options(browser: str, profile: Optional[str] = None) -> Dict:
    """Build yt-dlp's supported browser-cookie option without touching browser data."""
    browser_key = (browser or "").strip().casefold()
    browser_name = _BROWSER_NAMES.get(browser_key)
    if not browser_name:
        raise ValueError("Choose a supported browser before starting the download.")
    if browser_name == "safari" and sys.platform != "darwin":
        raise ValueError(BROWSER_SESSION_PLATFORM_MESSAGE)
    # yt-dlp accepts a four-item tuple: browser, profile, keyring, container. An empty
    # profile intentionally means yt-dlp selects its normal most-recently-accessed profile.
    selected_profile = profile if profile else None
    return {"cookiesfrombrowser": (browser_name, selected_profile, None, None)}


def is_browser_setup_error(detail: str) -> bool:
    """Identify local browser/profile/keyring failures without parsing cookie values."""
    lowered = detail.casefold()
    return any(
        marker in lowered
        for marker in (
            "browser",
            "profile",
            "cookie",
            "keyring",
            "decrypt",
            "cookiesfrombrowser",
            "database",
        )
    )


def is_verified_mp4(path: str) -> bool:
    """Check the exact published output before exposing it to the browser action."""
    try:
        return (
            bool(path)
            and Path(path).suffix.casefold() == ".mp4"
            and os.path.isfile(path)
            and os.path.getsize(path) > 0
            and os.access(path, os.R_OK)
        )
    except (OSError, ValueError, TypeError):
        return False


def is_verified_output(path: str) -> bool:
    """Verify a published generic output without implying it is browser-playable MP4."""
    try:
        return bool(path) and Path(path).suffix.casefold() in {".mp4", ".mkv"} and os.path.isfile(path) and os.path.getsize(path) > 0 and os.access(path, os.R_OK)
    except (OSError, ValueError, TypeError):
        return False


def local_file_uri(path: str) -> str:
    """Return a properly encoded URI for a local file, never a source URL."""
    return Path(path).expanduser().resolve().as_uri()


def metadata_error_message(status_code: Optional[int]) -> str:
    """Return truthful, stage-specific metadata failure guidance."""
    if status_code == 403:
        return (
            "Metadata access was refused (HTTP 403). Check that the video is available to you; "
            "the app does not bypass login, age, region, policy, or other service restrictions."
        )
    if status_code == 429:
        return (
            "Metadata was rate-limited (HTTP 429). The app will not retry automatically; "
            "wait before trying again and avoid repeated requests."
        )
    return "Couldn't read this URL. Check the link and your connection, then try again."


def completion_status(output_path: str, *, oversized: bool = False) -> Tuple[str, str]:
    """Build the visible completion state and its semantic color."""
    filename = os.path.basename(output_path)
    if oversized:
        return (
            f"Completed with warning: {filename} was saved, but it is not Unifi-compliant; it exceeds the "
            "5 GB compatibility limit because it is larger than the bounded UniFi-compatible file size.",
            "#ffbf00",
        )
    return f"Completed: {filename}", "#4dcc7d"


def is_playlist_metadata(metadata: Dict) -> bool:
    """Identify extractor results that represent more than one video."""
    return metadata.get("_type") in {"playlist", "multi_video"}


def sanitize_child_output(text: str) -> str:
    """Remove ANSI/control sequences before child text reaches visible UI or logs."""
    text = re.sub(r"\x1b(?:\[[0-?]*[ -/]*[@-~]|\].*?(?:\x07|\x1b\\))", "", text or "")
    return "".join(character for character in text if not ord(character) < 32 or character in "\n\r\t")


def safe_error_detail(exc: BaseException) -> str:
    """Redact URLs and credential-like query values from diagnostics."""
    detail = sanitize_child_output(str(exc)).replace("\n", " ").strip()
    detail = re.sub(r"https?://[^\s]+", "<url>", detail, flags=re.IGNORECASE)
    detail = re.sub(
        r"(?i)(authorization|cookie|token|signature|sig|key|videoplayback)=[^&\s]+",
        r"\1=<redacted>",
        detail,
    )
    return detail[:500] or exc.__class__.__name__


def ytdlp_status_code(exc: BaseException) -> Optional[int]:
    match = re.search(r"\bHTTP(?: error)?\s+(\d{3})\b", str(exc), re.IGNORECASE)
    return int(match.group(1)) if match else None


@dataclass
class EnvironmentStatus:
    yt_dlp_available: bool = False
    yt_dlp_version: Optional[str] = None
    ejs_available: bool = False
    deno_path: Optional[str] = None
    deno_version: Optional[str] = None
    deno_ready: bool = False
    ffmpeg_path: Optional[str] = None
    ffprobe_path: Optional[str] = None
    nvenc_available: bool = False
    issues: list[str] = field(default_factory=list)


def sanitize_filename(name: str) -> str:
    """Generate a filesystem-friendly filename."""
    name = re.sub(r"[<>:\"/\\|?*]", "", name)
    name = re.sub(r"[\x00-\x1f\x7f]", "", name)
    name = re.sub(r"\s+", " ", name).strip()
    name = name.rstrip(". ")
    return name or "video"


def human_readable_size(num_bytes: float) -> str:
    """Convert bytes to a friendly string."""
    if num_bytes <= 0:
        return "0 B"
    units = ["B", "KB", "MB", "GB", "TB"]
    power = min(int(math.log(num_bytes, 1024)), len(units) - 1)
    return f"{num_bytes / (1024 ** power):.2f} {units[power]}"


def probe_environment() -> EnvironmentStatus:
    """Detect yt-dlp/EJS/Deno and ffmpeg/ffprobe readiness without network access."""
    status = EnvironmentStatus()
    if yt_dlp is not None:
        status.yt_dlp_available = True
        status.yt_dlp_version = getattr(getattr(yt_dlp, "version", None), "__version__", None)
    else:
        status.issues.append("yt-dlp is not installed. Install the requirements before downloading.")
    status.ejs_available = importlib.util.find_spec("yt_dlp_ejs") is not None
    if not status.ejs_available:
        status.issues.append("yt-dlp EJS scripts are missing. Install yt-dlp[default] before downloading.")
    status.deno_path = shutil.which("deno")
    if status.deno_path:
        try:
            deno_result = subprocess.run(
                [status.deno_path, "--version"],
                capture_output=True,
                text=True,
                check=False,
                timeout=5,
            )
            deno_output = sanitize_child_output((deno_result.stdout or "") + "\n" + (deno_result.stderr or ""))
            match = re.search(r"(?im)^deno\s+(\d+)\.(\d+)\.(\d+)", deno_output)
            if deno_result.returncode == 0 and match:
                status.deno_version = ".".join(match.groups())
                status.deno_ready = tuple(int(part) for part in match.groups()) >= DENO_MIN_VERSION
        except (OSError, subprocess.SubprocessError, ValueError):
            pass
    if not status.deno_ready:
        status.issues.append("Deno 2.3.0 or newer is required. Install a supported local JavaScript runtime before downloading.")
    status.ffmpeg_path = shutil.which("ffmpeg")
    status.ffprobe_path = shutil.which("ffprobe")
    if not status.ffmpeg_path:
        status.issues.append("FFmpeg not found on PATH.")
    if not status.ffprobe_path:
        status.issues.append("FFprobe not found on PATH.")
    if status.ffmpeg_path:
        try:
            result = subprocess.run(
                [
                    status.ffmpeg_path,
                    "-hide_banner",
                    "-loglevel",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    "color=c=black:s=16x16:r=1",
                    "-frames:v",
                    "1",
                    "-c:v",
                    "h264_nvenc",
                    "-f",
                    "null",
                    "-",
                ],
                capture_output=True,
                text=True,
                check=False,
                timeout=10,
            )
            status.nvenc_available = result.returncode == 0
        except Exception as exc:
            status.issues.append(f"Unable to probe NVENC: {safe_error_detail(exc)}")
    return status


def choose_target_fps(source_fps: Optional[float]) -> int:
    """Pick an allowed frame rate, defaulting to 30 when unsure."""
    if source_fps:
        rounded = round(source_fps)
        if rounded in ALLOWED_FPS:
            return rounded
    return 30


def pick_best_formats(info: Dict) -> Tuple[Dict, Dict]:
    """Determine best dedicated streams, or one progressive fallback exactly once."""
    formats = info.get("formats") or []
    video_candidates = [
        fmt
        for fmt in formats
        if fmt.get("vcodec") not in (None, "none") and fmt.get("acodec") in (None, "none", "")
    ]
    audio_candidates = [
        fmt
        for fmt in formats
        if fmt.get("acodec") not in (None, "none") and fmt.get("vcodec") in (None, "none", "")
    ]

    if not video_candidates or not audio_candidates:
        progressive = [
            fmt for fmt in formats
            if fmt.get("vcodec") not in (None, "none") and fmt.get("acodec") not in (None, "none", "")
        ]
        if not progressive:
            raise RuntimeError("Unable to locate suitable video/audio formats for this URL.")
        best = max(progressive, key=lambda fmt: (
            fmt.get("height") or 0, fmt.get("fps") or 0, fmt.get("tbr") or 0,
            fmt.get("format_id", "")
        ))
        return best, best

    def video_key(fmt: Dict) -> Tuple:
        return (
            fmt.get("height") or 0,
            fmt.get("fps") or 0,
            fmt.get("tbr") or 0,
            -fmt.get("filesize", 0),
            fmt.get("format_id", ""),
        )

    def audio_key(fmt: Dict) -> Tuple:
        return (
            fmt.get("abr") or fmt.get("tbr") or 0,
            fmt.get("asr") or 0,
            -fmt.get("filesize", 0),
            fmt.get("format_id", ""),
        )

    best_video = max(video_candidates, key=video_key)
    best_audio = max(audio_candidates, key=audio_key)
    return best_video, best_audio


def format_filesize(fmt: Dict) -> Optional[int]:
    return fmt.get("filesize") or fmt.get("filesize_approx")


def should_passthrough(video_fmt: Dict, audio_fmt: Dict) -> bool:
    """Return True when streams already satisfy Unifi constraints."""
    vcodec = (video_fmt.get("vcodec") or "").lower()
    acodec = (audio_fmt.get("acodec") or "").lower()
    video_ext = (video_fmt.get("ext") or "").lower()
    audio_ext = (audio_fmt.get("ext") or "").lower()
    fps = video_fmt.get("fps") or video_fmt.get("framerate")
    video_bitrate = video_fmt.get("tbr")
    audio_bitrate = audio_fmt.get("abr") or audio_fmt.get("tbr")
    size_video = format_filesize(video_fmt) or 0
    size_audio = format_filesize(audio_fmt) or 0

    video_codec_ok = "h264" in vcodec or "avc" in vcodec
    audio_codec_ok = "aac" in acodec or "mp4a" in acodec
    container_ok = video_ext in {"mp4", "m4v"} and audio_ext in {"m4a", "mp4", "aac"}
    fps_ok = bool(fps) and round(fps) in ALLOWED_FPS
    video_rate_ok = not video_bitrate or video_bitrate <= 41_000
    audio_rate_ok = not audio_bitrate or audio_bitrate <= 256
    size_ok = (size_video + size_audio) <= MAX_FILE_BYTES if (size_video and size_audio) else True

    return all(
        [video_codec_ok, audio_codec_ok, container_ok, fps_ok, video_rate_ok, audio_rate_ok, size_ok]
    )


def parse_ffmpeg_time(progress_line: str) -> Optional[float]:
    """Extract HH:MM:SS.xx timestamp (in seconds) from an ffmpeg line."""
    match = re.search(r"time=(\d+):(\d+):(\d+(?:\.\d+)?)", progress_line)
    if not match:
        return None
    hours, minutes, seconds = match.groups()
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def ensure_unique_path(directory: str, base_name: str, extension: str = ".mp4") -> str:
    """Generate a unique output path inside directory."""
    os.makedirs(directory, exist_ok=True)
    directory = os.path.abspath(directory)
    base_name = sanitize_filename(base_name)
    if not extension.startswith("."):
        extension = "." + extension
    candidate = os.path.join(directory, f"{base_name}{extension}")
    counter = 1
    while os.path.exists(candidate):
        candidate = os.path.join(directory, f"{base_name} ({counter}){extension}")
        counter += 1
    return candidate


class YouTubeDownloaderApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("YouTube Downloader")
        self.root.geometry("820x600")
        self.root.minsize(720, 520)

        self.cancel_requested = False
        self.active_process: Optional[subprocess.Popen] = None
        self.worker_thread: Optional[threading.Thread] = None

        self.env_status = probe_environment()

        self.url_var = tk.StringVar()
        default_dir = os.path.join(os.path.expanduser("~/Videos"), "YouTube Downloads")
        self.path_var = tk.StringVar(value=default_dir)
        self.unifi_compatible_var = tk.BooleanVar(value=False)
        self.stage_var = tk.StringVar(value="Waiting for input")
        self.status_var = tk.StringVar(value="Ready.")
        self.progress_var = tk.IntVar(value=0)
        self.env_summary_var = tk.StringVar()
        self.browser_session_var = tk.BooleanVar(value=False)
        self.browser_var = tk.StringVar(value=SELECT_BROWSER_LABEL)
        self.profile_var = tk.StringVar()
        self.browser_session_helper_var = tk.StringVar(
            value="Off by default. No browser data is read."
        )
        self._browser_session_consent = False
        self._browser_session_locked = False
        self._active_browser_options: Dict = {}
        self._completed_output_path: Optional[str] = None
        self._last_output_warning = False

        self._build_theme()
        self._build_ui()
        self._update_environment_summary()

    # ------------------------------------------------------------------ UI setup
    def _build_theme(self):
        style = ttk.Style()
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        bg = "#1f1f1f"
        accent = "#ffbf00"
        style.configure(".", background=bg, foreground="#e5e5e5", fieldbackground="#2a2a2a")
        style.configure("TButton", padding=8)
        style.map("TButton", background=[("active", "#2f2f2f")])
        style.configure(
            "Green.Horizontal.TProgressbar",
            troughcolor="#0f0f0f",
            bordercolor="#0f0f0f",
            background="#2fa463",
            lightcolor="#2fa463",
            darkcolor="#1b6d3f",
        )
        self.root.configure(bg=bg)
        self.accent_color = accent

    def _build_ui(self):
        self.main_frame = ttk.Frame(self.root, padding=20)
        self.main_frame.pack(fill="both", expand=True)
        self.main_frame.columnconfigure(1, weight=1)

        ttk.Label(self.main_frame, text="YouTube video URL (single video only)").grid(row=0, column=0, sticky="w")
        self.url_entry = ttk.Entry(self.main_frame, textvariable=self.url_var, width=80)
        self.url_entry.grid(row=0, column=1, columnspan=2, sticky="ew", pady=5)

        ttk.Label(self.main_frame, text="Download Folder").grid(row=1, column=0, sticky="w")
        self.path_entry = ttk.Entry(self.main_frame, textvariable=self.path_var, width=60)
        self.path_entry.grid(row=1, column=1, sticky="ew", pady=5)
        ttk.Button(self.main_frame, text="Browse...", command=self._browse_directory).grid(row=1, column=2, sticky="ew", padx=(10, 0))

        ttk.Checkbutton(
            self.main_frame,
            text="Make output UniFi-compatible (MP4 / H.264 / AAC)",
            variable=self.unifi_compatible_var,
        ).grid(row=2, column=0, columnspan=3, sticky="w", pady=(2, 0))
        ttk.Label(
            self.main_frame,
            text="Off by default: preserve source codecs in a lossless container. Enable only for the bounded UniFi MP4 compatibility profile.",
            wraplength=680,
            justify="left",
        ).grid(row=3, column=0, columnspan=3, sticky="w")

        session_frame = ttk.LabelFrame(self.main_frame, text="Browser Session (optional)", padding=8)
        session_frame.grid(row=4, column=0, columnspan=3, sticky="ew", pady=(5, 0))
        session_frame.columnconfigure(1, weight=1)
        self.browser_session_checkbutton = ttk.Checkbutton(
            session_frame,
            text="Use browser session for this download",
            variable=self.browser_session_var,
            command=self._toggle_browser_session,
        )
        self.browser_session_checkbutton.grid(row=0, column=0, columnspan=2, sticky="w")
        ttk.Label(
            session_frame,
            textvariable=self.browser_session_helper_var,
            wraplength=680,
            justify="left",
        ).grid(row=1, column=0, columnspan=2, sticky="w", pady=(2, 5))
        ttk.Label(session_frame, text="Browser").grid(row=2, column=0, sticky="w", padx=(0, 10))
        self.browser_combobox = ttk.Combobox(
            session_frame,
            textvariable=self.browser_var,
            values=(SELECT_BROWSER_LABEL,) + SUPPORTED_BROWSERS,
            state="disabled",
        )
        self.browser_combobox.grid(row=2, column=1, sticky="ew", pady=2)
        self.browser_combobox.bind("<<ComboboxSelected>>", self._browser_selection_changed)
        ttk.Label(session_frame, text="Profile name or path (optional)").grid(
            row=3, column=0, sticky="w", padx=(0, 10)
        )
        self.profile_entry = ttk.Entry(session_frame, textvariable=self.profile_var, state="disabled")
        self.profile_entry.grid(row=3, column=1, sticky="ew", pady=2)
        ttk.Label(
            session_frame,
            text="Leave blank to use the most recently accessed profile.",
            wraplength=680,
            justify="left",
        ).grid(row=4, column=1, sticky="w", pady=(0, 2))

        button_frame = ttk.Frame(self.main_frame)
        button_frame.grid(row=5, column=0, columnspan=3, pady=10, sticky="ew")
        button_frame.columnconfigure((0, 1, 2, 3, 4), weight=1, uniform="btn")
        self.download_button = ttk.Button(button_frame, text="Start Download", command=self.start_download)
        self.download_button.grid(row=0, column=0, padx=5, sticky="ew")
        self.cancel_button = ttk.Button(button_frame, text="Cancel", command=self.cancel_download, state="disabled")
        self.cancel_button.grid(row=0, column=1, padx=5, sticky="ew")
        ttk.Button(button_frame, text="Test Environment", command=self._handle_test_environment).grid(row=0, column=2, padx=5, sticky="ew")
        ttk.Button(button_frame, text="Open Folder", command=self._open_download_directory).grid(row=0, column=3, padx=5, sticky="ew")
        self.open_browser_button = ttk.Button(
            button_frame,
            text="Open in Browser",
            command=self._open_completed_in_browser,
            state="disabled",
        )
        self.open_browser_button.grid(row=0, column=4, padx=5, sticky="ew")

        ttk.Label(self.main_frame, textvariable=self.stage_var, foreground=self.accent_color).grid(
            row=6, column=0, columnspan=3, sticky="w", pady=(10, 0)
        )

        self.progress_bar = ttk.Progressbar(
            self.main_frame, maximum=100, variable=self.progress_var, mode="determinate", style="Green.Horizontal.TProgressbar"
        )
        self.progress_bar.grid(row=7, column=0, columnspan=3, sticky="ew", pady=8)

        self.status_label = ttk.Label(
            self.main_frame,
            textvariable=self.status_var,
            font=("Segoe UI", 10, "bold"),
            wraplength=680,
            justify="left",
            anchor="w",
        )
        self.status_label.grid(row=8, column=0, columnspan=3, sticky="w")

        env_frame = ttk.LabelFrame(self.main_frame, text="Environment", padding=10)
        env_frame.grid(row=9, column=0, columnspan=3, sticky="ew", pady=10)
        self.env_label = ttk.Label(env_frame, textvariable=self.env_summary_var, justify="left")
        self.env_label.pack(fill="x")

        log_frame = ttk.LabelFrame(self.main_frame, text="Activity Log", padding=10)
        log_frame.grid(row=10, column=0, columnspan=3, sticky="nsew")
        self.main_frame.rowconfigure(10, weight=1)
        self.log_widget = tk.Text(
            log_frame,
            height=12,
            wrap="word",
            bg="#111111",
            fg="#e5e5e5",
            insertbackground="#ffffff",
            relief="flat",
        )
        self.log_widget.pack(fill="both", expand=True)
        self.log_widget.configure(state="disabled")
        self._update_browser_controls()

    # ------------------------------------------------------------------ UI helpers
    def _browse_directory(self):
        path = filedialog.askdirectory(initialdir=self.path_var.get() or os.path.expanduser("~"))
        if path:
            self.path_var.set(path)

    def _show_browser_session_consent(self) -> bool:
        """Show the safe-default disclosure before enabling browser-session access."""
        dialog = tk.Toplevel(self.root)
        dialog.title("Use browser session?")
        dialog.transient(self.root)
        dialog.resizable(False, False)
        result = [False]

        content = ttk.Frame(dialog, padding=16)
        content.pack(fill="both", expand=True)
        ttk.Label(content, text=BROWSER_SESSION_CONSENT_BODY, wraplength=580, justify="left").pack(fill="x")
        button_frame = ttk.Frame(content)
        button_frame.pack(fill="x", pady=(16, 0))

        def keep_off():
            result[0] = False
            dialog.destroy()

        def continue_with_session():
            result[0] = True
            dialog.destroy()

        keep_off_button = ttk.Button(
            button_frame, text="Keep Browser Session Off", command=keep_off
        )
        keep_off_button.pack(side="left")
        ttk.Button(
            button_frame,
            text="Continue with Browser Session",
            command=continue_with_session,
        ).pack(side="right")
        dialog.protocol("WM_DELETE_WINDOW", keep_off)
        dialog.bind("<Escape>", lambda _event: keep_off())
        keep_off_button.focus_set()
        dialog.grab_set()
        self.root.wait_window(dialog)
        return result[0]

    def _toggle_browser_session(self):
        if self._browser_session_locked:
            return
        if self.browser_session_var.get():
            if not self._browser_session_consent:
                if self._show_browser_session_consent():
                    self._browser_session_consent = True
                else:
                    self.browser_session_var.set(False)
                    self.browser_session_helper_var.set("Off by default. No browser data is read.")
                    self._set_status("Browser session remains off. No browser data was read.")
        else:
            self._browser_session_consent = False
        self._update_browser_controls()

    def _browser_selection_changed(self, _event=None):
        self._update_download_button_state()

    def _update_download_button_state(self):
        if not hasattr(self, "download_button"):
            return
        worker = getattr(self, "worker_thread", None)
        if worker is not None and worker.is_alive():
            return
        session_on = bool(self.browser_session_var.get())
        browser_selected = self.browser_var.get() not in ("", SELECT_BROWSER_LABEL)
        state = "disabled" if session_on and not browser_selected else "normal"
        self.download_button.configure(state=state)

    def _update_browser_controls(self):
        if not hasattr(self, "browser_combobox"):
            return
        session_on = bool(self.browser_session_var.get())
        enabled = session_on and self._browser_session_consent and not self._browser_session_locked
        self.browser_combobox.configure(state="readonly" if enabled else "disabled")
        self.profile_entry.configure(state="normal" if enabled else "disabled")
        if self._browser_session_locked:
            self.browser_session_helper_var.set(
                "Using the selected browser session for this download."
            )
        elif session_on and self._browser_session_consent:
            self.browser_session_helper_var.set(
                "On for this download only. The app reads the selected local browser session "
                "through yt-dlp; it does not save or upload browser data."
            )
        elif session_on:
            self.browser_session_helper_var.set(
                "Choose a browser. This permission applies to this download only."
            )
        else:
            self.browser_session_helper_var.set("Off by default. No browser data is read.")
        self.browser_session_checkbutton.configure(
            state="disabled" if self._browser_session_locked else "normal"
        )
        self._update_download_button_state()

    def _browser_options_for_run(self) -> Dict:
        if not self.browser_session_var.get():
            return {}
        if not self._browser_session_consent:
            raise ValueError("Confirm browser-session access before starting the download.")
        profile = self.profile_var.get()
        return build_browser_session_options(self.browser_var.get(), profile)

    def _lock_browser_session_controls(self):
        self._browser_session_locked = True
        self._update_browser_controls()

    def _reset_browser_session_controls(self):
        """Clear per-run session consent and all in-memory browser selections."""
        self._browser_session_consent = False
        self._browser_session_locked = False
        if hasattr(self, "browser_session_var"):
            self.browser_session_var.set(False)
            self.browser_var.set(SELECT_BROWSER_LABEL)
            self.profile_var.set("")
            self._update_browser_controls()
        self._active_browser_options = {}

    def _clear_completed_output(self):
        self._completed_output_path = None
        if hasattr(self, "open_browser_button"):
            self.open_browser_button.configure(state="disabled")

    def _set_completed_output(self, output_path: str):
        """Enable Open in Browser only after re-reading the published file."""
        def publish_if_verified():
            if is_verified_output(output_path):
                self._completed_output_path = output_path
                if hasattr(self, "open_browser_button"):
                    self.open_browser_button.configure(state="normal")
            else:
                self._clear_completed_output()

        self.root.after(0, publish_if_verified)

    def _open_completed_in_browser(self):
        output_path = self._completed_output_path
        if not output_path or not is_verified_output(output_path):
            self._clear_completed_output()
            self._set_status(f"Error: {OPEN_BROWSER_MISSING_MESSAGE}", "#ff6b6b")
            messagebox.showerror("Open in Browser", OPEN_BROWSER_MISSING_MESSAGE)
            return
        try:
            uri = local_file_uri(output_path)
            if not webbrowser.open(uri):
                raise OSError("default browser did not accept the file URI")
        except Exception:
            self._set_status(f"Error: {OPEN_BROWSER_FAILURE_MESSAGE}", "#ff6b6b")
            messagebox.showerror("Open in Browser", OPEN_BROWSER_FAILURE_MESSAGE)
            return
        filename = os.path.basename(output_path)
        self._set_status(f"Opened {filename} in your default browser.", "#4dcc7d")

    def _set_stage(self, text: str):
        safe_text = sanitize_child_output(text)
        self.root.after(0, lambda: self.stage_var.set(safe_text))

    def _set_status(self, text: str, color: str = "#e5e5e5"):
        text = sanitize_child_output(text)
        def _update():
            self.status_var.set(text)
            self.status_label.configure(foreground=color)

        self.root.after(0, _update)

    def _set_progress(self, value: Optional[int]):
        if value is None:
            self.root.after(0, self._show_indeterminate_progress)
            return
        value = max(0, min(100, value))
        self.root.after(0, lambda: self._show_determinate_progress(value))

    def _show_indeterminate_progress(self):
        self.progress_bar.stop()
        self.progress_bar.configure(mode="indeterminate")
        self.progress_bar.start(12)

    def _show_determinate_progress(self, value: int):
        self.progress_bar.stop()
        self.progress_bar.configure(mode="determinate")
        self.progress_var.set(value)

    def _append_log(self, message: str):
        message = sanitize_child_output(message)
        timestamp = time.strftime("%H:%M:%S")
        entry = f"[{timestamp}] {message}\n"
        self.root.after(0, lambda: self._write_log(entry))

    def _write_log(self, text: str):
        self.log_widget.configure(state="normal")
        self.log_widget.insert("end", text)
        self.log_widget.see("end")
        self.log_widget.configure(state="disabled")

    def _reset_controls(self, *, retry: bool = False):
        self.download_button.configure(
            text="Try Again" if retry else "Start Download",
            state="normal",
        )
        self.cancel_button.configure(text="Cancel", state="disabled")
        if retry:
            self.download_button.focus_set()

    def _handle_test_environment(self):
        self.env_status = probe_environment()
        self._update_environment_summary()
        if self.env_status.issues:
            messagebox.showwarning("Environment Check", "\n".join(self.env_status.issues))
        else:
            messagebox.showinfo(
                "Environment Check",
                "yt-dlp EJS, Deno, FFmpeg, and FFprobe detected.\nNVENC available: yes" if self.env_status.nvenc_available else "yt-dlp EJS, Deno, FFmpeg, and FFprobe ready (CPU encoding mode).",
            )

    def _update_environment_summary(self):
        if not self.env_status.yt_dlp_available:
            summary = "yt-dlp: missing\n"
        else:
            version = self.env_status.yt_dlp_version or "unknown version"
            summary = f"yt-dlp: {version}\n"
        summary += f"yt-dlp EJS scripts: {'available' if self.env_status.ejs_available else 'missing'}\n"
        deno_label = self.env_status.deno_version or "missing/unparseable"
        summary += f"Deno: {deno_label} ({'ready' if self.env_status.deno_ready else 'not ready'})\n"
        if not self.env_status.ffmpeg_path:
            summary += "FFmpeg: missing\n"
        else:
            summary += f"FFmpeg: {self.env_status.ffmpeg_path}\n"
        if not self.env_status.ffprobe_path:
            summary += "FFprobe: missing\n"
        else:
            summary += f"FFprobe: {self.env_status.ffprobe_path}\n"
        summary += f"NVENC available: {'Yes' if self.env_status.nvenc_available else 'No'}"
        if self.env_status.issues:
            summary += "\nIssues:\n- " + "\n- ".join(self.env_status.issues)
        self.env_summary_var.set(summary)

    def _open_download_directory(self):
        directory = self.path_var.get().strip()
        if not directory:
            messagebox.showinfo("Open Folder", "Choose a download directory first.")
            return
        if not os.path.exists(directory):
            os.makedirs(directory, exist_ok=True)
        try:
            if os.name == "nt":
                os.startfile(directory)
            elif sys.platform == "darwin":
                subprocess.Popen(["open", directory])
            else:
                subprocess.Popen(["xdg-open", directory])
        except Exception as exc:
            messagebox.showerror("Open Folder", f"Unable to open directory: {exc}")

    # ------------------------------------------------------------------ Actions
    def start_download(self):
        if self.worker_thread and self.worker_thread.is_alive():
            messagebox.showinfo("Download running", "Please wait for the current download to finish or cancel it.")
            return

        url = self.url_var.get().strip()
        destination = self.path_var.get().strip()
        if not url:
            self._set_status("Enter a single-video URL.", "#ff6b6b")
            return
        if not destination:
            self._set_status("Select a download directory.", "#ff6b6b")
            return
        if not self.env_status.yt_dlp_available:
            self._set_status(
                "yt-dlp is required. Install the requirements, then choose Test Environment.",
                "#ff6b6b",
            )
            return
        if not self.env_status.ejs_available or not self.env_status.deno_ready:
            self._set_status(
                "yt-dlp EJS scripts and Deno 2.3.0 or newer are required. Install yt-dlp[default] and a supported local Deno runtime, then choose Test Environment.",
                "#ff6b6b",
            )
            return
        if not self.env_status.ffmpeg_path:
            self._set_status(
                "FFmpeg is required to create the output file. Install FFmpeg, then choose Test Environment.",
                "#ff6b6b",
            )
            return
        try:
            os.makedirs(destination, exist_ok=True)
            if not os.path.isdir(destination):
                raise NotADirectoryError(destination)
        except OSError as exc:
            self._set_status("Unable to use the selected download directory.", "#ff6b6b")
            self._append_log(f"Input path error: {safe_error_detail(exc)}")
            return

        try:
            browser_options = self._browser_options_for_run()
        except ValueError as exc:
            self._set_status(f"Error: {str(exc)}", "#ff6b6b")
            if self.browser_session_var.get() and hasattr(self, "browser_combobox"):
                self.browser_combobox.focus_set()
            return

        self._clear_completed_output()
        self._active_browser_options = dict(browser_options)
        self._lock_browser_session_controls()
        self.cancel_requested = False
        self._last_output_warning = False
        self._set_status(
            "Using the selected browser session for this download."
            if browser_options
            else "Preparing download...",
            self.accent_color,
        )
        self._set_stage("Initializing (phase 1 of 4)")
        self._set_progress(None)
        self.download_button.configure(text="Start Download", state="disabled")
        self.cancel_button.configure(state="normal")

        self.worker_thread = threading.Thread(
            target=self._run_pipeline, args=(url, destination), daemon=True
        )
        self.worker_thread.start()

    def cancel_download(self):
        if not self.worker_thread or not self.worker_thread.is_alive():
            return
        self.cancel_requested = True
        self._set_status("Cancellation requested...", "#ffbf00")
        self._append_log("Cancellation requested by user.")
        self.cancel_button.configure(text="Stopping...", state="disabled")
        if self.active_process and self.active_process.poll() is None:
            self.active_process.terminate()

    # ------------------------------------------------------------------ Pipeline
    def _run_pipeline(self, url: str, destination: str):
        temp_dir = tempfile.mkdtemp(prefix="unifi_dl_")
        final_status: Optional[str] = None
        final_color = "#ff6b6b"
        retry = False
        published_output = False
        try:
            video_path, audio_path, metadata, video_fmt, audio_fmt = self._download_media(url, temp_dir)
            if self.cancel_requested:
                raise DownloadCancelled()
            output_path = self._transcode_and_mux(
                video_path, audio_path, metadata, destination, video_fmt, audio_fmt
            )
            if not is_verified_output(output_path):
                raise PipelineError(
                    "publication",
                    "Couldn't verify the completed output. Try the download again.",
                    "Final output verification failed.",
                )
            published_output = True
            self._set_completed_output(output_path)
            final_status, final_color = completion_status(
                output_path, oversized=getattr(self, "_last_output_warning", False)
            )
            if self.cancel_requested:
                final_status += " (Cancellation arrived after finalization; output preserved.)"
                final_color = "#ffbf00"
                self._append_log("Cancellation arrived after final output was published; output preserved.")
            self._set_status(final_status, final_color)
            self._append_log(f"Saved final file to {output_path}")
        except DownloadCancelled:
            final_status = "Download cancelled. No completed file was saved."
            final_color = "#ff6b6b"
            self._set_stage("Cancelled")
            self._set_status(final_status, final_color)
            self._append_log("Operation cancelled.")
        except PipelineError as exc:
            retry = True
            final_status = f"Error: {exc.user_message}"
            final_color = "#ff6b6b"
            self._set_stage("Failed")
            self._set_status(final_status, final_color)
            self._append_log(f"Failed during {exc.stage}: {exc.detail}")
            if isinstance(exc, BrowserSessionError):
                self.root.after(
                    0,
                    lambda message=exc.user_message: messagebox.showerror(
                        "Browser session unavailable", message
                    ),
                )
        except Exception as exc:
            retry = True
            detail = safe_error_detail(exc)
            final_status = "Error: The download could not be completed. Try again."
            final_color = "#ff6b6b"
            self._set_stage("Failed")
            self._set_status(final_status, final_color)
            self._append_log(f"Unexpected pipeline error: {detail}")
        finally:
            try:
                shutil.rmtree(temp_dir)
            except OSError as exc:
                self._append_log(f"Cleanup warning: {safe_error_detail(exc)}")
                if final_status:
                    final_status = (
                        f"{final_status} Warning: temporary files could not be removed; "
                        "leftover temporary files may remain on disk."
                    )
                    self._set_status(
                        final_status,
                        "#ffbf00" if final_color == "#4dcc7d" else final_color,
                    )
            if not published_output:
                self.root.after(0, self._clear_completed_output)
            self.root.after(0, lambda: self._reset_controls(retry=retry))
            self.root.after(0, self._reset_browser_session_controls)

    def _check_cancelled(self):
        if self.cancel_requested:
            raise DownloadCancelled()

    def _wait_before_retry(self, seconds: float):
        self._set_stage(f"Waiting to retry in {int(seconds)} seconds")
        self._set_status("Waiting to retry. Cancel to stop.", "#ffbf00")
        deadline = time.monotonic() + seconds
        while True:
            self._check_cancelled()
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                return
            time.sleep(min(0.1, remaining))

    def _cleanup_temp_files(self, temp_dir: str):
        for name in os.listdir(temp_dir):
            path = os.path.join(temp_dir, name)
            try:
                if os.path.isdir(path) and not os.path.islink(path):
                    shutil.rmtree(path)
                else:
                    os.unlink(path)
            except FileNotFoundError:
                continue

    def _resolve_media(self, url: str) -> Tuple[Dict, Dict, Dict]:
        self._check_cancelled()
        self._set_stage("Fetching metadata (phase 2 of 4)")
        self._append_log("Fetching video information...")
        if yt_dlp is None:
            self._active_browser_options = {}
            raise PipelineError(
                "metadata",
                "yt-dlp is required. Install the requirements, then try again.",
                safe_error_detail(YT_DLP_IMPORT_ERROR or ImportError("yt-dlp is unavailable")),
            )
        ydl_opts = {
            "quiet": True,
            "skip_download": True,
            "noplaylist": False,
        }
        ydl_opts.update(getattr(self, "_active_browser_options", {}))
        converted_error: Optional[PipelineError] = None
        ydl = None
        try:
            with yt_dlp.YoutubeDL(ydl_opts) as ydl:
                metadata = ydl.extract_info(url, download=False)
            if is_playlist_metadata(metadata):
                raise PipelineError(
                    "metadata",
                    "Playlist URLs are not supported. Enter a single-video URL.",
                    "Extractor returned playlist metadata for a single-video-only request.",
                )
            best_video, best_audio = pick_best_formats(metadata)
        except DownloadCancelled:
            self._active_browser_options = {}
            raise
        except PipelineError:
            self._active_browser_options = {}
            raise
        except Exception as exc:
            status_code = ytdlp_status_code(exc)
            raw_detail = str(exc)
            if getattr(self, "_active_browser_options", {}):
                if status_code == 403:
                    converted_error = PipelineError(
                        "metadata",
                        BROWSER_SESSION_ACCESS_MESSAGE,
                        "Browser session did not grant metadata access.",
                    )
                elif is_browser_setup_error(raw_detail):
                    user_message = (
                        BROWSER_SESSION_DECRYPTION_MESSAGE
                        if any(
                            marker in raw_detail.casefold()
                            for marker in ("decrypt", "keyring")
                        )
                        else BROWSER_SESSION_UNAVAILABLE_MESSAGE
                    )
                    converted_error = BrowserSessionError("metadata", user_message)
                else:
                    # Keep ordinary network/access classification, but do not put the
                    # browser/session diagnostic in the activity log.
                    detail = "Metadata request failed while using the selected browser session."
                    converted_error = PipelineError(
                        "metadata",
                        metadata_error_message(status_code),
                        detail,
                    )
            else:
                converted_error = PipelineError(
                    "metadata",
                    metadata_error_message(status_code),
                    safe_error_detail(exc),
                )
            # Do not leave the unsanitized extractor message in the traceback
            # frame's locals when the deferred error is raised below.
            raw_detail = ""
        finally:
            # The deferred sanitized raise below keeps this frame in the traceback.
            # Release every yt-dlp option-bearing value before that can happen.
            ydl = None
            ydl_opts.clear()
        if converted_error is not None:
            if not converted_error.retryable:
                # No caller retry needs the browser session for terminal metadata/setup
                # errors. Clearing the shared state also keeps it out of nested frame
                # locals when a traceback inspector follows ``self``.
                self._active_browser_options = {}
            raise converted_error
        if best_video.get("format_id") == best_audio.get("format_id"):
            self._append_log(
                f"Selected progressive format {best_video.get('format_id')} "
                f"({best_video.get('height')}p; muxed video and audio)."
            )
        else:
            self._append_log(
                f"Selected video {best_video.get('format_id')} ({best_video.get('height')}p) "
                f"and audio {best_audio.get('format_id')} ({best_audio.get('abr') or best_audio.get('tbr')} kbps)."
            )
        return metadata, best_video, best_audio

    def _download_media(self, url: str, temp_dir: str) -> Tuple[str, str, Dict, Dict, Dict]:
        last_error: Optional[PipelineError] = None
        for attempt in range(1, MAX_DOWNLOAD_ATTEMPTS + 1):
            if attempt > 1:
                delay = RETRY_BACKOFF_SECONDS[min(attempt - 2, len(RETRY_BACKOFF_SECONDS) - 1)]
                self._wait_before_retry(delay)
            self._check_cancelled()
            try:
                metadata, best_video, best_audio = self._resolve_media(url)
                total_duration = metadata.get("duration") or 0
                estimated_size = total_duration * (TARGET_VIDEO_BITRATE + TARGET_AUDIO_BITRATE) / 8
                if estimated_size > MAX_FILE_BYTES:
                    self._append_log(
                        f"Warning: estimated size {human_readable_size(estimated_size)} exceeds 5 GB limit."
                    )
                video_path = self._download_stream(url, best_video["format_id"], temp_dir, "video")
                if best_video.get("format_id") == best_audio.get("format_id"):
                    audio_path = video_path
                else:
                    audio_path = self._download_stream(url, best_audio["format_id"], temp_dir, "audio")
                return video_path, audio_path, metadata, best_video, best_audio
            except DownloadCancelled:
                self._active_browser_options = {}
                raise
            except PipelineError as exc:
                last_error = exc
                self._cleanup_temp_files(temp_dir)
                if not exc.retryable or attempt >= MAX_DOWNLOAD_ATTEMPTS:
                    # A retryable stream error keeps the explicit browser options until
                    # the next attempt. Once no attempt remains, release them before
                    # re-raising so traceback inspection cannot reach selected state via
                    # this frame's ``self`` local.
                    self._active_browser_options = {}
                    raise
                self._append_log(
                    f"{exc.stage.capitalize()} failed; refreshing stream details "
                    f"(attempt {attempt + 1} of {MAX_DOWNLOAD_ATTEMPTS})."
                )
                if isinstance(exc, StreamDownloadError) and exc.status_code == 403:
                    retry_status = (
                        f"{exc.label.capitalize()} stream access was refused (HTTP 403). "
                        "Trying one bounded, best-effort refresh of stream details; "
                        "this cannot bypass access or service restrictions..."
                    )
                else:
                    retry_status = (
                        "Download interrupted. Retrying once with fresh stream details "
                        f"(attempt {attempt + 1} of {MAX_DOWNLOAD_ATTEMPTS})..."
                    )
                self._set_status(retry_status, "#ffbf00")
        raise last_error or RuntimeError("Download failed without a classified error.")

    def _download_stream(self, url: str, format_id: str, temp_dir: str, label: str) -> str:
        self._check_cancelled()

        target_template = os.path.join(temp_dir, f"{label}.%(ext)s")
        downloaded = {"path": None}
        progress_mode: Optional[str] = None

        def hook(d):
            nonlocal progress_mode
            if self.cancel_requested:
                raise DownloadCancelled()
            status = d.get("status")
            if status == "downloading":
                downloaded_bytes = d.get("downloaded_bytes", 0)
                total_bytes = d.get("total_bytes") or d.get("total_bytes_estimate") or 0
                speed = d.get("speed")
                if speed:
                    speed_mbps = speed * 8 / 1_000_000
                    stage = f"Downloading {label} stream @ {speed_mbps:.1f} Mbps"
                else:
                    stage = f"Downloading {label} stream"
                if total_bytes:
                    progress_mode = "determinate"
                    percent = int(downloaded_bytes / total_bytes * 100)
                    self._set_stage(stage)
                    self._set_progress(percent)
                else:
                    self._set_stage(f"{stage} (progress unavailable)")
                    if progress_mode != "indeterminate":
                        self._set_progress(None)
                        progress_mode = "indeterminate"
            elif status == "finished":
                downloaded["path"] = d.get("filename")
                self._append_log(f"{label.capitalize()} download finished: {downloaded['path']}")

        if yt_dlp is None:
            self._active_browser_options = {}
            raise PipelineError(
                f"{label} download",
                "yt-dlp is required. Install the requirements, then try again.",
                safe_error_detail(YT_DLP_IMPORT_ERROR or ImportError("yt-dlp is unavailable")),
            )
        ydl_opts = {
            "quiet": True,
            "noplaylist": True,
            "format": format_id,
            "outtmpl": target_template,
            "progress_hooks": [hook],
            "windowsfilenames": True,
            "retries": 1,
            "fragment_retries": 1,
            "socket_timeout": 30,
            "max_filesize": MAX_FILE_BYTES,
        }
        ydl_opts.update(getattr(self, "_active_browser_options", {}))
        converted_error: Optional[PipelineError] = None
        try:
            with yt_dlp.YoutubeDL(ydl_opts) as ydl:
                ydl.download([url])
        except DownloadCancelled:
            self._active_browser_options = {}
            raise
        except Exception as exc:
            status_code = ytdlp_status_code(exc)
            raw_detail = str(exc)
            if getattr(self, "_active_browser_options", {}):
                if status_code == 403:
                    error = StreamDownloadError(
                        label,
                        "Browser session did not grant stream access.",
                        status_code=status_code,
                    )
                    error.user_message = BROWSER_SESSION_ACCESS_MESSAGE
                    converted_error = error
                elif is_browser_setup_error(raw_detail):
                    user_message = (
                        BROWSER_SESSION_DECRYPTION_MESSAGE
                        if any(
                            marker in raw_detail.casefold()
                            for marker in ("decrypt", "keyring")
                        )
                        else BROWSER_SESSION_UNAVAILABLE_MESSAGE
                    )
                    converted_error = BrowserSessionError(f"{label} download", user_message)
                else:
                    detail = f"{label.capitalize()} request failed while using the selected browser session."
                    converted_error = StreamDownloadError(label, detail, status_code=status_code)
            else:
                converted_error = StreamDownloadError(
                    label, safe_error_detail(exc), status_code=status_code
                )
            # Do not leave the unsanitized extractor message in the traceback
            # frame's locals when the deferred error is raised below.
            raw_detail = ""
        finally:
            # Keep the option-bearing locals empty before any converted error is raised.
            ydl = None
            ydl_opts.clear()
        if converted_error is not None:
            if not converted_error.retryable:
                self._active_browser_options = {}
            raise converted_error

        file_path = downloaded["path"]
        if not file_path or not os.path.isfile(file_path):
            raise StreamDownloadError(label, f"yt-dlp did not produce a {label} file.")
        return file_path

    def _execute_ffmpeg(
        self,
        cmd: list[str],
        stage_label: str,
        duration: Optional[float],
        start_progress: int,
        end_progress: int,
    ):
        try:
            process = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                universal_newlines=True,
                encoding="utf-8",
                errors="replace",
            )
        except OSError as exc:
            raise RuntimeError(f"Unable to start FFmpeg during {stage_label.lower()}.") from exc
        self.active_process = process
        output_queue: queue.Queue[Optional[str]] = queue.Queue()

        def read_output():
            try:
                for line in process.stdout or ():
                    output_queue.put(line)
            except (OSError, ValueError):
                # The cancellation cleanup may close a pipe while this reader exits.
                pass
            finally:
                output_queue.put(None)

        reader = threading.Thread(target=read_output, name="ffmpeg-output-reader", daemon=True)
        reader.start()
        last_log = 0.0
        if duration:
            self._set_progress(start_progress)
        else:
            self._set_stage(f"{stage_label} (progress unavailable)")
            self._set_progress(None)
        try:
            while True:
                if self.cancel_requested:
                    self._stop_ffmpeg_process(process)
                    raise DownloadCancelled()
                try:
                    line = output_queue.get(timeout=FFMPEG_READER_POLL_SECONDS)
                except queue.Empty:
                    continue
                if line is None:
                    break
                line = sanitize_child_output(line or "").strip()
                if not line:
                    continue
                timestamp = parse_ffmpeg_time(line) if "time=" in line else None
                if timestamp is not None and duration:
                    fraction = min(1.0, timestamp / duration)
                    progress_value = int(start_progress + fraction * (end_progress - start_progress))
                    self._set_progress(progress_value)
                bitrate_match = re.search(r"bitrate=\s*([\d.]+)kbits/s", line)
                if bitrate_match:
                    bitrate_mbps = float(bitrate_match.group(1)) / 1000
                    self._set_stage(f"{stage_label} @ {bitrate_mbps:.1f} Mbps")
                now = time.time()
                if now - last_log >= 0.9 or "error" in line.lower():
                    self._append_log(f"{stage_label}: {line}")
                    last_log = now
        finally:
            if self.cancel_requested:
                self._stop_ffmpeg_process(process)
            reader.join(timeout=FFMPEG_CANCEL_GRACE_SECONDS)
            if reader.is_alive() and process.stdout is not None:
                process.stdout.close()
                reader.join(timeout=FFMPEG_CANCEL_GRACE_SECONDS)
            elif process.stdout is not None:
                process.stdout.close()
            self.active_process = None

        return_code = process.wait()
        if self.cancel_requested:
            raise DownloadCancelled()
        if return_code != 0:
            raise RuntimeError(f"FFmpeg failed during {stage_label.lower()} (exit code {return_code}).")
        self._set_progress(end_progress)

    @staticmethod
    def _stop_ffmpeg_process(process: subprocess.Popen):
        """Stop FFmpeg promptly, escalating when it ignores a graceful signal."""
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=FFMPEG_CANCEL_GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=FFMPEG_CANCEL_GRACE_SECONDS)

    def _remux_passthrough(
        self, video_path: str, audio_path: str, output_path: str, duration: Optional[float]
    ):
        ffmpeg_bin = self.env_status.ffmpeg_path or "ffmpeg"
        cmd = [ffmpeg_bin, "-y", "-i", video_path]
        if video_path != audio_path:
            cmd += ["-i", audio_path]
        cmd += [
            "-map", "0:v:0", "-map", "0:a:0" if video_path == audio_path else "1:a:0",
            "-c:v",
            "copy",
            "-c:a",
            "copy",
            "-movflags",
            "+faststart",
            output_path,
        ]
        self._execute_ffmpeg(
            cmd,
            stage_label="Remuxing",
            duration=duration or None,
            start_progress=70,
            end_progress=95,
        )

    def _create_staging_output(self, destination: str, extension: str = ".mp4") -> str:
        os.makedirs(destination, exist_ok=True)
        fd, path = tempfile.mkstemp(prefix=".youtube_dl_", suffix=extension, dir=destination)
        os.close(fd)
        os.unlink(path)
        return path

    def _finalize_output(self, staged_path: str, destination: str, base_name: str) -> str:
        """Publish a completed staged file without overwriting an existing output."""
        while True:
            output_path = ensure_unique_path(destination, base_name, Path(staged_path).suffix)
            try:
                os.link(staged_path, output_path)
            except FileExistsError:
                continue
            try:
                os.unlink(staged_path)
            except OSError as exc:
                self._append_log(f"Output staging cleanup warning: {safe_error_detail(exc)}")
            if not os.path.isfile(output_path) or os.path.getsize(output_path) <= 0:
                raise RuntimeError("Final output could not be verified after publishing.")
            return output_path

    def _transcode_and_mux(
        self,
        video_path: str,
        audio_path: str,
        metadata: Dict,
        destination: str,
        video_fmt: Dict,
        audio_fmt: Dict,
    ) -> str:
        source_title = metadata.get("title") or "YouTube Video"
        safe_name = sanitize_filename(source_title)
        staged_path: Optional[str] = None
        self._last_output_warning = False
        try:
            compatibility = bool(getattr(self, "unifi_compatible_var", False) and self.unifi_compatible_var.get())
            source_video_codec = (video_fmt.get("vcodec") or "").lower()
            source_audio_codec = (audio_fmt.get("acodec") or "").lower()
            source_extension = (video_fmt.get("ext") or "").lower()
            generic_mp4 = (
                source_extension in GENERIC_CONTAINER_EXTENSIONS and
                source_video_codec.startswith(("avc", "h264")) and
                source_audio_codec.startswith(("mp4a", "aac")) and
                source_extension in {"mp4", "m4v"}
            )
            staged_path = self._create_staging_output(destination, ".mp4" if (compatibility or generic_mp4) else ".mkv")
            duration = metadata.get("duration") or 0
            if not compatibility:
                self._append_log("Combining selected streams without lossy re-encoding.")
                self._set_stage("Combining streams (no re-encode)")
                self._set_progress(70)
                self._remux_passthrough(video_path, audio_path, staged_path, duration)
            elif should_passthrough(video_fmt, audio_fmt):
                self._append_log("Stream already meets UniFi compatibility requirements. Remuxing without re-encode.")
                self._set_stage("Remuxing (no transcode needed)")
                self._set_progress(70)
                self._remux_passthrough(video_path, audio_path, staged_path, duration)
            else:
                source_fps = video_fmt.get("fps") or metadata.get("fps") or metadata.get("average_fps")
                target_fps = choose_target_fps(source_fps)
                needs_fps_change = not source_fps or round(source_fps) not in ALLOWED_FPS

                video_codec = "h264_nvenc" if self.env_status.nvenc_available else "libx264"
                encoder_note = "NVENC p4" if video_codec == "h264_nvenc" else "x264 medium"
                self._append_log(
                    f"Transcoding to Unifi profile ({encoder_note}, target {target_fps} FPS, 40 Mbps video / 192 kbps audio)."
                )
                self._set_stage("Transcoding and packaging")
                self._set_progress(15)

                ffmpeg_bin = self.env_status.ffmpeg_path or "ffmpeg"
                start_args = [
                    ffmpeg_bin,
                    "-y",
                    "-i",
                    video_path,
                    "-i",
                    audio_path,
                    "-map",
                    "0:v:0",
                    "-map",
                    "1:a:0",
                ]

                def build_cmd(codec: str) -> list[str]:
                    cmd = start_args + ["-c:v", codec]
                    if codec == "h264_nvenc":
                        cmd += [
                            "-preset",
                            "p4",
                            "-rc:v",
                            "vbr_hq",
                            "-cq",
                            "19",
                            "-b:v",
                            f"{TARGET_VIDEO_BITRATE}",
                            "-maxrate",
                            f"{int(TARGET_VIDEO_BITRATE * 1.15)}",
                            "-bufsize",
                            f"{TARGET_VIDEO_BITRATE * 2}",
                        ]
                    else:
                        cmd += [
                            "-preset",
                            "medium",
                            "-crf",
                            "18",
                            "-b:v",
                            f"{TARGET_VIDEO_BITRATE}",
                            "-maxrate",
                            f"{int(TARGET_VIDEO_BITRATE * 1.15)}",
                            "-bufsize",
                            f"{TARGET_VIDEO_BITRATE * 2}",
                        ]

                    cmd += ["-profile:v", "high", "-pix_fmt", "yuv420p"]
                    if needs_fps_change:
                        cmd += ["-r", str(target_fps)]
                    return cmd + [
                        "-c:a",
                        "aac",
                        "-b:a",
                        f"{TARGET_AUDIO_BITRATE}",
                        "-ac",
                        "2",
                        "-movflags",
                        "+faststart",
                        staged_path,
                    ]

                cmd = build_cmd(video_codec)
                try:
                    self._execute_ffmpeg(
                        cmd,
                        stage_label="Transcoding",
                        duration=duration or None,
                        start_progress=20,
                        end_progress=98,
                    )
                except RuntimeError as exc:
                    if video_codec != "h264_nvenc":
                        raise
                    self._append_log(f"NVENC failed ({safe_error_detail(exc)}). Retrying with CPU x264.")
                    self._set_status("NVENC unavailable. Retrying on CPU...", "#ffbf00")
                    fallback_cmd = build_cmd("libx264")
                    self._execute_ffmpeg(
                        fallback_cmd,
                        stage_label="CPU Transcoding",
                        duration=duration or None,
                        start_progress=20,
                        end_progress=98,
                    )

            if not staged_path or not os.path.isfile(staged_path) or os.path.getsize(staged_path) <= 0:
                raise RuntimeError("FFmpeg did not produce a non-empty output file.")
            final_size = os.path.getsize(staged_path)
            self._last_output_warning = final_size > MAX_FILE_BYTES
            if self._last_output_warning:
                self._append_log(
                    f"Warning: Final file is {human_readable_size(final_size)}, which exceeds the UniFi compatibility 5 GB limit."
                )
            else:
                self._append_log(f"Final file size: {human_readable_size(final_size)}")
            output_path = self._finalize_output(staged_path, destination, safe_name)
            self._set_progress(100)
            return output_path
        except DownloadCancelled:
            raise
        except PipelineError:
            raise
        except Exception as exc:
            raise PipelineError(
                "FFmpeg",
                "Couldn't create the output file. Check the FFmpeg setup and available disk space, then try again.",
                safe_error_detail(exc),
            ) from exc
        finally:
            if staged_path and os.path.exists(staged_path):
                try:
                    os.unlink(staged_path)
                except OSError as exc:
                    self._append_log(f"Output cleanup warning: {safe_error_detail(exc)}")


if __name__ == "__main__":
    root = tk.Tk()
    app = YouTubeDownloaderApp(root)
    root.mainloop()
