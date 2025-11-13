import os
import threading
import tkinter as tk
from tkinter import ttk, filedialog
import yt_dlp

class YouTubeDownloaderApp:
    def __init__(self, root):
        self.root = root
        self.root.title("YouTube Downloader for Unifi Connect")
        self.root.geometry("600x250")
        self.root.configure(bg='#2e2e2e')

        # Flag to handle cancellation
        self.cancel_requested = False

        # Main frame setup
        self.frame = tk.Frame(root, bg='#2e2e2e')
        self.frame.pack(pady=20, padx=20, fill='both', expand=True)
        self.frame.columnconfigure(0, weight=1, uniform="col")
        self.frame.columnconfigure(1, weight=2, uniform="col")
        self.frame.columnconfigure(2, weight=1, uniform="col")

        # YouTube URL input
        tk.Label(self.frame, text='YouTube URL:', bg='#2e2e2e', fg='white') \
            .grid(row=0, column=0, sticky='ew', pady=5)
        self.url_entry = tk.Entry(self.frame, width=50, bg='#3e3e3e', fg='white')
        self.url_entry.grid(row=0, column=1, sticky='ew', padx=5, pady=5)

        # Download Path input
        tk.Label(self.frame, text='Download Path:', bg='#2e2e2e', fg='white') \
            .grid(row=1, column=0, sticky='ew', pady=5)
        self.path_entry = tk.Entry(self.frame, width=50, bg='#3e3e3e', fg='white')
        self.path_entry.insert(0, os.path.expanduser('~/videos/yt dl'))
        self.path_entry.grid(row=1, column=1, sticky='ew', padx=5, pady=5)
        tk.Button(self.frame, text='Browse', command=self.browse_directory, width=15) \
            .grid(row=1, column=2, sticky='ew', padx=5, pady=5)

        # Download and Cancel buttons
        self.download_button = tk.Button(self.frame, text='Download', command=self.start_download, width=20)
        self.download_button.grid(row=2, column=0, columnspan=2, pady=10)
        self.cancel_button = tk.Button(self.frame, text='Cancel', command=self.cancel_download, width=20, state='disabled')
        self.cancel_button.grid(row=2, column=2, pady=10)

        # Progress bar and notification
        self.progress_bar = ttk.Progressbar(self.frame, length=400, maximum=100)
        self.progress_bar.grid(row=3, column=0, columnspan=3, pady=10, sticky='ew')
        self.notification_label = tk.Label(self.frame, text="", bg='#2e2e2e', fg='white')
        self.notification_label.grid(row=4, column=0, columnspan=3, sticky='ew')

    def browse_directory(self):
        """Open a dialog to select download directory."""
        path = filedialog.askdirectory()
        if path:
            self.path_entry.delete(0, tk.END)
            self.path_entry.insert(0, path)

    def update_notification(self, message, color="white"):
        """Update the notification label in the GUI."""
        self.root.after(0, lambda: self.notification_label.config(text=message, fg=color))

    def progress_hook(self, d):
        """Update progress bar during download."""
        if self.cancel_requested:
            raise Exception("Download cancelled by user.")
        if d['status'] == 'downloading':
            downloaded = d.get('downloaded_bytes', 0)
            total = d.get('total_bytes') or d.get('total_bytes_estimate', 0)
            if total > 0:
                percent = int(downloaded / total * 100)
                self.root.after(0, lambda: self.progress_bar.config(value=percent))

    def download_video(self, url, download_path):
        """Download and merge video using yt-dlp."""
        try:
            ydl_opts = {
                'format': 'bestvideo+bestaudio/best',  # Select highest quality video and audio
                'merge_output_format': 'mp4',          # Merge into MP4
                'outtmpl': os.path.join(download_path, '%(title)s.%(ext)s'),  # Output path
                'progress_hooks': [self.progress_hook],  # Update progress
                'postprocessor_args': [
                    '-c:v', 'h264_nvenc',  # Hardware-accelerated H.264 encoding (RTX 4090)
                    '-preset', 'p7',       # High-quality preset
                    '-c:a', 'aac',         # AAC audio codec
                    '-movflags', '+faststart'  # Optimize for playback
                ]
            }
            self.update_notification("Downloading and merging video...", "blue")
            with yt_dlp.YoutubeDL(ydl_opts) as ydl:
                ydl.download([url])
            self.update_notification("Download completed successfully!", "green")
            self.root.after(0, lambda: self.progress_bar.config(value=0))
        except Exception as e:
            self.update_notification(f"Error: {str(e)}", "red")
        finally:
            self.root.after(0, lambda: self.cancel_button.config(state='disabled'))
            self.root.after(0, lambda: self.download_button.config(state='normal'))

    def start_download(self):
        """Start the download process in a separate thread."""
        url = self.url_entry.get().strip()
        download_path = self.path_entry.get().strip()
        if not url or not download_path:
            self.update_notification("Please provide both URL and download path", "red")
            return
        if not os.path.exists(download_path):
            os.makedirs(download_path)
        self.cancel_requested = False
        self.download_button.config(state='disabled')
        self.cancel_button.config(state='normal')
        self.progress_bar.config(value=0)
        threading.Thread(target=self.download_video, args=(url, download_path), daemon=True).start()

    def cancel_download(self):
        """Set flag to cancel the download."""
        self.cancel_requested = True
        self.update_notification("Cancelling download...", "orange")

if __name__ == "__main__":
    root = tk.Tk()
    app = YouTubeDownloaderApp(root)
    root.mainloop()