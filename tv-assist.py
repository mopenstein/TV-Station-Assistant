#!/usr/bin/env python3
import datetime
import os
import random
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

FILE_EXTENSIONS = (
    ".3g2", ".3gp", ".asf", ".avi", ".flv", ".h264", ".m2t", ".m2ts",
    ".m4a", ".m4v", ".mkv", ".mod", ".mov", ".mp3", ".ogg", ".wav",
    ".mp4", ".mpg", ".png", ".tod", ".ts", ".vob", ".webm", ".wmv"
)



# Global State
ffmpeg_location = ""
temp_folder = ""
use_nvidia = False
nvenc_preset = "fast"
log_folder = ""
normalizing = False


# ==========================================
# Terminal & Input Helpers
# ==========================================

def clear_screen():
    os.system("cls" if os.name == "nt" else "clear")


def getch():
    """Reads a single keypress without waiting for Enter."""
    if os.name == "nt":
        import msvcrt
        ch = msvcrt.getch()
        if ch in (b"\x00", b"\xe0"):  # Function/arrow keys
            msvcrt.getch()
            return ""
        if ch == b"\x1b":  # ESC
            return "\x1b"
        return ch.decode("utf-8", errors="ignore")
    else:
        import termios
        import tty
        fd = sys.stdin.fileno()
        old_settings = termios.tcgetattr(fd)
        try:
            tty.setraw(fd)
            ch = sys.stdin.read(1)
        finally:
            termios.tcsetattr(fd, termios.TCSADRAIN, old_settings)
        return ch


def draw_message(message: str):
    clear_screen()
    print(r" _______  ___      _______  ______    _______ ")
    print(r"|   _   ||   |    |       ||    _ |  |       |")
    print(r"|  |_|  ||   |    |    ___||   | ||  |_     _|")
    print(r"|       ||   |    |   |___ |   |_||_   |   |  ")
    print(r"|       ||   |___ |    ___||    __  |  |   |  ")
    print(r"|   _   ||       ||   |___ |   |  | |  |   |  ")
    print(r"|__| |__||_______||_______||___|  |_|  |___|  ")
    print("\n" + "*" * 65)
    for line in message.splitlines():
        print(f">>>>>     {line}")
    print("*" * 65 + "\n")
    print("Press a key to continue!")
    getch()


def prompt_path(prompt: str, must_exist: bool = True, is_file: bool = False):
    print(prompt)
    raw = input("> ").strip().strip("\"'")
    if not raw:
        return ""

    if must_exist:
        exists = os.path.isfile(raw) if is_file else os.path.isdir(raw)
        if not exists:
            draw_message(f"{'File' if is_file else 'Path'} does not exist: {raw}")
            return None
    return raw


def prompt_setting(label: str, current_value: str) -> str:
    print("\n")
    if current_value:
        print(f"{label} [Current: {current_value}]")
        print("(Press Enter to keep current, or type a new path)")
    else:
        print(label)
    user_input = input("> ").strip()
    return current_value if not user_input else user_input


def prompt_double(prompt: str, default_val: float) -> float:
    print(f"\n{prompt} [Default: {default_val}]")
    raw = input("> ").strip()
    try:
        return float(raw)
    except ValueError:
        return default_val


def prompt_int(prompt: str, default_val: int) -> int:
    print(f"\n{prompt} [Default: {default_val}]")
    raw = input("> ").strip()
    try:
        return int(raw)
    except ValueError:
        return default_val


def prompt_bool(prompt: str, default_val: bool = False) -> bool:
    choice = "Y" if default_val else "N"
    print(f"\n{prompt} ([Y]es / [N]o) [Default: {choice}]")
    raw = input("> ").strip().lower()
    if not raw:
        return default_val
    return raw.startswith("y")


# ==========================================
# Logging & Utilities
# ==========================================

def reset_log():
    if not log_folder:
        return
    try:
        os.makedirs(log_folder, exist_ok=True)
        log_path = os.path.join(log_folder, "log.txt")
        with open(log_path, "w", encoding="utf-8") as f:
            f.write("")
    except Exception as ex:
        print("Error resetting log file:", ex)


def add_log(msg: str):
    if not log_folder:
        return
    try:
        log_path = os.path.join(log_folder, "log.txt")
        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(f"{now}    {msg}\n")
    except Exception:
        pass


def return_clean_ascii(s: str) -> str:
    if not s:
        return ""
    replacements = {
        "’": "'", "‘": "'", "“": '"', "”": '"', "—": "-", "–": "-"
    }
    for orig, rep in replacements.items():
        s = s.replace(orig, rep)

    invalid_chars = set(r'<>:"/\|?*' + "".join(chr(i) for i in range(32)))
    cleaned = "".join(c for c in s if 32 <= ord(c) <= 126 and c not in invalid_chars)
    return cleaned.strip()


def parse_duration_to_seconds(dur_str: str) -> float:
    """Parses HH:MM:SS.xx or MM:SS.xx into total seconds."""
    parts = dur_str.strip().split(":")
    try:
        if len(parts) == 3:
            return float(parts[0]) * 3600 + float(parts[1]) * 60 + float(parts[2])
        elif len(parts) == 2:
            return float(parts[0]) * 60 + float(parts[1])
        elif len(parts) == 1:
            return float(parts[0])
    except ValueError:
        pass
    return 0.0


def format_duration(seconds: float) -> str:
    td = datetime.timedelta(seconds=int(seconds))
    return str(td)


def get_files(folder: str, ext_filter=None, name_filter=None):
    if not folder or not os.path.isdir(folder):
        return []

    collected = []
    for entry in os.scandir(folder):
        if not entry.is_file():
            continue

        filename = entry.name
        base_name, ext = os.path.splitext(filename)

        if ext_filter:
            if not any(ext.lower() == target_ext.lower() for target_ext in ext_filter):
                continue

        if name_filter:
            if any(bad_str.lower() in base_name.lower() for bad_str in name_filter):
                continue

        collected.append(entry.path)
    return collected


