from __future__ import annotations

import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import tkinter as tk
from dataclasses import dataclass, field
from tkinter import ttk, filedialog, messagebox
from typing import Dict, Optional, Tuple

import yt_dlp

ALLOWED_FPS = {24, 25, 30}
TARGET_VIDEO_BITRATE = 40_000_000  # bits per second (40 Mbps)
TARGET_AUDIO_BITRATE = 192_000  # bits per second
MAX_FILE_BYTES = 5 * 1024 * 1024 * 1024  # 5 GB


class DownloadCancelled(Exception):
    """Raised when the user cancels an in-progress download/transcode."""


@dataclass
class EnvironmentStatus:
    ffmpeg_path: Optional[str] = None
    ffprobe_path: Optional[str] = None
    nvenc_available: bool = False
    issues: list[str] = field(default_factory=list)


def sanitize_filename(name: str) -> str:
    """Generate a filesystem-friendly filename."""
    name = re.sub(r"[<>:\"/\\|?*]", "", name)
    name = re.sub(r"\s+", " ", name).strip()
    return name or "video"


def human_readable_size(num_bytes: float) -> str:
    """Convert bytes to a friendly string."""
    if num_bytes <= 0:
        return "0 B"
    units = ["B", "KB", "MB", "GB", "TB"]
    power = min(int(math.log(num_bytes, 1024)), len(units) - 1)
    return f"{num_bytes / (1024 ** power):.2f} {units[power]}"


def probe_environment() -> EnvironmentStatus:
    """Detect ffmpeg/ffprobe availability and NVENC support."""
    status = EnvironmentStatus()
    status.ffmpeg_path = shutil.which("ffmpeg")
    status.ffprobe_path = shutil.which("ffprobe")
    if not status.ffmpeg_path:
        status.issues.append("FFmpeg not found on PATH.")
    if not status.ffprobe_path:
        status.issues.append("FFprobe not found on PATH.")
    if status.ffmpeg_path:
        try:
            result = subprocess.run(
                [status.ffmpeg_path, "-hide_banner", "-encoders"],
                capture_output=True,
                text=True,
                check=False,
            )
            status.nvenc_available = "h264_nvenc" in result.stdout
        except Exception as exc:
            status.issues.append(f"Unable to query FFmpeg encoders: {exc}")
    return status


def choose_target_fps(source_fps: Optional[float]) -> int:
    """Pick an allowed frame rate, defaulting to 30 when unsure."""
    if source_fps:
        rounded = round(source_fps)
        if rounded in ALLOWED_FPS:
            return rounded
    return 30


