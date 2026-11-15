// Copyright (c) 2026 Maël Kobura
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Nobara.AudioToolkit;

/// <summary>
/// Decodes native FLAC files (RFC 9639): every subframe type, every channel
/// assignment, and 4 to 32 bits per sample. The decoded samples are checked
/// against the MD5 signature of the stream when it has one.
/// </summary>
public static class FlacDecoder
{
    private static readonly int[] SampleRates = { 0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };

    /// <summary>Decodes a FLAC file.</summary>
    /// <param name="data">The whole file.</param>
    /// <returns>The decoded audio.</returns>
    /// <exception cref="AudioFormatException">The file is malformed, or its decoded samples do not match its signature.</exception>
    public static PcmAudio Decode(byte[] data)
    {
        if (data is null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (AudioFile.Detect(data) != AudioFormat.Flac)
        {
            throw new AudioFormatException("the file is not a FLAC file");
        }

        var reader = new BitReader(data, 4);
        StreamInfo info = ReadMetadata(reader);
        var channels = new List<long>[info.Channels];
        for (int c = 0; c < channels.Length; c++)
        {
            channels[c] = new List<long>(info.TotalSamples > 0 && info.TotalSamples < int.MaxValue ? (int)info.TotalSamples : 0);
        }

        var block = new long[info.Channels][];
        while (reader.BytesLeft >= 2)
        {
            int frames = ReadFrame(reader, info, block);
            for (int c = 0; c < info.Channels; c++)
            {
                for (int i = 0; i < frames; i++)
                {
                    channels[c].Add(block[c][i]);
                }
            }
        }

        int count = channels[0].Count;
        if (info.TotalSamples > 0 && info.TotalSamples != count)
        {
            throw new AudioFormatException($"the stream declares {info.TotalSamples} samples per channel and holds {count}");
        }

        CheckSignature(info, channels, count);
        var samples = new float[count * info.Channels];
        float scale = 1f / (1L << (info.BitsPerSample - 1));
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < info.Channels; c++)
            {
                samples[(i * info.Channels) + c] = channels[c][i] * scale;
            }
        }