def build_precision_seek_args(start_sec: float, duration_sec: float, pre_seek_buffer: float = 5.0):
    start = max(0.0, start_sec)
    duration = max(0.000001, duration_sec)
    duration_str = f"{duration:.6f}"

    if start <= pre_seek_buffer:
        return ["", f"-ss {start:.6f} -t {duration_str} "]

    rough_seek = start - pre_seek_buffer
    fine_seek = pre_seek_buffer
    return [
        f"-ss {rough_seek:.6f} ",
        f"-ss {fine_seek:.6f} -t {duration_str} "
    ]


# ==========================================
# FFmpeg Subprocess Routines
# ==========================================

def test_nvenc_preset(preset_name: str) -> bool:
    try:
        cmd = [
            ffmpeg_location,
            "-y", "-f", "lavfi",
            "-i", "color=c=black:s=64x64:d=0.04",
            "-c:v", "h264_nvenc",
            "-preset", preset_name,
            "-f", "null", "-"
        ]
        res = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, check=False)
        return res.returncode == 0
    except Exception:
        return False


def detect_nvidia_encoder() -> bool:
    global nvenc_preset
    if not ffmpeg_location or not os.path.isfile(ffmpeg_location):
        return False

    if test_nvenc_preset("p4"):
        nvenc_preset = "p4"
        return True

    if test_nvenc_preset("fast"):
        nvenc_preset = "fast"
        return True

    return False


def get_video_duration(file_path: str) -> float:
    add_log(f"Getting Duration {file_path}")
    cmd = [ffmpeg_location, "-i", file_path, "-f", "null", "-"]
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        universal_newlines=True,
        encoding="utf-8",
        errors="replace"
    )

    duration_sec = 0.0
    for line in proc.stderr:
        if "Duration" in line and "Durations" not in line:
            match = re.search(r"Duration:\s*([^,]+)", line)
            if match:
                dur_str = match.group(1).strip()
                add_log(f"Found length {dur_str}")
                duration_sec = parse_duration_to_seconds(dur_str)
                try:
                    proc.kill()
                except Exception:
                    pass
                break
    proc.wait()
    return duration_sec


def render_console_progress(line: str, total_seconds: float):
    if total_seconds <= 0 or "time=" not in line:
        return
    match = re.search(r"time=(\d{2}:\d{2}:\d{2}(?:\.\d+)?)", line)
    if match:
        cur_sec = parse_duration_to_seconds(match.group(1))
        percent = int(min(100, max(0, (cur_sec / total_seconds) * 100)))
        filled = min(50, int(percent / 2.0))
        bar = "█" * filled + "░" * (50 - filled)
        sys.stdout.write(f"\r{bar}")
        sys.stdout.flush()


def extract_json_param(line: str, tag: str, log_name: str) -> str:
    if tag in line and "Segment" not in line:
        parts = line.split(tag)
        if len(parts) > 1:
            val = parts[1].split('"')[0]
            add_log(f"Found {log_name}: {val}")
            return val
    return ""


def normalize_audio(file_path: str, verbose: bool = False):
    global normalizing
    normalizing = True
    if verbose:
        print("Normalizing has begun!")

    ret = [0.0, 0.0]
    pad = "_NA_"
    folder = os.path.dirname(file_path)
    base_name, ext = os.path.splitext(os.path.basename(file_path))
    target_path = os.path.join(folder, f"{base_name}{pad}{ext}")

    if verbose:
        print(f"Checking if file exists {target_path}")
    if os.path.isfile(target_path) or pad in file_path:
        if verbose:
            print("File exists skipping")
        add_log("File Exists, skipping normalization")
        normalizing = False
        return ret

    # PASS 1: Loudness Analysis
    print("Generating Filter...")
    start_t1 = time.time()
    total_duration = 0.0

    input_i = ""
    input_lra = ""
    input_tp = ""
    input_thresh = ""
    input_offset = ""

    cmd_pass1 = [
        ffmpeg_location, "-y",
        "-i", file_path,
        "-vn", "-sn",
        "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json",
        "-f", "null", "-"
    ]

    proc = subprocess.Popen(
        cmd_pass1,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        universal_newlines=True,
        encoding="utf-8",
        errors="replace"
    )

    for line in proc.stderr:
        if "Duration" in line and "Segment" not in line:
            match = re.search(r"Duration:\s*([^,]+)", line)
            if match:
                total_duration = parse_duration_to_seconds(match.group(1))

        render_console_progress(line, total_duration)

        p = extract_json_param(line, '"input_i" : "', "input_i")
        if p: input_i = p
        p = extract_json_param(line, '"input_lra" : "', "input_lra")
        if p: input_lra = p
        p = extract_json_param(line, '"input_tp" : "', "input_tp")
        if p: input_tp = p
        p = extract_json_param(line, '"input_thresh" : "', "input_thresh")
        if p: input_thresh = p
        p = extract_json_param(line, '"target_offset" : "', "input_offset")
        if p: input_offset = p

    proc.wait()
    sys.stdout.write("\n\n")

    pass1_dur = time.time() - start_t1
    ret[0] = pass1_dur
    print(f"Finished Generating Filter. Took {datetime.timedelta(seconds=int(pass1_dur))}\n")

    # PASS 2: Applying Loudnorm Filter
    print("Applying audio filter...")
    start_t2 = time.time()
    filt = (
        f"loudnorm=I=-16:TP=-1.5:LRA=11:measured_I={input_i}:measured_LRA={input_lra}:"
        f"measured_TP={input_tp}:measured_thresh={input_thresh}:offset={input_offset}:"
        f"linear=true:print_format=summary"
    )

    cmd_pass2 = [
        ffmpeg_location, "-y",
        "-i", file_path,
        "-vcodec", "copy",
        "-af", filt
    ]

    if file_path.lower().endswith(".mp3"):
        cmd_pass2.extend(["-map_metadata", "0", "-id3v2_version", "3", "-write_id3v1", "1"])

    cmd_pass2.append(target_path)

    proc = subprocess.Popen(
        cmd_pass2,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        universal_newlines=True,
        encoding="utf-8",
        errors="replace"
    )

    for line in proc.stderr:
        if "Duration" in line and "Segment" not in line:
            match = re.search(r"Duration:\s*([^,]+)", line)
            if match:
                total_duration = parse_duration_to_seconds(match.group(1))
        render_console_progress(line, total_duration)

    proc.wait()
    sys.stdout.write("\n")

    pass2_dur = time.time() - start_t2
    ret[1] = pass2_dur
    print(f"\nFinished Applying Filter. Took {datetime.timedelta(seconds=int(pass2_dur))}\n")

    # Handle .commercials sidecar
    comm_src = file_path + ".commercials"
    comm_dst = target_path + ".commercials"
    if os.path.isfile(comm_src):
        print("\nRenamed commercials file!")
        try:
            shutil.move(comm_src, comm_dst)
        except Exception as ex:
            print(f"Error moving commercials sidecar: {ex}")

    # Remove original source file
    if os.path.isfile(file_path) and os.path.isfile(target_path):
        print("\nDeleted original file!")
        try:
            os.remove(file_path)
        except Exception as ex:
            print(f"Error deleting original file: {ex}")

    normalizing = False
    return ret


