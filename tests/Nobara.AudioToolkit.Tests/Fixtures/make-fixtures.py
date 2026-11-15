# Copyright (c) 2026 Maël Kobura
# SPDX-License-Identifier: MIT
#
# Writes the FLAC fixtures of the tests and their reference samples, from a
# generated signal: run it from this folder with ffmpeg on the PATH. The
# signal is a chirp with a little noise, then a stretch of silence, so that
# the encoder uses constant, fixed, and LPC subframes.
import math, random, struct, subprocess

def signal(rate, channels, seconds):
    random.seed(7)
    frames = int(rate * seconds)
    phase = [0.0] * channels
    data = []
    for i in range(frames):
        t = i / frames
        for c in range(channels):
            if t > 0.8:
                data.append(0.0)
                continue
            f = 200 + t * (rate / 5 - 200) + c * 70
            phase[c] += 2 * math.pi * f / rate
            data.append(0.45 * math.sin(phase[c]) + 0.02 * (random.random() - 0.5))
    return data

def run(*args):
    subprocess.run(["ffmpeg", "-v", "error", "-y", *args], check=True)

cases = [
    # name, rate, channels, sample format, ffmpeg options
    ("mono16", 44100, 1, "s16", ["-compression_level", "5"]),
    ("stereo16", 44100, 2, "s16", ["-compression_level", "8"]),
    ("stereo16-fixed", 44100, 2, "s16", ["-compression_level", "0"]),
    ("stereo24", 48000, 2, "s32", ["-compression_level", "8", "-bits_per_raw_sample", "24"]),
    ("mono16-oddblock", 22050, 1, "s16", ["-frame_size", "1000"]),
]
for name, rate, channels, fmt, options in cases:
    data = signal(rate, channels, 0.5)
    open("source.f32", "wb").write(struct.pack("<%df" % len(data), *data))
    run("-f", "f32le", "-ar", str(rate), "-ac", str(channels), "-i", "source.f32",
        "-c:a", "flac", "-sample_fmt", fmt, *options, f"{name}.flac")
    # The reference: the samples of the FLAC file as 32-bit integers, scaled to the top bits.
    run("-i", f"{name}.flac", "-f", "s32le", "-c:a", "pcm_s32le", f"{name}.s32")