        return new PcmAudio(info.Channels, info.SampleRate, samples);
    }

    private static StreamInfo ReadMetadata(BitReader reader)
    {
        StreamInfo? info = null;
        bool last = false;
        while (!last)
        {
            if (reader.BytesLeft < 4)
            {
                throw new AudioFormatException("the metadata is truncated");
            }

            last = reader.Read(1) == 1;
            int type = (int)reader.Read(7);
            int length = (int)reader.Read(24);
            if (reader.BytesLeft < length)
            {
                throw new AudioFormatException("a metadata block is truncated");
            }

            if (type == 0)
            {
                if (length != 34)
                {
                    throw new AudioFormatException("the STREAMINFO block does not have 34 bytes");
                }

                reader.Read(16); // minimum block size
                int maxBlock = (int)reader.Read(16);
                reader.Read(24); // minimum frame size
                reader.Read(24); // maximum frame size
                int rate = (int)reader.Read(20);
                int channels = (int)reader.Read(3) + 1;
                int bits = (int)reader.Read(5) + 1;
                long total = (long)reader.Read(36);
                byte[] md5 = new byte[16];
                for (int i = 0; i < 16; i++)
                {
                    md5[i] = (byte)reader.Read(8);
                }

                if (rate == 0)
                {
                    throw new AudioFormatException("the stream has no sample rate");
                }

                if (bits < 4)
                {
                    throw new AudioFormatException($"the stream has {bits} bits per sample; FLAC has at least 4");
                }

                info = new StreamInfo(rate, channels, bits, total, maxBlock, md5);
            }
            else
            {
                reader.SkipBytes(length);
            }
        }

        return info ?? throw new AudioFormatException("the file has no STREAMINFO block");
    }

    private static int ReadFrame(BitReader reader, StreamInfo info, long[][] block)
    {
        if (reader.Read(14) != 0x3FFE)
        {
            throw new AudioFormatException($"no frame sync code at byte {reader.BytePosition - 2}");
        }

        if (reader.Read(1) != 0)
        {
            throw new AudioFormatException("a frame header has its reserved bit set");
        }

        reader.Read(1); // blocking strategy: fixed or variable, the decoding is the same
        int sizeCode = (int)reader.Read(4);
        int rateCode = (int)reader.Read(4);
        int channelCode = (int)reader.Read(4);
        int bitsCode = (int)reader.Read(3);
        reader.Read(1);
        ReadCodedNumber(reader);

        int size = sizeCode switch
        {
            0 => throw new AudioFormatException("a frame uses the reserved block size code"),
            1 => 192,
            >= 2 and <= 5 => 576 << (sizeCode - 2),
            6 => (int)reader.Read(8) + 1,
            7 => (int)reader.Read(16) + 1,
            _ => 256 << (sizeCode - 8),
        };

        switch (rateCode)
        {
            case 12: reader.Read(8); break;
            case 13: reader.Read(16); break;
            case 14: reader.Read(16); break;
            case 15: throw new AudioFormatException("a frame uses the invalid sample rate code");
        }

        if (rateCode is > 0 and < 12 && SampleRates[rateCode] != info.SampleRate)
        {
            throw new AudioFormatException("a frame changes the sample rate of the stream");
        }

        int bits = bitsCode switch
        {
            0 => info.BitsPerSample,
            1 => 8,
            2 => 12,
            3 => throw new AudioFormatException("a frame uses the reserved sample size code"),
            4 => 16,
            5 => 20,
            6 => 24,
            _ => 32,
        };

        if (bits != info.BitsPerSample)
        {
            throw new AudioFormatException("a frame changes the bits per sample of the stream");
        }

        int channels = channelCode <= 7 ? channelCode + 1 : 2;
        if (channelCode > 10)
        {
            throw new AudioFormatException("a frame uses a reserved channel assignment");
        }

        if (channels != info.Channels)
        {
            throw new AudioFormatException("a frame changes the number of channels of the stream");
        }

        reader.Read(8); // CRC-8 of the header

        for (int c = 0; c < channels; c++)
        {
            if (block[c] is null || block[c].Length < size)
            {
                block[c] = new long[Math.Max(size, info.MaxBlockSize)];
            }

            // The side channel takes one more bit.
            bool side = (channelCode == 8 && c == 1) || (channelCode == 9 && c == 0) || (channelCode == 10 && c == 1);
            ReadSubframe(reader, block[c], size, bits + (side ? 1 : 0));
        }

        Decorrelate(channelCode, block, size);
        reader.AlignToByte();
        reader.Read(16); // CRC-16 of the frame
        return size;
    }

    private static void ReadCodedNumber(BitReader reader)
    {
        uint first = (uint)reader.Read(8);
        int extra = first < 0x80 ? 0 : first >= 0xFE ? 6 : first >= 0xFC ? 5 : first >= 0xF8 ? 4 : first >= 0xF0 ? 3 : first >= 0xE0 ? 2 : first >= 0xC0 ? 1 : -1;
        if (extra < 0)
        {
            throw new AudioFormatException("a frame has a malformed frame number");
        }

        for (int i = 0; i < extra; i++)
        {
            if ((reader.Read(8) & 0xC0) != 0x80)
            {
                throw new AudioFormatException("a frame has a malformed frame number");
            }
        }
    }

    private static void ReadSubframe(BitReader reader, long[] samples, int size, int bits)
    {
        if (reader.Read(1) != 0)
        {
            throw new AudioFormatException("a subframe header has its padding bit set");
        }

        int type = (int)reader.Read(6);
        int wasted = 0;
        if (reader.Read(1) == 1)
        {
            wasted = 1;
            while (reader.Read(1) == 0)
            {
                wasted++;
            }
        }

        bits -= wasted;
        if (bits <= 0)
        {
            throw new AudioFormatException("a subframe has more wasted bits than bits per sample");
        }

        if (type == 0)
        {
            long value = reader.ReadSigned(bits);
            for (int i = 0; i < size; i++)
            {
                samples[i] = value;
            }
        }
        else if (type == 1)
        {
            for (int i = 0; i < size; i++)
            {
                samples[i] = reader.ReadSigned(bits);
            }
        }
        else if (type >= 8 && type <= 12)
        {
            int order = type - 8;
            ReadWarmUp(reader, samples, order, size, bits);
            ReadResidual(reader, samples, order, size);
            PredictFixed(samples, order, size);
        }
        else if (type >= 32)
        {
            int order = type - 31;
            ReadWarmUp(reader, samples, order, size, bits);
            int precision = (int)reader.Read(4) + 1;
            if (precision == 16)
            {
                throw new AudioFormatException("a subframe has an invalid coefficient precision");
            }

            int shift = (int)reader.ReadSigned(5);
            if (shift < 0)
            {
                throw new AudioFormatException("a subframe has a negative prediction shift");
            }

            var coefficients = new long[order];
            for (int i = 0; i < order; i++)
            {
                coefficients[i] = reader.ReadSigned(precision);
            }

            ReadResidual(reader, samples, order, size);
            for (int i = order; i < size; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++)
                {
                    sum += coefficients[j] * samples[i - 1 - j];
                }

                samples[i] += sum >> shift;
            }
        }
        else
        {
            throw new AudioFormatException($"a subframe uses the reserved type {type}");
        }

        if (wasted > 0)
        {
            for (int i = 0; i < size; i++)
            {
                samples[i] <<= wasted;
            }
        }
    }

    private static void ReadWarmUp(BitReader reader, long[] samples, int order, int size, int bits)
    {
        if (order > size)
        {
            throw new AudioFormatException("a subframe has a predictor order larger than its block");
        }

        for (int i = 0; i < order; i++)
        {
            samples[i] = reader.ReadSigned(bits);
        }
    }

    // Reads the residual into samples[order..size], before the prediction is added.
    private static void ReadResidual(BitReader reader, long[] samples, int order, int size)
    {
        int method = (int)reader.Read(2);
        if (method > 1)
        {
            throw new AudioFormatException("a subframe uses a reserved residual coding method");
        }

        int parameterBits = method == 0 ? 4 : 5;
        int escape = method == 0 ? 15 : 31;
        int partitionOrder = (int)reader.Read(4);
        int partitions = 1 << partitionOrder;
        if ((size % partitions) != 0 || (size >> partitionOrder) < order)
        {
            throw new AudioFormatException("a subframe has a partition order that does not fit its block");
        }

        int index = order;
        for (int p = 0; p < partitions; p++)
        {
            int count = (size >> partitionOrder) - (p == 0 ? order : 0);
            int parameter = (int)reader.Read(parameterBits);
            if (parameter == escape)
            {
                int bits = (int)reader.Read(5);
                for (int i = 0; i < count; i++)
                {
                    samples[index++] = bits == 0 ? 0 : reader.ReadSigned(bits);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    ulong quotient = reader.ReadUnary();
                    ulong value = (quotient << parameter) | reader.Read(parameter);
                    samples[index++] = (long)(value >> 1) ^ -(long)(value & 1);
                }
            }
        }
    }

    private static void PredictFixed(long[] samples, int order, int size)
    {
        for (int i = order; i < size; i++)
        {
            samples[i] += order switch
            {
                0 => 0,
                1 => samples[i - 1],
                2 => (2 * samples[i - 1]) - samples[i - 2],
                3 => (3 * samples[i - 1]) - (3 * samples[i - 2]) + samples[i - 3],
                _ => (4 * samples[i - 1]) - (6 * samples[i - 2]) + (4 * samples[i - 3]) - samples[i - 4],
            };
        }
    }

    private static void Decorrelate(int channelCode, long[][] block, int size)
    {
        switch (channelCode)
        {
            case 8: // left, side
                for (int i = 0; i < size; i++)
                {
                    block[1][i] = block[0][i] - block[1][i];
                }

                break;
            case 9: // side, right
                for (int i = 0; i < size; i++)
                {
                    block[0][i] += block[1][i];
                }

                break;
            case 10: // mid, side
                for (int i = 0; i < size; i++)
                {
                    long side = block[1][i];
                    long mid = (block[0][i] << 1) | (side & 1);
                    block[0][i] = (mid + side) >> 1;
                    block[1][i] = (mid - side) >> 1;
                }

                break;
        }
    }

    // The signature is the MD5 of the samples as little-endian signed integers of whole bytes, interleaved.
    private static void CheckSignature(StreamInfo info, List<long>[] channels, int count)
    {
        bool hasSignature = false;
        foreach (byte b in info.Md5)
        {
            hasSignature |= b != 0;
        }

        if (!hasSignature)
        {
            return;
        }

        int bytes = (info.BitsPerSample + 7) / 8;
        using var md5 = MD5.Create();
        var buffer = new byte[4096 * bytes * info.Channels];
        int used = 0;
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < info.Channels; c++)
            {
                long value = channels[c][i];
                for (int b = 0; b < bytes; b++)
                {
                    buffer[used++] = (byte)(value >> (8 * b));
                }
            }

            if (used == buffer.Length)
            {
                md5.TransformBlock(buffer, 0, used, null, 0);
                used = 0;
            }
        }

        md5.TransformFinalBlock(buffer, 0, used);
        byte[] actual = md5.Hash!;
        for (int i = 0; i < 16; i++)
        {
            if (actual[i] != info.Md5[i])
            {
                throw new AudioFormatException("the decoded samples do not match the MD5 signature of the stream");
            }
        }
    }

    private sealed class StreamInfo
    {
        public StreamInfo(int sampleRate, int channels, int bitsPerSample, long totalSamples, int maxBlockSize, byte[] md5)
        {
            SampleRate = sampleRate;
            Channels = channels;
            BitsPerSample = bitsPerSample;
            TotalSamples = totalSamples;
            MaxBlockSize = maxBlockSize;
            Md5 = md5;
        }

        public int SampleRate { get; }

        public int Channels { get; }

        public int BitsPerSample { get; }

        public long TotalSamples { get; }

        public int MaxBlockSize { get; }

        public byte[] Md5 { get; }
    }

    /// <summary>Reads bits from the most significant one, as FLAC writes them.</summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private long _bit;

        public BitReader(byte[] data, int startByte)
        {
            _data = data;
            _bit = startByte * 8L;
        }

        public long BytesLeft => _data.Length - ((_bit + 7) / 8);

        public long BytePosition => _bit / 8;

        public ulong Read(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            if (_bit + count > _data.Length * 8L)
            {
                throw new AudioFormatException("the file ends in the middle of a frame");
            }

            ulong value = 0;
            while (count > 0)
            {
                int offset = (int)(_bit & 7);
                int take = Math.Min(8 - offset, count);
                int current = _data[_bit >> 3];
                int bits = (current >> (8 - offset - take)) & ((1 << take) - 1);
                value = (value << take) | (uint)bits;
                _bit += take;
                count -= take;
            }

            return value;
        }

        public long ReadSigned(int count)
        {
            ulong value = Read(count);
            int shift = 64 - count;
            return (long)(value << shift) >> shift;
        }

        public ulong ReadUnary()
        {
            ulong zeros = 0;
            while (true)
            {
                if (_bit >= _data.Length * 8L)
                {
                    throw new AudioFormatException("the file ends in the middle of a frame");
                }

                int offset = (int)(_bit & 7);
                int current = (_data[_bit >> 3] << offset) & 0xFF;
                if (current == 0)
                {
                    zeros += (ulong)(8 - offset);
                    _bit += 8 - offset;
                    continue;
                }

                int leading = 0;
                while ((current & 0x80) == 0)
                {
                    leading++;
                    current <<= 1;
                }

                zeros += (ulong)leading;
                _bit += leading + 1;
                return zeros;
            }
        }

        public void AlignToByte() => _bit = (_bit + 7) & ~7L;

        public void SkipBytes(int count) => _bit += count * 8L;
    }
}