def scan_for_scene_cuts(file_path: str, scene_thresh: float = 0.40):
    cmd = [
        ffmpeg_location,
        "-i", file_path,
        "-vf", f"select='gt(scene,{scene_thresh:.4f})',metadata=print",
        "-an", "-f", "null", "-"
    ]
    add_log(f"scanForSceneCuts: {' '.join(cmd)}")
    scenes = []

    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        universal_newlines=True,
        encoding="utf-8",
        errors="replace"
    )

    add_log("Scanning for Scene Cuts...")
    last_dot = time.time()

    for line in proc.stderr:
        if time.time() - last_dot > 0.25:
            sys.stdout.write(".")
            sys.stdout.flush()
            last_dot = time.time()

        if "pts_time:" in line:
            idx = line.find("pts_time:")
            s_time = line[idx + 9:].strip()
            try:
                pts = float(s_time.split()[0])
                scenes.append(pts)
                sys.stdout.write(f"+{format_duration(pts)}")
                sys.stdout.flush()
            except ValueError:
                pass

    try:
        proc.kill()
    except Exception:
        pass
    proc.wait()

    sys.stdout.write("\n")
    add_log(f"Finished scene scan..... found {len(scenes)} cuts")
    return scenes


def scan_for_commercial_breaks(file_path: str, threshhold: float, wait: float,
                               min_time_add: int = 300, min_end_time: int = 59,
                               black_level: float = 0.05, add_start_end: bool = True):
    vid_dur = get_video_duration(file_path)
    cmd = [
        ffmpeg_location,
        "-i", file_path,
        "-vf", f"blackdetect=d={threshhold:.4f}:pix_th={black_level:.4f}",
        "-an", "-f", "null", "-"
    ]
    add_log(f"scanForCommercialBreaks: {' '.join(cmd)}")

    nums = []
    if add_start_end:
        nums.append(0.0)

    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        universal_newlines=True,
        encoding="utf-8",
        errors="replace"
    )

    add_log("Scanning for Commercial Breaks...")
    last_dot = time.time()
    last_break = 0.0

    for line in proc.stderr:
        if time.time() - last_dot > 0.25:
            sys.stdout.write(".")
            sys.stdout.flush()
            last_dot = time.time()

        if "blackdetect" in line:
            m_start = re.search(r"black_start:([0-9.]+)", line)
            m_dur = re.search(r"black_duration:([0-9.]+)", line)

            if m_start and m_dur:
                try:
                    b_start = float(m_start.group(1))
                    b_dur = float(m_dur.group(1))
                    b_med = b_start + (b_dur / 2.0)

                    if b_med > wait and (vid_dur - b_med) > min_end_time:
                        if (b_med - last_break) > min_time_add or len(nums) == 0:
                            nums.append(b_med)
                            last_break = b_med
                            sys.stdout.write(f"+{format_duration(b_med)}")
                            sys.stdout.flush()
                        else:
                            sys.stdout.write("x")
                            sys.stdout.flush()
                except ValueError:
                    pass

    try:
        proc.kill()
    except Exception:
        pass
    proc.wait()

    sys.stdout.write("\n")
    add_log(f"Finished scan..... found {len(nums)} breaks")

    if add_start_end:
        nums.append(vid_dur)
    return nums


def align_breaks_with_scenes(black_breaks, scene_cuts, window_seconds: float = 0.40):
    aligned = []
    total = len(black_breaks)

    for i, b_sec in enumerate(black_breaks):
        if (i == 0 and b_sec == 0.0) or (i == total - 1 and i > 0):
            aligned.append(b_sec)
            continue

        closest_diff = float("inf")
        best_scene = b_sec

        for s_sec in scene_cuts:
            diff = abs(s_sec - b_sec)
            if diff <= window_seconds and diff < closest_diff:
                closest_diff = diff
                best_scene = s_sec

        aligned.append(best_scene)
    return aligned


