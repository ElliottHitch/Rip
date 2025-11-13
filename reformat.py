import tkinter as tk
from tkinter import filedialog, ttk
import ffmpeg
import os
from concurrent.futures import ThreadPoolExecutor, as_completed
import multiprocessing

def reformat_video(input_video_path, output_video_path):
    """
    Reformats a video file to MP4 format with hardware acceleration.
    """
    try:
        # Convert video to MP4 format with hardware acceleration
        ffmpeg.input(input_video_path).output(output_video_path, format='mp4', vcodec='h264_nvenc', acodec='aac').run()
    except ffmpeg.Error as e:
        return f"Error: {e.stderr.decode()}"
    return f"Video reformatted to MP4: {output_video_path}"

def browse_videos():
    """
    Opens a file dialog to select multiple video files.
    """
    video_paths = filedialog.askopenfilenames(filetypes=[("Video Files", "*.mp4;*.avi;*.mov")])
    if video_paths:
        video_entry.delete(0, tk.END)
        video_entry.insert(0, ', '.join(video_paths))
        # Store selected videos in a global list
        global video_list
        video_list = video_paths

def browse_save_location():
    """
    Opens a directory dialog to select the save location.
    """
    folder_path = filedialog.askdirectory()
    if folder_path:
        save_entry.delete(0, tk.END)
        save_entry.insert(0, folder_path)

def process_videos():
    """
    Processes each video file in the queue using parallel processing.
    """
    global video_list
    save_folder = save_entry.get()

    if not video_list or not save_folder:
        status_label.config(text="Please provide both input and output paths.")
        return

    # Clear status label
    status_label.config(text="Processing videos...")

    # Get the number of CPU cores
    num_cores = multiprocessing.cpu_count()

    # Function to process each video
    def process_video(video_path):
        base_filename = os.path.basename(video_path)
        output_video = os.path.join(save_folder, os.path.splitext(base_filename)[0] + ".mp4")
        return reformat_video(video_path, output_video)

    # Set up a ThreadPoolExecutor to process videos in parallel
    with ThreadPoolExecutor(max_workers=num_cores) as executor:
        # Submit all video processing tasks
        futures = [executor.submit(process_video, video_path) for video_path in video_list]

        total_files = len(futures)
        completed_files = 0
        
        for future in as_completed(futures):
            result = future.result()
            if "Error" in result:
                status_label.config(text=result)
                return
            completed_files += 1
            progress_var.set(completed_files / total_files * 100)
            root.update_idletasks()

    status_label.config(text="All videos have been reformatted.")

# Create main window
root = tk.Tk()
root.title("Video Reformatter")

# Create and place widgets
tk.Label(root, text="Select Video Files:").grid(row=0, column=0, padx=10, pady=10, sticky="w")
video_entry = tk.Entry(root, width=50)
video_entry.grid(row=0, column=1, padx=10, pady=10)
tk.Button(root, text="Browse", command=browse_videos).grid(row=0, column=2, padx=10, pady=10)

tk.Label(root, text="Save Location:").grid(row=1, column=0, padx=10, pady=10, sticky="w")
save_entry = tk.Entry(root, width=50)
save_entry.grid(row=1, column=1, padx=10, pady=10)
tk.Button(root, text="Browse", command=browse_save_location).grid(row=1, column=2, padx=10, pady=10)

tk.Button(root, text="Start Conversion", command=process_videos).grid(row=2, column=0, columnspan=3, pady=10)

# Progress bar
progress_var = tk.DoubleVar()
progress_bar = ttk.Progressbar(root, variable=progress_var, maximum=100, length=300)
progress_bar.grid(row=3, column=0, columnspan=3, padx=10, pady=10)

# Status label
status_label = tk.Label(root, text="", relief=tk.SUNKEN, anchor="w")
status_label.grid(row=4, column=0, columnspan=3, padx=10, pady=10, sticky="ew")

# Global variable to store the list of video files
video_list = []

# Run the main event loop
root.mainloop()
