# Rip

Rip is a Windows desktop app for downloading YouTube videos at highest quality. It downloads video and audio separately, then combines them into one file. That gives you access to higher resolutions, including 4K, when the source provides them.


![yt-dlp with Deno downloads separate video and audio streams. FFmpeg combines or converts them, then FFprobe checks the result before Rip saves one file.](docs/assets/pipeline.svg)

## Download a video

1. Paste a video link and choose where to save it.
2. Leave **Highest available** selected, or choose a resolution such as 1080p.
3. Click **Download**.

Standard mode keeps the original codecs and frame rate in an MKV file. A selected resolution sets the maximum quality; Rip never upscales smaller videos.

You can also download audio only. Rip names files after the video title and never overwrites an existing file.

## Digital Signage
Enable **UniFi Connect compatibility** to convert to MP4 with H.264 video and AAC-LC audio for Display Cast and Cast Pro. Conversion takes longer.
Support for UniFi Connect currently. 

## Browser-session access

Under **More options**, Rip can ask yt-dlp to read a supported browser's local session for one download. This is off by default. Rip passes only the selected browser type to yt-dlp, does not export a cookie file, and clears the selection after the run.

This gives yt-dlp temporary access to authentication cookies held by that browser. Enable it only for a browser and account you control, and only when you understand the access being granted. It is not a way to bypass service restrictions.

## Under the hood

| Tool | What it does |
| --- | --- |
| yt-dlp | Reads video metadata and available formats, then downloads the selected streams. |
| Deno | Provides the JavaScript runtime yt-dlp uses for YouTube extraction. |
| FFmpeg | Combines streams without re-encoding in standard mode. UniFi mode uses its `libx264` and AAC encoders to produce H.264 video and AAC-LC audio. |
| FFprobe | Inspects the output's streams, container, duration and resolution, plus UniFi requirements, before Rip saves it. |

Rip selects formats for your quality setting and coordinates these tools. Their pinned versions and download sources are in the [tool catalog](src/Rip.App/Setup/tool-bootstrap.json).

## Install and update

Download `Rip-win-Setup.exe` from the [releases page](https://github.com/ElliottHitch/Rip/releases/latest).

`Rip-win-Setup.exe` creates desktop and Start menu shortcuts. First launch downloads and verifies the required media tools. Python is not required.

The installer and update packages are currently unsigned, so Windows may display a warning. They must not be described as publisher-verified. Current packaging includes checksums and `THIRD-PARTY-NOTICES.md`; earlier releases may not.

Rip does not check for updates automatically. Open **About Rip & updates** and select **Check for updates** when you choose. Updates are offered for review and are installed only after you select **Update and restart**. The complete update/relaunch flow still needs validation before it should be treated as production-ready.

## Run from source

Install .NET SDK 10.0.400, then run these commands from the repository folder:

```powershell
dotnet restore --locked-mode
dotnet run --project src/Rip.App/Rip.App.csproj
```

`python app.py` also launches the same app if you already have Python installed.

See the [release runbook](docs/release-runbook.md) for installer builds, tests, and explicitly approved GitHub releases.

## License

Rip does not currently include an open-source license. Public visibility alone does not grant permission to copy, modify, or redistribute the source.