def check_splits(in_path: str, out_path: str, threshold: float, black_level: float, fine_tune: bool):
    if not os.path.isdir(in_path):
        print(f"Input directory does not exist: {in_path}")
        return

    os.makedirs(out_path, exist_ok=True)
    files = get_files(in_path, FILE_EXTENSIONS)

    vcodec = (
        "-c:v h264_nvenc -preset slow -profile:v high -level 3.1 -rc vbr -cq 28 -b:v 0 -maxrate 1200k -bufsize 1200k -g 60 -bf 0"
        if use_nvidia else
        "-c:v libx264 -preset fast -profile:v high -level 3.1 -crf 26 -maxrate 1200k -bufsize 1200k -g 60 -bf 2"
    )
    vf = "yadif=0:-1:1,scale=640:480,setsar=1,setdar=4/3,fps=29.97"

    batch_start = time.time()

    for file_idx, file_path in enumerate(files):
        print(f"\n[{file_idx + 1}/{len(files)}] Processing: {os.path.basename(file_path)}")
        add_log(f"Scanning compilation for individual ads: {file_path}")

        file_start = time.time()
        print("\nScanning for Black Frames...")
        black_breaks = scan_for_commercial_breaks(
            file_path, threshold, wait=0, min_time_add=12,
            min_end_time=5, black_level=black_level, add_start_end=True
        )

        if fine_tune:
            print("\nScanning for Scene Cuts...")
            scene_cuts = scan_for_scene_cuts(file_path, scene_thresh=0.20)
            breaks = align_breaks_with_scenes(black_breaks, scene_cuts, window_seconds=0.45)
        else:
            breaks = black_breaks

        if len(breaks) < 2:
            print(f"No ad transitions detected in: {os.path.basename(file_path)}")
            continue

        total_cuts = len(breaks) - 1
        print(f"Found {total_cuts} cuts.")

        split_start = time.time()
        processed_cuts = 0

        for i in range(1, total_cuts + 1):
            boundary_buffer = 0.134
            start_sec = breaks[i - 1] + boundary_buffer
            length_sec = breaks[i] - start_sec - boundary_buffer

            if length_sec < 0.0:
                continue

            pre_seek, post_seek = build_precision_seek_args(start_sec, length_sec)
            base_name = os.path.splitext(os.path.basename(file_path))[0]
            output_name = f"{base_name}_{i:03d}_{random.randint(1, 999):03d}.mp4"
            target_path = os.path.join(out_path, output_name)

            eta_string = "Estimating..."
            if processed_cuts > 0:
                ms_per_cut = (time.time() - split_start) / processed_cuts
                rem_sec = ms_per_cut * (total_cuts - i + 1)
                eta_string = f"~{int(rem_sec // 60):02d}m {int(rem_sec % 60):02d}s remaining"

            elapsed_file = time.time() - file_start
            sys.stdout.write(
                f"\rExtracting cut {i}/{total_cuts} ({round(length_sec)}s) | "
                f"Elapsed: {int(elapsed_file // 60):02d}:{int(elapsed_file % 60):02d} | ETA: {eta_string}   "
            )
            sys.stdout.flush()

            add_log(f"Exporting video #{i}: Start {start_sec:.3f}s, Duration {length_sec:.3f}s -> {output_name}")

            # Assemble ffmpeg arguments list cleanly
            cmd = [ffmpeg_location, "-y"]
            if pre_seek.strip():
                cmd.extend(pre_seek.strip().split())
            cmd.extend(["-i", file_path])
            if post_seek.strip():
                cmd.extend(post_seek.strip().split())
            cmd.extend(vcodec.split())
            cmd.extend([
                "-pix_fmt", "yuv420p", "-vsync", "cfr", "-vf", vf,
                "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "2",
                "-af", "aresample=async=1:first_pts=0",
                "-movflags", "+faststart", target_path
            ])

            subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            processed_cuts += 1

        file_dur = time.time() - file_start
        print(f"\nFinished file in {int(file_dur // 60)}m {int(file_dur % 60)}s.")

    batch_dur = time.time() - batch_start
    print(f"\nCompilation splitting complete. Total batch runtime: {datetime.timedelta(seconds=int(batch_dur))}")


# ==========================================
# File Organization Routines
# ==========================================

def add_time_string_to_file_name(file_path: str):
    pad_l = "%T("
    pad_r = ")%"

    if pad_l in file_path and pad_r in file_path:
        print("Time already added, skipping!")
        return

    folder = os.path.dirname(file_path)
    base_name, ext = os.path.splitext(os.path.basename(file_path))

    sys.stdout.write(f"Getting video length: {base_name} ... ")
    sys.stdout.flush()
    clean_name = return_clean_ascii(base_name)

    duration = get_video_duration(file_path)
    print(str(datetime.timedelta(seconds=int(duration))))

    pad = f"{pad_l}{round(duration)}{pad_r}"
    new_filename = f"{clean_name}{pad}{ext}"
    target_path = os.path.join(folder, new_filename)

    if os.path.isfile(target_path):
        print("File Exists, skipping!")
        return

    for retry in range(16):
        try:
            sys.stdout.write("Renaming file... ")
            sys.stdout.flush()
            shutil.move(file_path, target_path)
            if os.path.isfile(target_path):
                break
        except Exception:
            sys.stdout.write(f"{retry}... ")
            sys.stdout.flush()
            time.sleep(1)

    comm_src = file_path + ".commercials"
    comm_dst = target_path + ".commercials"
    if os.path.isfile(comm_src):
        print("\nRenamed commercials file!")
        try:
            shutil.move(comm_src, comm_dst)
        except Exception as ex:
            print(ex)

    print("Done!\n")


def recurse_add_duration_internal(current_dir: str):
    vid_files = get_files(current_dir, FILE_EXTENSIONS)
    for vf in vid_files:
        add_time_string_to_file_name(vf)

    try:
        for entry in os.scandir(current_dir):
            if entry.is_dir(follow_symlinks=False):
                recurse_add_duration_internal(entry.path)
    except PermissionError:
        pass


def recurse_add_duration(folder: str):
    if not folder or not os.path.isdir(folder):
        print(f"Directory does not exist: {folder}")
        return
    recurse_add_duration_internal(folder)
    print("Times have been added. Press any key to continue...")
    getch()


def move_tagged_files(dir_path: str):
    if not dir_path or not os.path.isdir(dir_path):
        print(f"Directory does not exist: {dir_path}")
        return

    tags = {
        "%AM%": "am",
        "%PM%": "pm",
        "%ANY%": "any"
    }

    print(f"Searching for tagged files in: {dir_path}")

    for entry in os.scandir(dir_path):
        if not entry.is_file():
            continue

        name = entry.name
        for tag, subfolder in tags.items():
            if tag.lower() in name.lower():
                target_dir = os.path.join(dir_path, subfolder)
                os.makedirs(target_dir, exist_ok=True)
                dest_path = os.path.join(target_dir, name)

                if os.path.exists(dest_path):
                    print(f"Destination exists, skipping: {dest_path}")
                    break

                try:
                    shutil.move(entry.path, dest_path)
                    print(f"Moved: {name} ==> {subfolder}/")

                    sidecar = entry.path + ".commercials"
                    sidecar_dest = dest_path + ".commercials"
                    if os.path.isfile(sidecar):
                        shutil.move(sidecar, sidecar_dest)
                        print(f"Moved sidecar: {os.path.basename(sidecar)} ==> {subfolder}/")
                except Exception as ex:
                    print(f"Failed to move {name}: {ex}")
                break

    print("Finished moving tagged files.\n")


