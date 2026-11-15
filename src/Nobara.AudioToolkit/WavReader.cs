// Copyright (c) 2026 Maël Kobura
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;

namespace Nobara.AudioToolkit;

/// <summary>
/// Reads RIFF WAVE files: integer PCM of 8, 16, 24, or 32 bits and 32-bit
/// float, in the plain or the extensible header.
/// </summary>
public static class WavReader
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>Decodes a WAV file.</summary>
    /// <param name="data">The whole file.</param>
    /// <returns>The decoded audio.</returns>
    /// <exception cref="AudioFormatException">The file is malformed or uses an encoding that the reader does not decode.</exception>
    public static PcmAudio Read(ReadOnlySpan<byte> data)
    {
        if (AudioFile.Detect(data) != AudioFormat.Wav)
        {
            throw new AudioFormatException("the file is not a RIFF WAVE file");
        }

        int position = 12;
        int channels = 0, sampleRate = 0, bits = 0;
        ushort format = 0;
        bool hasFormat = false;
        while (position + 8 <= data.Length)
        {
            ReadOnlySpan<byte> id = data.Slice(position, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4, 4));
            int body = position + 8;
            long available = data.Length - body;

            if (Is(id, "fmt "))
            {
                if (size < 16 || size > available)
                {
                    throw new AudioFormatException("the fmt chunk is truncated");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 14, 2));
                if (format == FormatExtensible)
                {
                    if (size < 40)
                    {
                        throw new AudioFormatException("the extensible fmt chunk is truncated");
                    }

                    // The sub-format GUID starts with the format code.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(body + 24, 2));
                }

                hasFormat = true;
            }
            else if (Is(id, "data"))
            {
                if (!hasFormat)
                {
                    throw new AudioFormatException("the data chunk comes before the fmt chunk");
                }

                // A writer that streamed the file may leave the size of the last chunk too large.
                int length = (int)Math.Min(size, available);
                return Decode(data.Slice(body, length), format, channels, sampleRate, bits);
            }

            // Chunks are padded to an even size.
            long next = body + (long)size + (size & 1);
            if (next > data.Length)
            {
                break;
            }

            position = (int)next;
        }

        throw new AudioFormatException(hasFormat ? "the file has no data chunk" : "the file has no fmt chunk");
    }

    private static PcmAudio Decode(ReadOnlySpan<byte> data, ushort format, int channels, int sampleRate, int bits)
    {
        if (channels < 1)
        {
            throw new AudioFormatException("the file has no channel");
        }

        if (sampleRate < 1)
        {
            throw new AudioFormatException("the file has no sample rate");
        }

        int bytes = bits / 8;
        bool supported = (format == FormatPcm && bits is 8 or 16 or 24 or 32) || (format == FormatFloat && bits == 32);
        if (!supported)
        {
            string kind = format switch
            {
                FormatPcm => $"{bits}-bit integer PCM",
                FormatFloat => $"{bits}-bit float",
                _ => $"format code {format}",
            };
            throw new AudioFormatException($"the file is {kind}; WAV files are read as 8, 16, 24, or 32-bit integer PCM, or 32-bit float");
        }

        int frameSize = bytes * channels;
        int frames = data.Length / frameSize;
        var samples = new float[frames * channels];
        for (int i = 0; i < samples.Length; i++)
        {
            ReadOnlySpan<byte> s = data.Slice(i * bytes, bytes);
            samples[i] = format == FormatFloat
                ? BinaryPrimitives.ReadSingleLittleEndian(s)
                : bits switch
                {
                    8 => (s[0] - 128) / 128f,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    24 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                };
        }

        return new PcmAudio(channels, sampleRate, samples);
    }

    private static bool Is(ReadOnlySpan<byte> id, string name) =>
        id[0] == name[0] && id[1] == name[1] && id[2] == name[2] && id[3] == name[3];
}
