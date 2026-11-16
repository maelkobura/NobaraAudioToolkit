// Copyright (c) 2026 Maël Kobura
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace Nobara.AudioToolkit.Tests;

/// <summary>The readers of the source formats: WAV and FLAC.</summary>
public sealed class SourceTests
{
    [Theory]
    [InlineData("mono16", 44100, 1)]
    [InlineData("stereo16", 44100, 2)]
    [InlineData("stereo16-fixed", 44100, 2)]
    [InlineData("stereo24", 48000, 2)]
    [InlineData("mono16-oddblock", 22050, 1)]
    public void DecodesFlacExactlyLikeTheReferenceDecoder(string name, int rate, int channels)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".flac"));
        byte[] reference = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".s32"));

        PcmAudio audio = FlacDecoder.Decode(file);

        Assert.Equal(AudioFormat.Flac, AudioFile.Detect(file));
        Assert.Equal(rate, audio.SampleRate);
        Assert.Equal(channels, audio.Channels);
        Assert.Equal(reference.Length / 4, audio.Samples.Length);
        for (int i = 0; i < audio.Samples.Length; i++)
        {
            float expected = BinaryPrimitives.ReadInt32LittleEndian(reference.AsSpan(i * 4)) / 2147483648f;
            Assert.True(expected == audio.Samples[i], $"sample {i}: {audio.Samples[i]} instead of {expected}");
        }
    }

    [Fact]
    public void RejectsFlacWhoseSamplesDoNotMatchTheSignature()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mono16.flac"));

        // The first byte of the MD5 signature, at the end of the STREAMINFO block.
        file[8 + 18] ^= 0xFF;

        Assert.Throws<AudioFormatException>(() => FlacDecoder.Decode(file));
    }

    [Fact]
    public void RejectsATruncatedFlacFile()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stereo16.flac"));

        Assert.Throws<AudioFormatException>(() => FlacDecoder.Decode(file.AsSpan(0, file.Length / 2).ToArray()));
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(1, 16)]
    [InlineData(2, 16)]
    [InlineData(2, 24)]
    [InlineData(2, 32)]
    public void ReadsIntegerWav(int channels, int bits)
    {
        float[] samples = Ramp(channels, 1000);
        byte[] file = Wav.Write(samples, channels, 32000, bits, isFloat: false, extensible: false);

        PcmAudio audio = WavReader.Read(file);

        Assert.Equal(channels, audio.Channels);
        Assert.Equal(32000, audio.SampleRate);
        Assert.Equal(1000, audio.Frames);
        // One step of the format, or the precision of a float near 1 for 32 bits.
        float tolerance = Math.Max(1.5f / (1L << (bits - 1)), 1e-6f);
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.InRange(audio.Samples[i], samples[i] - tolerance, samples[i] + tolerance);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsFloatWav(bool extensible)
    {
        float[] samples = Ramp(2, 500);
        byte[] file = Wav.Write(samples, 2, 48000, 32, isFloat: true, extensible);

        Assert.Equal(samples, WavReader.Read(file).Samples);
    }

    [Fact]
    public void SkipsUnknownChunksAndTheirPadding()
    {
        float[] samples = Ramp(1, 100);
        byte[] file = Wav.Write(samples, 1, 22050, 16, isFloat: false, extensible: false, extraChunk: true);

        Assert.Equal(100, WavReader.Read(file).Frames);
    }

    [Fact]
    public void RejectsAWavEncodingItDoesNotRead()
    {
        byte[] file = Wav.Write(Ramp(1, 10), 1, 8000, 16, isFloat: false, extensible: false);

        // Format code 2: Microsoft ADPCM.
        file[20] = 2;

        AudioFormatException error = Assert.Throws<AudioFormatException>(() => WavReader.Read(file));
        Assert.Contains("format code 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MixesStereoToMono()
    {
        var audio = new PcmAudio(2, 8000, [0.5f, -0.5f, 1f, 0f]);

        PcmAudio mono = audio.ToMono();

        Assert.Equal(1, mono.Channels);
        Assert.Equal([0f, 0.5f], mono.Samples);
    }

    [Fact]
    public void RecognizesFormatsByTheirFirstBytes()
    {
        Assert.Equal(AudioFormat.Ogg, AudioFile.Detect("OggS...."u8));
        Assert.Equal(AudioFormat.Unknown, AudioFile.Detect("ID3\u0004...."u8));
        Assert.Throws<AudioFormatException>(() => AudioFile.Decode("ID3\u0004........"u8.ToArray()));
    }

    private static float[] Ramp(int channels, int frames)
    {
        var samples = new float[channels * frames];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(i * 0.01) * 0.9f;
        }

        return samples;
    }
}

/// <summary>Writes WAV files for the tests.</summary>
internal static class Wav
{
    public static byte[] Write(float[] samples, int channels, int rate, int bits, bool isFloat, bool extensible, bool extraChunk = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int bytes = bits / 8;
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        if (extraChunk)
        {
            writer.Write("LIST"u8);
            writer.Write(3);
            writer.Write((byte)1);
            writer.Write((byte)2);
            writer.Write((byte)3);
            writer.Write((byte)0); // padding
        }

        writer.Write("fmt "u8);
        writer.Write(extensible ? 40 : 16);
        writer.Write((ushort)(extensible ? 0xFFFE : isFloat ? 3 : 1));
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bytes);
        writer.Write((ushort)(channels * bytes));
        writer.Write((ushort)bits);
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)bits);
            writer.Write(channels == 2 ? 3 : 4);
            writer.Write((ushort)(isFloat ? 3 : 1));
            writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }

        writer.Write("data"u8);
        writer.Write(samples.Length * bytes);
        foreach (float sample in samples)
        {
            if (isFloat)
            {
                writer.Write(sample);
                continue;
            }

            switch (bits)
            {
                case 8: writer.Write((byte)Math.Clamp((int)Math.Round((sample * 128) + 128), 0, 255)); break;
                case 16: writer.Write((short)Math.Clamp(Math.Round(sample * 32768), short.MinValue, short.MaxValue)); break;
                case 24:
                    int value = (int)Math.Clamp(Math.Round(sample * 8388608), -8388608, 8388607);
                    writer.Write((byte)value);
                    writer.Write((byte)(value >> 8));
                    writer.Write((byte)(value >> 16));
                    break;
                default: writer.Write((int)Math.Clamp(Math.Round(sample * 2147483648.0), int.MinValue, int.MaxValue)); break;
            }
        }

        writer.Flush();
        byte[] file = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        return file;
    }
}