# ==========================================
# Screens and Menus
# ==========================================

def draw_screen(which_one):
    clear_screen()
    if which_one == 1:
        print(r"  _   _                            _ _         ")
        print(r" | \ | |                          | (_)        ")
        print(r" |  \| | ___  _ __ _ __ ___   __ _| |_ _______ ")
        print(r" | . ` |/ _ \| '__| '_ ` _ \ / _` | | |_  / _ \ ")
        print(r" | |\  | (_) | |  | | | | | | (_| | | |/ /  __/")
        print(r" |_| \_|\___/|_|  |_| |_| |_|\__,_|_|_/___\___|")
    elif which_one == 2:
        print(r"  _____      _       _     ____                 _        ")
        print(r" |  __ \    (_)     | |   |  _ \               | |       ")
        print(r" | |__) | __ _ _ __ | |_  | |_) |_ __ ___  __ _| | _____ ")
        print(r" |  ___/ '__| | '_ \| __| |  _ <| '__/ _ \/ _` | |/ / __|")
        print(r" | |   | |  | | | | | |_  | |_) | | |  __/ (_| |   <\__ \ ")
        print(r" |_|   |_|  |_|_| |_|\__| |____/|_|  \___|\__,_|_|\_\___/")
    elif which_one == 3:
        print(r"  _________  _     ____ ______      __ __ ____ ___     ___  ___  ")
        print(r" / ___/    \| |   |    |      |    |  |  |    |   \   /  _]/   \ ")
        print(r"(   \_|  o  ) |    |  ||      |    |  |  ||  ||    \ /  [_|     |")
        print(r" \__  |   _/| |___ |  ||_|  |_|    |  |  ||  ||  D  |    _]  O  |")
        print(r" /  \ |  |  |     ||  |  |  |      |  :  ||  ||     |   [_|     |")
        print(r" \    |  |  |     ||  |  |  |       \   / |  ||     |     |     |")
        print(r"  \___|__|  |_____|____| |__|        \_/ |____|_____|_____|\___/ ")
    elif which_one == 5:
        print(r" __  __  _____  _  _  ____    ____   __    ___   ___  ____  ____     ____  ____  __    ____  ___ ")
        print(r"(  \/  )(  _  )( \/ )( ___)  (_  _) /__\  / __) / __)( ___)(  _ \   ( ___)(_  _)(  )  ( ___)/ __)")
        print(r" )    (  )(_)(  \  /  )__)     )(  /(__)\( (_-.( (_-. )__)  )(_) )   )__)  _)(_  )(__  )__) \__ \ ")
        print(r"(_/\/\_)(_____)  \/  (____)   (__)(__)(__)\___/ \___/(____)(____/   (__)  (____)(____)(____)(___/")
    elif which_one == 6:
        print(r"  _____      _       _     ____                 _        ")
        print(r" |  __ \    (_)     | |   |  _ \               | |       ")
        print(r" | |__) | __ _ _ __ | |_  | |_) |_ __ ___  __ _| | _____ ")
        print(r" |  ___/ '__| | '_ \| __| |  _ <| '__/ _ \/ _` | |/ / __|")
        print(r" | |   | |  | | | | | |_  | |_) | | |  __/ (_| |   <\__ \ ")
        print(r" |_|   |_|  |_|_| |_|\__| |____/|_|  \___|\__,_|_|\_\___/")
        print(" ------------------------------------------------------------- ")
        print(" | TEST                                                      |")
        print(" ------------------------------------------------------------- ")
    elif which_one == 7:
        print(r"  _____              __  __ U _____ u   _     ____    ____ U _____ u  ____     ")
        print(r" |_ - _|    ___    U|' \/ '|\| ___-|U  /-\  u|  _-\  |  _-\\| ___-|U |  _-\ u  ")
        print(r"   | |     |_-_|   \| |\/| |/|  _|-  \/ _ \//| | | |/| | | ||  _|-  \| |_) |/  ")
        print(r"  /| |\     | |     | |  | | | |___  / ___ \U| |_| |U| |_| || |___   |  _ <    ")
        print(r" u |_|U   U/| |\u   |_|  |_| |_____|/_/   \_\|____/ u|____/ |_____|  |_| \_\   ")
        print(r" _// \\.-,_|___|_,-<<,-,,-.  <<   >> \\    >> |||_    |||_  <<   >>  //   \\_  ")
        print(r"(__) (__\_)-' '-(_/ (./  \.)(__) (__(__)  (__(__)_)  (__)_)(__) (__)(__)  (__)")
    elif which_one == 9:
        print(r"    )    (               (         )       )   (     ")
        print(r" ( /(    )\ )    *   )   )\ )   ( /(    ( /(   )\ )  ")
        print(r" )\())  (()/(  ` )  /(  (()/(   )\())   )\()) (()/(  ")
        print(r"((_)\    /(_))  ( )(_))  /(_)) ((_)\   ((_)\   /(_)) ")
        print(r"  ((_)  (_))   (_(_())  (_))     ((_)   _((_) (_))   ")
        print(r" / _ \  | _ \  |_   _|  |_ _|   / _ \  | \| | / __|  ")
        print(r"| (_) | |  _/    | |     | |   | (_) | | .` | \__ \  ")
        print(r" \___/  |_|      |_|    |___|   \___/  |_|\_| |___/  ")
    elif which_one == "n":
        print("Remove Normalization mark from filename")
    elif which_one == "l":
        print("Replace string with string in filename")
    elif which_one == "k":
        print("Add string to end of filename")
    elif which_one == "m":
        print("Add Normalization mark to filename")
    elif which_one == "t":
        print("Remove Timestamp from filename")
    elif which_one == "u":
        print("Clean ASCII filenames")
    else:
        version = "0.4.4"
        print(r" _____ _    _   ____  _        _   _              ")
        print(r"|_   _\ \  / / / ___|| |_ __ _| |_(_) ___  _ __   ")
        print(r"  | |  \ \/ /  \___ \| __/ _` | __| |/ _ \| '_ \  ")
        print(r"  | |   \  /    ___) | || (_| | |_| | (_) | | | | ")
        print(r"  |_|    \/    |____/ \__\__,_|\__|_|\___/|_| |_| ")
        print(r"    _             _     _                         ")
        print(r"   / \   ___ ___ (_)___| |_ __ _ _ __  _|_        ")
        print(r"  / _ \ / __/ __|| / __| __/ _` | '_ \| __|       ")
        print(r" / ___ \\__ \__ \| \__ \ || (_| | | | | |_        ")
        print(r"/_/   \_\___/___/|_|___/\__\__,_|_| |_|\__|       ")
        print(f"version: {version}\n")
        print("██████████████████████████████████████████████████████████████")
        print("█                                                            █")
        print("█ Options:                                                   █")
        print("█                                                            █")
        print("█   [ 1 ] - Batch Normalize Audio   [ n ] Remove NA Mark     █")
        print("█   [ 2 ] - Print Breaks            [ u ] Remove Non-Ascii   █")
        print("█   [ 3 ] - Split Video             [ l ] Replace String     █")
        print("█   [ 5 ] - Move Tagged Files                                █")
        print("█   [ 6 ] - Test Print Breaks       [ k ] Add to Filename    █")
        print("█   [ 7 ] - Time Adder              [ t ] Remove Time        █")
        print("█                                                            █")
        print("█   [ 9 ] - Options                                          █")
        print("█                                                            █")
        print("██████████████████████████████████████████████████████████████")
    print("\n")


