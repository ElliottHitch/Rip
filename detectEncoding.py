import ffmpeg
import sys
import tkinter as tk
from tkinter import ttk, filedialog, scrolledtext
from pathlib import Path

class MediaInfoGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("Media Info Detector")
        self.root.geometry("600x500")
        
        # Create main frame
        main_frame = ttk.Frame(root, padding="10")
        main_frame.grid(row=0, column=0, sticky=(tk.W, tk.E, tk.N, tk.S))
        
        # File selection
        self.file_frame = ttk.Frame(main_frame)
        self.file_frame.grid(row=0, column=0, sticky=(tk.W, tk.E), pady=5)
        
        self.file_path = tk.StringVar()
        self.file_entry = ttk.Entry(self.file_frame, textvariable=self.file_path, width=50)
        self.file_entry.grid(row=0, column=0, padx=5)
        
        self.browse_button = ttk.Button(self.file_frame, text="Browse", command=self.browse_file)
        self.browse_button.grid(row=0, column=1, padx=5)
        
        # Analyze button
        self.analyze_button = ttk.Button(main_frame, text="Analyze Media", command=self.analyze_media)
        self.analyze_button.grid(row=1, column=0, pady=10)
        
        # Results area
        self.results_area = scrolledtext.ScrolledText(main_frame, width=70, height=20, wrap=tk.WORD)
        self.results_area.grid(row=2, column=0, pady=5)
        
        # Configure grid weights
        root.columnconfigure(0, weight=1)
        root.rowconfigure(0, weight=1)
        main_frame.columnconfigure(0, weight=1)
        self.file_frame.columnconfigure(0, weight=1)

    def browse_file(self):
        filename = filedialog.askopenfilename(
            title="Select Media File",
            filetypes=(
                ("Media files", "*.mp4 *.avi *.mkv *.mov *.mp3 *.wav"),
                ("All files", "*.*")
            )
        )
        if filename:
            self.file_path.set(filename)

    def analyze_media(self):
        file_path = self.file_path.get()
        if not file_path:
            self.results_area.delete(1.0, tk.END)
            self.results_area.insert(tk.END, "Please select a file first.")
            return
            
        try:
            # Clear previous results
            self.results_area.delete(1.0, tk.END)
            
            # Get media information using ffprobe
            probe = ffmpeg.probe(file_path)
            
            # Initialize variables
            video_info = None
            audio_info = None
            
            # Extract stream information
            for stream in probe['streams']:
                if stream['codec_type'] == 'video':
                    video_info = {
                        'codec': stream['codec_name'],
                        'resolution': f"{stream.get('width', 'N/A')}x{stream.get('height', 'N/A')}",
                        'pixel_format': stream.get('pix_fmt', 'N/A'),
                        'bitrate': stream.get('bit_rate', 'N/A'),
                        'fps': eval(stream.get('r_frame_rate', 'N/A'))
                    }
                
                elif stream['codec_type'] == 'audio':
                    audio_info = {
                        'codec': stream['codec_name'],
                        'sample_rate': stream.get('sample_rate', 'N/A'),
                        'channels': stream.get('channels', 'N/A'),
                        'bitrate': stream.get('bit_rate', 'N/A')
                    }
            
            # Display results
            self.results_area.insert(tk.END, "Media Information:\n")
            self.results_area.insert(tk.END, "-----------------\n")
            
            if video_info:
                self.results_area.insert(tk.END, "\nVideo Stream:\n")
                self.results_area.insert(tk.END, f"Codec: {video_info['codec']}\n")
                self.results_area.insert(tk.END, f"Resolution: {video_info['resolution']}\n")
                self.results_area.insert(tk.END, f"Pixel Format: {video_info['pixel_format']}\n")
                self.results_area.insert(tk.END, f"Bitrate: {video_info['bitrate']} bits/s\n")
                self.results_area.insert(tk.END, f"Frame Rate: {video_info['fps']:.2f} fps\n")
            else:
                self.results_area.insert(tk.END, "\nNo video stream found\n")
                
            if audio_info:
                self.results_area.insert(tk.END, "\nAudio Stream:\n")
                self.results_area.insert(tk.END, f"Codec: {audio_info['codec']}\n")
                self.results_area.insert(tk.END, f"Sample Rate: {audio_info['sample_rate']} Hz\n")
                self.results_area.insert(tk.END, f"Channels: {audio_info['channels']}\n")
                self.results_area.insert(tk.END, f"Bitrate: {audio_info['bitrate']} bits/s\n")
            else:
                self.results_area.insert(tk.END, "\nNo audio stream found\n")
                
        except ffmpeg.Error as e:
            self.results_area.delete(1.0, tk.END)
            self.results_area.insert(tk.END, f"Error occurred: {e.stderr.decode()}")
        except Exception as e:
            self.results_area.delete(1.0, tk.END)
            self.results_area.insert(tk.END, f"Unexpected error: {str(e)}")

def main():
    root = tk.Tk()
    app = MediaInfoGUI(root)
    root.mainloop()

if __name__ == "__main__":
    main()
