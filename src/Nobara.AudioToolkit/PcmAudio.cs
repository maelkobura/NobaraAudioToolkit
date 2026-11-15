// Copyright (c) 2026 Maël Kobura
// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace Nobara.AudioToolkit;

/// <summary>
/// Decoded audio: interleaved samples from -1 to 1, left then right for
/// stereo, with their channel count and sample rate.
/// </summary>
public sealed class PcmAudio
{
    /// <summary>Creates audio from interleaved samples.</summary>
    /// <param name="channels">The number of channels, at least 1.</param>
    /// <param name="sampleRate">The frames per second, at least 1.</param>
    /// <param name="samples">The samples; their count is a multiple of <paramref name="channels"/>.</param>
    public PcmAudio(int channels, int sampleRate, float[] samples)
    {
        if (channels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Audio has at least one channel.");
        }

        if (sampleRate < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "The sample rate is at least 1.");
        }

        Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        if (samples.Length % channels != 0)
        {
            throw new ArgumentException($"{samples.Length} samples do not make whole frames of {channels} channels.", nameof(samples));
        }

        Channels = channels;
        SampleRate = sampleRate;
    }

    /// <summary>Gets the number of channels.</summary>
    public int Channels { get; }

    /// <summary>Gets the frames per second.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the interleaved samples, from -1 to 1.</summary>
    public float[] Samples { get; }

    /// <summary>Gets the length in frames, one sample of each channel.</summary>
    public long Frames => Samples.Length / Channels;

    /// <summary>Gets the length in time.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    /// <summary>Mixes the channels down to one, by their mean.</summary>
    /// <returns>Mono audio; this instance when it is already mono.</returns>
    public PcmAudio ToMono()
    {
        if (Channels == 1)
        {
            return this;
        }

        var mono = new float[Frames];
        float scale = 1f / Channels;
        for (long frame = 0; frame < mono.Length; frame++)
        {
            float sum = 0;
            long first = frame * Channels;
            for (int c = 0; c < Channels; c++)
            {
                sum += Samples[first + c];
            }

            mono[frame] = sum * scale;
        }

        return new PcmAudio(1, SampleRate, mono);
    }
}

/// <summary>The container format of an audio file, recognized by its first bytes.</summary>
public enum AudioFormat
{
    /// <summary>Not a format the toolkit knows.</summary>
    Unknown,

    /// <summary>RIFF WAVE.</summary>
    Wav,

    /// <summary>Native FLAC.</summary>
    Flac,

    /// <summary>Ogg, which usually holds Vorbis.</summary>
    Ogg,
}

/// <summary>Thrown when an audio file is malformed or uses a variant that the toolkit does not read.</summary>
public sealed class AudioFormatException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong with the file.</param>
    public AudioFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>Recognizes and decodes the source formats that the toolkit reads.</summary>
public static class AudioFile
{
    /// <summary>Recognizes the format of a file by its first bytes.</summary>
    /// <param name="data">The file, or its first 12 bytes at least.</param>
    /// <returns>The format, or <see cref="AudioFormat.Unknown"/>.</returns>
    public static AudioFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E')
        {
            return AudioFormat.Wav;
        }

        if (data.Length >= 4 && data[0] == 'f' && data[1] == 'L' && data[2] == 'a' && data[3] == 'C')
        {
            return AudioFormat.Flac;
        }

        if (data.Length >= 4 && data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
        {
            return AudioFormat.Ogg;
        }

        return AudioFormat.Unknown;
    }

    /// <summary>Decodes a WAV or FLAC file.</summary>
    /// <param name="data">The whole file.</param>
    /// <returns>The decoded audio.</returns>
    /// <exception cref="AudioFormatException">The file is not a WAV or FLAC file that the toolkit reads.</exception>
    public static PcmAudio Decode(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        return Detect(data) switch
        {
            AudioFormat.Wav => WavReader.Read(data),
            AudioFormat.Flac => FlacDecoder.Decode(data),
            AudioFormat.Ogg => throw new AudioFormatException("the file is Ogg; decode it with a Vorbis decoder"),
            _ => throw new AudioFormatException("the file is neither WAV nor FLAC"),
        };
    }
}