def process_audio_normalization_menu():
    draw_screen(1)
    paths = []
    add_paths = True

    while add_paths:
        add_paths = False
        folder = prompt_path("Enter path to video files (leave blank to finish queue, prefix with '+' to add more):", must_exist=False)
        if not folder:
            break

        if folder.startswith("+"):
            folder = folder[1:].strip("\"'")
            add_paths = True

        if not os.path.isdir(folder):
            draw_message(f"Path does not exist: {folder}")
        else:
            paths.append(folder)
            print(f"\n{folder} added to queue ({len(paths)} total)\n")

    if not paths:
        return

    total_filter = 0.0
    total_apply = 0.0

    for folder in paths:
        files = get_files(folder, FILE_EXTENSIONS, ["_NA_"])
        total_files = len(files)
        processed = 0

        for i, file_path in enumerate(files):
            draw_screen(1)
            elapsed = total_filter + total_apply
            avg_seconds = elapsed / processed if processed > 0 else 0
            eta = avg_seconds * (total_files - processed)

            print(f"ETA: {datetime.timedelta(seconds=int(eta))} | Elapsed: {datetime.timedelta(seconds=int(elapsed))}\n")
            print(f"Normalizing audio: {os.path.basename(file_path)}")
            print(f"File {i + 1} of {total_files}\n")

            run_times = normalize_audio(file_path)
            total_filter += run_times[0]
            total_apply += run_times[1]

            if run_times[0] > 0:
                processed += 1

    total_norm = total_filter + total_apply
    draw_message(f"Normalization complete!\nTotal runtime: {datetime.timedelta(seconds=int(total_norm))}")


def process_print_breaks_menu():
    draw_screen(2)
    print_folder = prompt_path("Enter path to video files: (leave blank to cancel)")
    if not print_folder:
        return

    break_files = get_files(print_folder, FILE_EXTENSIONS)
    if not break_files:
        draw_message("No matching video files found in directory.")
        return

    p_thresh = prompt_double("Enter threshold (length of black frames in seconds):", 0.5)
    p_black = prompt_double("Enter black luminance (pure=0, black=0.05, light black=0.1):", 0.05)
    p_min_comm = prompt_int("Enter minimum detected breaks required to write file:", 3)
    p_wait = prompt_double("Enter minimum start time (seconds from beginning):", 0.0)
    p_min_between = prompt_int("Enter minimum length between breaks (seconds):", 300)
    p_end_pad = prompt_int("Enter time from end of video to stop looking (seconds):", 0)
    synth_breaks = prompt_bool("Fabricate synthetic breaks if none are detected?", False)

    for file_path in break_files:
        commercial_file = os.path.join(print_folder, os.path.basename(file_path) + ".commercials")
        if os.path.isfile(commercial_file):
            add_log(f"Commercials metadata already exists for: {os.path.basename(file_path)}")
            continue

        print(f"\nScanning: {os.path.basename(file_path)}")
        cms = scan_for_commercial_breaks(file_path, p_thresh, p_wait, p_min_between, p_end_pad, p_black, False)

        if len(cms) >= p_min_comm:
            with open(commercial_file, "w", encoding="utf-8") as f:
                f.write("\n".join(f"{sec:.6f}" for sec in cms))
            add_log(f"Printed breaks: {commercial_file}")
            print(f"Wrote {len(cms)} breaks.")
        elif synth_breaks:
            print("Insufficient breaks found. Generating synthetic intervals...")
            dur = get_video_duration(file_path)
            if dur > 600:
                synth = [str(s) for s in range(600, int(dur), 600)]
                if synth:
                    with open(commercial_file, "w", encoding="utf-8") as f:
                        f.write("\n".join(synth))
        else:
            print(f"Found {len(cms)} breaks (under threshold of {p_min_comm}). Skipped.")

    print("\nPress any key to continue...")
    getch()
    draw_message("Print Breaks complete!")


def process_split_video_menu():
    draw_screen(3)
    split_in = prompt_path("Enter path to input video files:")
    if not split_in:
        return

    print("\nEnter output path (leave blank for default '\\output\\'):")
    split_out = input("> ").strip().strip("\"'")

    if not split_out:
        split_out = os.path.join(split_in, "output")
    else:
        split_out = os.path.join(split_in, split_out)

    if not os.path.isdir(split_out):
        os.makedirs(split_out, exist_ok=True)
        print(f"\n** {split_out} directory was created. **\n")
    else:
        draw_message(f"{split_out} exists.\nFiles may be overwritten.\nProceed with caution.")

    s_thresh = prompt_double("Enter threshold (length of black dip in seconds):", 0.5)
    s_black = prompt_double("Enter black level (e.g. 0.05):", 0.05)
    b_fine = prompt_bool("Fine Tune?", False)

    check_splits(split_in, split_out, s_thresh, s_black, b_fine)
    draw_message("Splits complete!")
    add_log(f"Splits complete for: {split_in}")


