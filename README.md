





# Tv Station Assistant

**Tv Station Assistant** is a C# console application designed to automate audio normalization, commercial break detection, compilation splitting, and file tagging for video archives. Built around FFmpeg, it prepares media at 640x480 for SD tv viewing for the Raspberry Pi TV Station emulator: https://github.com/mopenstein/raspberry_pi_tv_station

<img src="/assets/Screenshot%202026-09-29%20154z530.png" width="80%" title="CLI Screenshot">
---

## Table of Contents
- [Features](#features)
- [Requirements](#requirements)
- [Configuration](#configuration)
- [CLI Usage](#cli-usage)
- [Interactive Menu Reference](#interactive-menu-reference)
  - [Audio Normalization](#1---batch-audio-normalization)
  - [Commercial Detection & Break Printing](#2---print-breaks--6---test-print-breaks)
  - [Compilation Splitting](#3---split-compilation-video)
  - [File Tagging & Sorting](#5---move-tagged-files)
  - [Filename Utilities](#filename-utilities)
- [File Conventions & Tag Formats](#file-conventions--tag-formats)
- [Logging](#logging)

---

## Features

- **Two-Pass EBU R128 Audio Normalization:** Analyzes integrated loudness, true peak, and loudness range via FFmpeg's `loudnorm` filter (JSON mode), then renders normalized audio without re-encoding video streams (`-c:v copy`).
- **Commercial Break Detection:** Detects transition blackout periods using FFmpeg's `blackdetect` filter with user-tunable luminance, minimum spacing, and duration constraints.
- **Scene-Cut Snapping (Fine-Tuning):** Matches black detection timestamps to exact intra-frame cuts using `select='gt(scene,...)'`, eliminating messy mid-fade splice points.
- **Automated Video Splitting:** Batch-chops ad compilations into individual MP4 files formatted for 4:3 SD presentation (`640x480`, 29.97 fps CFR, YADIF deinterlacing, AAC stereo).
- **Two-Stage Precision Seeking:** Applies fast pre-seeking (`-ss` before `-i`) combined with precision frame seeking (`-ss` after `-i`) to guarantee accurate trim points without A/V drift.
- **Hardware Acceleration:** Auto-probes NVIDIA NVENC encoder capability (testing `p4` modern and `fast` legacy presets) and falls back cleanly to software `libx264`.
- **Runtime Duration Injection:** Recursively calculates clip durations via FFmpeg and appends timestamp tokens (`%T(seconds)%`) to filenames for schedule-matching engines.
- **Time-Slot Organizing:** Automatically categorizes and moves content flagged for specific broadcast blocks (`%AM%`, `%PM%`, `%ANY%`) into dedicated subdirectories.
- **Sidecar Preservation:** Maintains synchronization of `.commercials` metadata files during normalization, renaming, moving, and tagging passes.

---

## Requirements

- **OS:** Windows 10 / 11 or Windows Server (uses Windows shell console formatting and directory APIs).
- **Runtime:** .NET Framework 4.7.2+ or .NET 6.0+ SDK / Runtime.
- **FFmpeg:** A modern static build of `ffmpeg.exe` compiled with `loudnorm`, `blackdetect`, and (optionally) `h264_nvenc`.
- **Hardware (Optional):** NVIDIA GPU with up-to-date drivers supporting NVENC.

## Configuration

On startup, the application reads runtime configuration from `settings.txt`:

```ini
ffmpeg location=C:\Tools\ffmpeg\bin\ffmpeg.exe
temp folder=C:\Media\Temp
log file=C:\Media\Logs
use nvidia=true
```

| Key | Description |
| :--- | :--- |
| `ffmpeg location` | Full path to the local `ffmpeg.exe` binary. |
| `temp folder` | Scratch folder for intermediate operations. |
| `log file` | Directory path where `log.txt` will be written and cleared per session. |
| `use nvidia` | Set to `true` to use `h264_nvenc` encoding; set to `false` for `libx264`. |

*Note: If the configured `ffmpeg.exe` path is empty or invalid upon start, the application routes automatically to the Settings menu.*

---

## CLI Usage

The program supports non-interactive execution for automated ingest pipelines and shell scripts:

```bash
# Normalize a single media file:
TvStationAssistant.exe -n "C:\Media\Bumper.mp4"

# Alternative flag syntax:
TvStationAssistant.exe --normalize "C:\Media\Bumper.mp4"
```

Files containing the `_NA_` token are automatically skipped to avoid double normalization.

---

## Interactive Menu Reference

```text
██████████████████████████████████████████████████████████████
█                                                            █
█ Options:                                                   █
█                                                            █
█   [ 1 ] - Batch Normalize Audio   [ n ] Remove NA Mark     █
█   [ 2 ] - Print Breaks            [ u ] Remove Non-Ascii   █
█   [ 3 ] - Split Video             [ l ] Replace String     █
█   [ 5 ] - Move Tagged Files                                █
█   [ 6 ] - Test Print Breaks       [ k ] Add to Filename    █
█   [ 7 ] - Time Adder              [ t ] Remove Time        █
█                                                            █
█   [ 9 ] - Options                                          █
█                                                            █
██████████████████████████████████████████████████████████████
```

### [ 1 ] - Batch Audio Normalization
- Prompts for one or more directory paths (prefix an entry with `+` to add additional paths).
- Performs Pass 1 loudness measurement (`-af loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json`).
- Performs Pass 2 filter application with measured parameters while copying video bitstreams (`-vcodec copy`).
- Renames the output to append `_NA_` and updates matching `.commercials` sidecar files.
- Removes the unnormalized original file upon verified completion.

### [ 2 ] - Print Breaks & [ 6 ] - Test Print Breaks
- Runs `blackdetect` across videos in the target folder (or a single file using Option `6`).
- Configurable parameters:
  - **Threshold:** Minimum blackout duration (default: `0.5s`).
  - **Black Luminance:** Maximum luminance level considered black (default: `0.05` to `0.10`).
  - **Minimum Spacing:** Enforces minimum runtime separation between detected commercial pods (default: `300s`).
  - **Synthetic Breaks:** Optional fallback that injects fixed 10-minute intervals (`600s`) if no black frames are found.
- Outputs detected timestamps to `<filename>.commercials` as newline-delimited second values.

### [ 3 ] - Split Compilation Video
- Batches through compilations, detects black frame markers, and slices clips into standalone files named `{BaseName}_{Index}_{Random}.mp4`.
- **Fine-Tuning:** Uses scene detection (`select='gt(scene,0.20)'`) within a 0.45s window of each black marker to snap cut points to precise scene boundaries.
- **Encoding specs:**
  - Standardizes video to `640x480`, 4:3 DAR, 29.97 fps constant framerate, `yuv420p`.
  - Applies `yadif=0:-1:1` deinterlacing.
  - Encodes audio to stereo AAC @ 96 kbps, 48 kHz.
  - Applies `+faststart` MP4 metadata flags for web/streaming optimization.

### [ 5 ] - Move Tagged Files
- Scans a directory for time-slot tokens in filenames:
  - `%AM%` &rarr; Moved to `./am/`
  - `%PM%` &rarr; Moved to `./pm/`
  - `%ANY%` &rarr; Moved to `./any/`
- Relocates associated `.commercials` sidecars into the corresponding target folder.

### [ 7 ] - Time Adder
- Recursively walks a folder and its subdirectories.
- Reads media duration via FFmpeg and appends the rounded total seconds as a token: `Filename%T(142)%.mp4`.

### Filename Utilities
- **`[ n ]` Remove NA Mark:** Strips `_NA_` from filenames.
- **`[ m ]` Add NA Mark:** Inserts `_NA_` into filenames and matching `.commercials` files without running normalization.
- **`[ u ]` Remove Non-ASCII:** Replaces typographic/curly quotes, dashes, and strips non-printable ASCII characters.
- **`[ l ]` Replace String:** Performs batch find-and-replace on filenames across a folder while preserving the `_NA_` marker.
- **`[ k ]` Add to Filename:** Batch prepends or appends a user-defined string to all files in a folder.
- **`[ t ]` Remove Time:** Strips `%T(...)%` timestamp tokens from filenames.

---

## File Conventions & Tag Formats

| Tag / Suffix | Purpose | Example |
| :--- | :--- | :--- |
| `_NA_` | Identifies audio that has undergone two-pass EBU R128 loudness normalization. | `Commercial_NA_.mp4` |
| `%T(###)%` | Total media duration formatted in whole seconds. | `CarAd%T(30)%.mp4` |
| `%AM%` | Tags file for morning/daytime playback slots. | `MorningBumper%AM%.mp4` |
| `%PM%` | Tags file for evening/nighttime playback slots. | `LateSignOff%PM%.mp4` |
| `%ANY%` | Tags file for floating/general rotation. | `StationID%ANY%.mp4` |
| `.commercials` | Sidecar text file containing newline-delimited break timestamps in seconds. | `Episode1.mp4.commercials` |

---

## Logging

Session events are written to `log.txt` located in the configured `log file` directory:
- Log contents are reset each time the application launches.
- Records all FFmpeg process arguments, detected durations, JSON audio metadata parses, and file moves.
- Output timestamp format: `yyyy-MM-dd HH:mm:ss`.