def pick_best_formats(info: Dict) -> Tuple[Dict, Dict]:
    """Determine the best available video-only and audio-only formats."""
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

    if not video_candidates:
        video_candidates = [
            fmt for fmt in formats if fmt.get("vcodec") not in (None, "none")
        ]
    if not audio_candidates:
        audio_candidates = [
            fmt for fmt in formats if fmt.get("acodec") not in (None, "none")
        ]

    if not video_candidates or not audio_candidates:
        raise RuntimeError("Unable to locate suitable video/audio formats for this URL.")

    def video_key(fmt: Dict) -> Tuple:
        return (
            fmt.get("height") or 0,
            fmt.get("fps") or 0,
            fmt.get("tbr") or 0,
            -fmt.get("filesize", 0),
        )

    def audio_key(fmt: Dict) -> Tuple:
        return (
            fmt.get("abr") or fmt.get("tbr") or 0,
            fmt.get("asr") or 0,
            -fmt.get("filesize", 0),
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


def ensure_unique_path(directory: str, base_name: str) -> str:
    """Generate a unique MP4 path inside directory."""
    os.makedirs(directory, exist_ok=True)
    candidate = os.path.join(directory, f"{base_name}.mp4")
    counter = 1
    while os.path.exists(candidate):
        candidate = os.path.join(directory, f"{base_name} ({counter}).mp4")
        counter += 1
    return candidate


class YouTubeDownloaderApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title("Ultra Quality YouTube Downloader (Unifi Ready)")
        self.root.geometry("820x600")
        self.root.minsize(720, 520)

        self.cancel_requested = False
        self.active_process: Optional[subprocess.Popen] = None
        self.worker_thread: Optional[threading.Thread] = None

        self.env_status = probe_environment()

        self.url_var = tk.StringVar()
        default_dir = os.path.join(os.path.expanduser("~/Videos"), "Unifi Downloads")
        self.path_var = tk.StringVar(value=default_dir)
        self.stage_var = tk.StringVar(value="Waiting for input")
        self.status_var = tk.StringVar(value="Ready.")
        self.progress_var = tk.IntVar(value=0)
        self.env_summary_var = tk.StringVar()

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

        ttk.Label(self.main_frame, text="YouTube / Playlist URL").grid(row=0, column=0, sticky="w")
        self.url_entry = ttk.Entry(self.main_frame, textvariable=self.url_var, width=80)
        self.url_entry.grid(row=0, column=1, columnspan=2, sticky="ew", pady=5)

        ttk.Label(self.main_frame, text="Download Folder").grid(row=1, column=0, sticky="w")
        self.path_entry = ttk.Entry(self.main_frame, textvariable=self.path_var, width=60)
        self.path_entry.grid(row=1, column=1, sticky="ew", pady=5)
        ttk.Button(self.main_frame, text="Browse...", command=self._browse_directory).grid(row=1, column=2, sticky="ew", padx=(10, 0))

        button_frame = ttk.Frame(self.main_frame)
        button_frame.grid(row=2, column=0, columnspan=3, pady=10, sticky="ew")
        button_frame.columnconfigure((0, 1, 2, 3), weight=1, uniform="btn")
        self.download_button = ttk.Button(button_frame, text="Start Download", command=self.start_download)
        self.download_button.grid(row=0, column=0, padx=5, sticky="ew")
        self.cancel_button = ttk.Button(button_frame, text="Cancel", command=self.cancel_download, state="disabled")
        self.cancel_button.grid(row=0, column=1, padx=5, sticky="ew")
        ttk.Button(button_frame, text="Test Environment", command=self._handle_test_environment).grid(row=0, column=2, padx=5, sticky="ew")
        ttk.Button(button_frame, text="Open Folder", command=self._open_download_directory).grid(row=0, column=3, padx=5, sticky="ew")

        ttk.Label(self.main_frame, textvariable=self.stage_var, foreground=self.accent_color).grid(
            row=3, column=0, columnspan=3, sticky="w", pady=(10, 0)
        )

        self.progress_bar = ttk.Progressbar(
            self.main_frame, maximum=100, variable=self.progress_var, style="Green.Horizontal.TProgressbar"
        )
        self.progress_bar.grid(row=4, column=0, columnspan=3, sticky="ew", pady=8)

        self.status_label = ttk.Label(self.main_frame, textvariable=self.status_var, font=("Segoe UI", 10, "bold"))
        self.status_label.grid(row=5, column=0, columnspan=3, sticky="w")

        env_frame = ttk.LabelFrame(self.main_frame, text="Environment", padding=10)
        env_frame.grid(row=6, column=0, columnspan=3, sticky="ew", pady=10)
        self.env_label = ttk.Label(env_frame, textvariable=self.env_summary_var, justify="left")
        self.env_label.pack(fill="x")

        log_frame = ttk.LabelFrame(self.main_frame, text="Activity Log", padding=10)
        log_frame.grid(row=7, column=0, columnspan=3, sticky="nsew")
        self.main_frame.rowconfigure(7, weight=1)
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

    # ------------------------------------------------------------------ UI helpers
    def _browse_directory(self):
        path = filedialog.askdirectory(initialdir=self.path_var.get() or os.path.expanduser("~"))
        if path:
            self.path_var.set(path)

    def _set_stage(self, text: str):
        self.root.after(0, lambda: self.stage_var.set(text))

    def _set_status(self, text: str, color: str = "#e5e5e5"):
        def _update():
            self.status_var.set(text)
            self.status_label.configure(foreground=color)

        self.root.after(0, _update)

    def _set_progress(self, value: int):
        value = max(0, min(100, value))
        self.root.after(0, lambda: self.progress_var.set(value))

    def _append_log(self, message: str):
        timestamp = time.strftime("%H:%M:%S")
        entry = f"[{timestamp}] {message}\n"
        self.root.after(0, lambda: self._write_log(entry))

    def _write_log(self, text: str):
        self.log_widget.configure(state="normal")
        self.log_widget.insert("end", text)
        self.log_widget.see("end")
        self.log_widget.configure(state="disabled")

    def _reset_controls(self):
        self.download_button.configure(state="normal")
        self.cancel_button.configure(state="disabled")
        self._set_progress(0)
        self._set_stage("Idle")

    def _handle_test_environment(self):
        self.env_status = probe_environment()
        self._update_environment_summary()
        if self.env_status.issues:
            messagebox.showwarning("Environment Check", "\n".join(self.env_status.issues))
        else:
            messagebox.showinfo(
                "Environment Check",
                "FFmpeg and FFprobe detected.\nNVENC available: yes" if self.env_status.nvenc_available else "FFmpeg ready (CPU encoding mode).",
            )

    def _update_environment_summary(self):
        if not self.env_status.ffmpeg_path:
            summary = "FFmpeg: missing\n"
        else:
            summary = f"FFmpeg: {self.env_status.ffmpeg_path}\n"
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
            self._set_status("Enter a video or playlist URL.", "#ff6b6b")
            return
        if not destination:
            self._set_status("Select a download directory.", "#ff6b6b")
            return
        os.makedirs(destination, exist_ok=True)

        self.cancel_requested = False
        self._set_status("Preparing download...", self.accent_color)
        self._set_stage("Initializing")
        self._set_progress(0)
        self.download_button.configure(state="disabled")
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
        if self.active_process and self.active_process.poll() is None:
            self.active_process.terminate()

    # ------------------------------------------------------------------ Pipeline
    def _run_pipeline(self, url: str, destination: str):
        temp_dir = tempfile.mkdtemp(prefix="unifi_dl_")
        try:
            video_path, audio_path, metadata, video_fmt, audio_fmt = self._download_media(url, temp_dir)
            if self.cancel_requested:
                raise DownloadCancelled()
            output_path = self._transcode_and_mux(
                video_path, audio_path, metadata, destination, video_fmt, audio_fmt
            )
            self._set_status(f"Completed: {os.path.basename(output_path)}", "#4dcc7d")
            self._append_log(f"Saved final file to {output_path}")
        except DownloadCancelled:
            self._set_status("Download cancelled.", "#ff6b6b")
            self._append_log("Operation cancelled.")
        except Exception as exc:
            self._set_status(f"Error: {exc}", "#ff6b6b")
            self._append_log(f"Error: {exc}")
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)
            self.root.after(0, self._reset_controls)

    def _download_media(self, url: str, temp_dir: str) -> Tuple[str, str, Dict, Dict, Dict]:
        self._set_stage("Fetching metadata")
        self._append_log("Fetching video information...")
        ydl_opts = {
            "quiet": True,
            "skip_download": True,
            "noplaylist": False,
        }
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            metadata = ydl.extract_info(url, download=False)
        best_video, best_audio = pick_best_formats(metadata)
        self._append_log(
            f"Selected video {best_video.get('format_id')} ({best_video.get('height')}p) "
            f"and audio {best_audio.get('format_id')} ({best_audio.get('abr') or best_audio.get('tbr')} kbps)."
        )
        total_duration = metadata.get("duration") or 0
        estimated_size = total_duration * (TARGET_VIDEO_BITRATE + TARGET_AUDIO_BITRATE) / 8
        if estimated_size > MAX_FILE_BYTES:
            self._append_log(
                f"Warning: estimated size {human_readable_size(estimated_size)} exceeds 5 GB limit."
            )

        video_path = self._download_stream(url, best_video["format_id"], temp_dir, "video")
        audio_path = self._download_stream(url, best_audio["format_id"], temp_dir, "audio")
        return video_path, audio_path, metadata, best_video, best_audio

    def _download_stream(self, url: str, format_id: str, temp_dir: str, label: str) -> str:
        if self.cancel_requested:
            raise DownloadCancelled()

        target_template = os.path.join(temp_dir, f"{label}.%(ext)s")
        downloaded = {"path": None}

        def hook(d):
            if self.cancel_requested:
                raise DownloadCancelled()
            status = d.get("status")
            if status == "downloading":
                downloaded_bytes = d.get("downloaded_bytes", 0)
                total_bytes = d.get("total_bytes") or d.get("total_bytes_estimate") or 0
                percent = int(downloaded_bytes / total_bytes * 100) if total_bytes else 0
                speed = d.get("speed")
                if speed:
                    speed_mbps = speed * 8 / 1_000_000
                    stage = f"Downloading {label} stream @ {speed_mbps:.1f} Mbps"
                else:
                    stage = f"Downloading {label} stream"
                self._set_stage(stage)
                self._set_progress(percent)
            elif status == "finished":
                downloaded["path"] = d.get("filename")
                self._append_log(f"{label.capitalize()} download finished: {downloaded['path']}")

        ydl_opts = {
            "quiet": True,
            "nocheckcertificate": True,
            "noplaylist": True,
            "format": format_id,
            "outtmpl": target_template,
            "progress_hooks": [hook],
            "windowsfilenames": True,
        }
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            ydl.download([url])

        file_path = downloaded["path"]
        if not file_path or not os.path.exists(file_path):
            raise RuntimeError(f"Failed to download {label} stream.")
        return file_path

    def _execute_ffmpeg(
        self,
        cmd: list[str],
        stage_label: str,
        duration: Optional[float],
        start_progress: int,
        end_progress: int,
    ):
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            universal_newlines=True,
            encoding="utf-8",
            errors="replace",
        )
        self.active_process = process
        last_log = 0.0
        fallback_progress = start_progress
        try:
            for line in process.stdout:
                if self.cancel_requested and process.poll() is None:
                    process.terminate()
                    process.wait()
                    raise DownloadCancelled()
                line = (line or "").strip()
                if not line:
                    continue
                timestamp = parse_ffmpeg_time(line) if "time=" in line else None
                if timestamp is not None and duration:
                    fraction = min(1.0, timestamp / duration)
                    progress_value = int(start_progress + fraction * (end_progress - start_progress))
                    self._set_progress(progress_value)
                elif not duration:
                    fallback_progress = min(end_progress, fallback_progress + 1)
                    self._set_progress(fallback_progress)
                bitrate_match = re.search(r"bitrate=\s*([\d.]+)kbits/s", line)
                if bitrate_match:
                    bitrate_mbps = float(bitrate_match.group(1)) / 1000
                    self._set_stage(f"{stage_label} @ {bitrate_mbps:.1f} Mbps")
                now = time.time()
                if now - last_log >= 0.9 or "error" in line.lower():
                    self._append_log(f"{stage_label}: {line}")
                    last_log = now
        finally:
            self.active_process = None

        return_code = process.wait()
        if return_code != 0:
            raise RuntimeError(f"FFmpeg failed during {stage_label.lower()} (exit code {return_code}).")
        self._set_progress(end_progress)

    def _remux_passthrough(
        self, video_path: str, audio_path: str, output_path: str, duration: Optional[float]
    ):
        ffmpeg_bin = self.env_status.ffmpeg_path or "ffmpeg"
        cmd = [
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
        output_path = ensure_unique_path(destination, safe_name)

        duration = metadata.get("duration") or 0
        if should_passthrough(video_fmt, audio_fmt):
            self._append_log("Stream already meets Unifi requirements. Remuxing without re-encode.")
            self._set_stage("Remuxing (no transcode needed)")
            self._set_progress(70)
            self._remux_passthrough(video_path, audio_path, output_path, duration)
            final_size = os.path.getsize(output_path)
            self._append_log(f"Remux complete. Final size: {human_readable_size(final_size)}")
            self._set_progress(100)
            return output_path

        source_fps = video_fmt.get("fps") or metadata.get("fps") or metadata.get("average_fps")
        target_fps = choose_target_fps(source_fps)
        needs_fps_change = bool(source_fps) and round(source_fps) not in ALLOWED_FPS

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

            cmd += [
                "-profile:v",
                "high",
                "-pix_fmt",
                "yuv420p",
            ]

            if needs_fps_change:
                cmd += ["-r", str(target_fps)]

            cmd += [
                "-c:a",
                "aac",
                "-b:a",
                f"{TARGET_AUDIO_BITRATE}",
                "-ac",
                "2",
                "-movflags",
                "+faststart",
                output_path,
            ]
            return cmd

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
            if video_codec == "h264_nvenc":
                self._append_log(f"NVENC failed ({exc}). Retrying with CPU x264.")
                self._set_status("NVENC unavailable. Retrying on CPU...", "#ffbf00")
                fallback_cmd = build_cmd("libx264")
                self._execute_ffmpeg(
                    fallback_cmd,
                    stage_label="CPU Transcoding",
                    duration=duration or None,
                    start_progress=20,
                    end_progress=98,
                )
            else:
                raise

        final_size = os.path.getsize(output_path)
        if final_size > MAX_FILE_BYTES:
            self._append_log(
                f"Warning: Final file is {human_readable_size(final_size)}, which exceeds Unifi's 5 GB limit."
            )
        else:
            self._append_log(f"Final file size: {human_readable_size(final_size)}")
        self._set_progress(100)
        return output_path


if __name__ == "__main__":
    root = tk.Tk()
    app = YouTubeDownloaderApp(root)
    root.mainloop()