def process_move_tagged_files_menu():
    draw_screen(5)
    bbars_folder = prompt_path("Enter path to video files:")
    if not bbars_folder:
        return

    move_tagged_files(bbars_folder)
    print("\nFile moving complete. Press any key to continue...")
    getch()


def process_test_single_break_scan_menu():
    draw_screen(6)
    test_file = prompt_path("Enter path to single video file:", must_exist=True, is_file=True)
    if not test_file:
        return

    t_thresh = prompt_double("Enter threshold (default 0.5s):", 0.5)
    t_black = prompt_double("Enter black luminance (default 0.10):", 0.10)
    t_wait = prompt_double("Enter minimum start time (default 0s):", 0.0)
    t_length = prompt_int("Enter minimum spacing between breaks (default 300s):", 300)
    t_end = prompt_int("Enter end cutoff padding (default 0s):", 0)

    print("\nScanning for commercials...")
    test_breaks = scan_for_commercial_breaks(test_file, t_thresh, t_wait, t_length, t_end, t_black, False)

    print(f"\nFound {len(test_breaks)} commercial(s):")
    for sec in test_breaks:
        print(f"Break at: {format_duration(sec)} ({round(sec, 2)}s)")

    print("\nPress any key to return to menu...")
    getch()


def process_add_duration_to_filenames_menu():
    draw_screen(7)
    dur_folder = prompt_path("Enter path to video files:")
    if not dur_folder:
        return
    recurse_add_duration(dur_folder)


def process_settings_menu(base_dir: str):
    global ffmpeg_location, temp_folder, log_folder, use_nvidia, nvenc_preset
    draw_screen(9)

    floc = prompt_setting("Enter FFMPEG Location:", ffmpeg_location)
    if not os.path.isfile(floc):
        draw_message(f"FFmpeg binary not found at: {floc}")
        return
    ffmpeg_location = floc

    tloc = prompt_setting("Enter TEMP Path:", temp_folder)
    if tloc and not os.path.isdir(tloc):
        try:
            os.makedirs(tloc, exist_ok=True)
        except Exception as ex:
            print(f"Warning: Could not create temp directory: {ex}")
    temp_folder = tloc

    lloc = prompt_setting("Enter Log File Location:", log_folder)
    if lloc and not os.path.isdir(lloc):
        try:
            os.makedirs(lloc, exist_ok=True)
        except Exception as ex:
            print(f"Warning: Could not create log directory: {ex}")
    log_folder = lloc

    sys.stdout.write("\nTesting for NVIDIA NVENC hardware encoder... ")
    sys.stdout.flush()
    nvenc_available = detect_nvidia_encoder()

    if nvenc_available:
        print(f"\033[92m[AVAILABLE - Preset: {nvenc_preset}]\033[0m")
    else:
        print("\033[93m[NOT DETECTED / FAILED]\033[0m")

    use_nvidia = prompt_bool("Enable NVIDIA Hardware Acceleration?", nvenc_available)
    print(f"NVIDIA acceleration set to: {'ENABLED' if use_nvidia else 'DISABLED'}")

    settings_content = (
        f"ffmpeg location={ffmpeg_location}\n"
        f"temp folder={temp_folder}\n"
        f"log file={log_folder}\n"
        f"use nvidia={'true' if use_nvidia else 'false'}\n"
    )

    settings_file = os.path.join(base_dir, "settings.txt")
    with open(settings_file, "w", encoding="utf-8") as f:
        f.write(settings_content)

    draw_message("Settings saved successfully!")


def process_remove_normalization_mark_menu():
    draw_screen("n")
    folder = prompt_path("Enter path to files:")
    if not folder:
        return

    for file_path in get_files(folder):
        base, ext = os.path.splitext(os.path.basename(file_path))
        if "_NA_" in base:
            new_path = os.path.join(folder, base.replace("_NA_", "") + ext)
            try:
                shutil.move(file_path, new_path)
                print(f"{os.path.basename(file_path)} ==> {os.path.basename(new_path)}")
            except Exception as ex:
                print(ex)

    print("\nNormalization tags removed. Press any key to continue...")
    getch()


def process_append_prepend_filenames_menu():
    draw_screen("k")
    folder = prompt_path("Enter path to files:")
    if not folder:
        return

    print("\nEnter string to APPEND to filenames (leave blank to skip):")
    append_str = input("> ")
    if append_str:
        for file_path in get_files(folder):
            base, ext = os.path.splitext(os.path.basename(file_path))
            if append_str not in base:
                new_path = os.path.join(folder, f"{base}{append_str}{ext}")
                try:
                    shutil.move(file_path, new_path)
                    print(f"{os.path.basename(file_path)} ==> {os.path.basename(new_path)}")
                except Exception as ex:
                    print(ex)

    print("\nEnter string to PREPEND to filenames (leave blank to skip):")
    prepend_str = input("> ")
    if prepend_str:
        for file_path in get_files(folder):
            base, ext = os.path.splitext(os.path.basename(file_path))
            if not base.startswith(prepend_str):
                new_path = os.path.join(folder, f"{prepend_str}{base}{ext}")
                try:
                    shutil.move(file_path, new_path)
                    print(f"{os.path.basename(file_path)} ==> {os.path.basename(new_path)}")
                except Exception as ex:
                    print(ex)

    print("\nRenaming complete. Press any key to continue...")
    getch()


def process_find_replace_filenames_menu():
    draw_screen("l")
    folder = prompt_path("Enter path to files:")
    if not folder:
        return

    find_str = input("\nEnter string to find:\n> ")
    if not find_str:
        return
    replace_str = input("\nEnter string to replace with:\n> ")

    for file_path in get_files(folder):
        base, ext = os.path.splitext(os.path.basename(file_path))
        if find_str in base:
            # Preserve _NA_ tag identity during replacement
            new_base = base.replace("_NA_", "|||||").replace(find_str, replace_str).replace("|||||", "_NA_")
            new_path = os.path.join(folder, new_base + ext)
            try:
                shutil.move(file_path, new_path)
                print(f"{os.path.basename(file_path)} ==> {os.path.basename(new_path)}")
            except Exception as ex:
                print(ex)

    print(f"\nReplaced '{find_str}' with '{replace_str}'. Press any key to continue...")
    getch()


def process_add_normalization_mark_menu():
    draw_screen("m")
    folder = prompt_path("Enter path to files:")
    if not folder:
        return

    for file_path in get_files(folder):
        base, ext = os.path.splitext(os.path.basename(file_path))
        if "_NA_" not in base:
            if ext.lower() == ".commercials":
                inner_base, inner_ext = os.path.splitext(base)
                target_name = f"{inner_base}_NA_{inner_ext}.commercials"
            else:
                target_name = f"{base}_NA_{ext}"

            new_path = os.path.join(folder, target_name)
            try:
                shutil.move(file_path, new_path)
                print(f"{os.path.basename(file_path)} ==> {target_name}")
            except Exception as ex:
                print(ex)

    print("\n_NA_ markers added. Press any key to continue...")
    getch()


def process_clean_ascii_filenames_menu():
    draw_screen("u")
    folder = prompt_path("Enter path to files:")
    if not folder:
        return

    for file_path in get_files(folder):
        base, ext = os.path.splitext(os.path.basename(file_path))
        clean_name = return_clean_ascii(base)
        if base != clean_name:
            new_path = os.path.join(folder, clean_name + ext)
            try:
                shutil.move(file_path, new_path)
                print(f"{base}{ext} ==> {clean_name}{ext}")
            except Exception as ex:
                print(ex)

    print("\nClean ASCII filenames complete. Press any key to continue...")
    getch()


def process_remove_timestamp_tokens_menu():
    draw_screen("t")
    folder = prompt_path("Enter path to files:")
    if not folder:
        return

    for file_path in get_files(folder):
        base, ext = os.path.splitext(os.path.basename(file_path))
        start_token = base.find("%T(")
        end_token = base.find(")%", start_token + 3) if start_token > -1 else -1

        if start_token > -1 and end_token > -1:
            clean_name = base[:start_token] + base[end_token + 2:]
            new_path = os.path.join(folder, clean_name + ext)
            try:
                shutil.move(file_path, new_path)
                print(f"{base}{ext} ==> {clean_name}{ext}")
            except Exception as ex:
                print(ex)

    print("\nTimestamp tokens removed. Press any key to continue...")
    getch()


# ==========================================
# Startup & Configuration Loading
# ==========================================

def load_settings(settings_file: str):
    global ffmpeg_location, temp_folder, log_folder, use_nvidia
    if not os.path.isfile(settings_file):
        return

    with open(settings_file, "r", encoding="utf-8") as f:
        for raw_line in f:
            line = raw_line.strip()
            if not line or line.startswith("#"):
                continue

            parts = line.split("=", 1)
            if len(parts) < 2:
                continue

            key = parts[0].strip().lower()
            val = parts[1].strip()

            if key == "ffmpeg location":
                print(f"Setting FFMPEG location to {val}")
                ffmpeg_location = val
            elif key == "temp folder":
                print(f"Setting Temp Folder to {val}")
                temp_folder = val
            elif key == "log file":
                print(f"Setting Log File location to {val}")
                log_folder = val
            elif key == "use nvidia":
                use_nvidia = (val.lower() == "true")
                print(f"Setting NVIDIA Hardware Acceleration to {'ENABLED' if use_nvidia else 'DISABLED'}")


def handle_command_line_arguments(args: list) -> bool:
    if not args:
        return False

    cli_handled = False
    i = 0
    while i < len(args):
        flag = args[i].lower().strip()
        if flag in ("--normalize", "-n"):
            cli_handled = True
            if i + 1 >= len(args):
                print(f"Error: Missing file path after {args[i]}")
                break

            file_path = args[i + 1]
            if os.path.isfile(file_path):
                if "_NA_" in file_path:
                    print(f"{file_path} has already been normalized.")
                else:
                    print(f"Normalizing file: {file_path}")
                    normalize_audio(file_path, verbose=True)
                    while normalizing:
                        print("Normalizing...")
                        time.sleep(3)
                    print("Normalization complete.")
            else:
                print(f"{file_path} does not exist.")
            i += 1
        i += 1

    return cli_handled


def main():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    settings_file = os.path.join(base_dir, "settings.txt")

    load_settings(settings_file)
    reset_log()

    if handle_command_line_arguments(sys.argv[1:]):
        return

    time.sleep(2)

    while True:
        draw_screen(0)
        key = getch()

        if key in ("\x1b", "\x03"):  # ESC or Ctrl+C
            sys.exit(0)

        selected_option = key.lower() if key else ""
        if not ffmpeg_location:
            selected_option = "9"

        if selected_option == "1":
            process_audio_normalization_menu()
        elif selected_option == "2":
            process_print_breaks_menu()
        elif selected_option == "3":
            process_split_video_menu()
        elif selected_option == "4":
            test_file = r"D:\Video\80's Commercials Vol. 1180 [h__MbJfmWh4]_NA_.webm"
            black_breaks = scan_for_commercial_breaks(test_file, 0.1, wait=0.1, min_time_add=0, min_end_time=0, black_level=0.1, add_start_end=True)
            scene_cuts = scan_for_scene_cuts(test_file, scene_thresh=0.40)
            breaks = align_breaks_with_scenes(black_breaks, scene_cuts, window_seconds=0.45)
            for b in breaks:
                print(format_duration(b))
            getch()
        elif selected_option == "5":
            process_move_tagged_files_menu()
        elif selected_option == "6":
            process_test_single_break_scan_menu()
        elif selected_option == "7":
            process_add_duration_to_filenames_menu()
        elif selected_option == "9":
            process_settings_menu(base_dir)
        elif selected_option == "n":
            process_remove_normalization_mark_menu()
        elif selected_option == "k":
            process_append_prepend_filenames_menu()
        elif selected_option == "l":
            process_find_replace_filenames_menu()
        elif selected_option == "m":
            process_add_normalization_mark_menu()
        elif selected_option == "u":
            process_clean_ascii_filenames_menu()
        elif selected_option == "t":
            process_remove_timestamp_tokens_menu()
        else:
            draw_message("Invalid Entry!")


if __name__ == "__main__":
    main()