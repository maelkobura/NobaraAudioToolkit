// Copyright (c) 2026 Maël Kobura
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using NVorbis;
using Xunit.Abstractions;

namespace Nobara.AudioToolkit.Tests;

/// <summary>
/// The Vorbis encoder: every common rate, mono and stereo, every quality,
/// decoded by NVorbis, an independent decoder.
/// </summary>
public sealed class EncoderTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Configurations()
    {
        foreach (int rate in new[] { 8000, 11025, 16000, 22050, 32000, 44100, 48000 })
        {
            foreach (int channels in new[] { 1, 2 })
            {
                for (int q = -1; q <= 10; q++)
                {
                    yield return [rate, channels, q / 10f];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void DecodedStreamMatchesTheSource(int rate, int channels, float quality)
    {
        PcmAudio source = Signal.Chirp(channels, rate, rate);

        PcmAudio decoded = Decode(VorbisEncoder.Encode(source, quality));

        Assert.Equal(rate, decoded.SampleRate);
        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(source.Frames, decoded.Frames);
        for (int c = 0; c < channels; c++)
        {
            Assert.Equal(0, Signal.BestLag(source, decoded, c));
            // The ratio compares waveforms, which a perceptual codec does not keep: below quality 0.1,
            // libvorbis codes stereo with lossy coupling of the channels, which the ear hardly hears.
            double snr = Signal.SignalToNoise(source, decoded, c);
            double minimum = channels == 2 && quality < 0.1f ? 3 : 8;
            Assert.True(snr > minimum, $"channel {c}: signal to noise ratio {snr:F1} dB");
        }
    }

    [Fact]
    public void KeepsTheStartOfTheStream()
    {
        var samples = new float[22050];
        for (int i = 100; i < 300; i++)
        {
            samples[i] = 0.8f * MathF.Sin(i * 0.3f);
        }

        PcmAudio decoded = Decode(VorbisEncoder.Encode(new PcmAudio(1, 44100, samples), 0.5f));

        double energy = 0;
        for (int i = 0; i < 400; i++)
        {
            energy += decoded.Samples[i] * decoded.Samples[i];
        }

        Assert.True(energy > 10, $"energy of the first 400 samples: {energy:F2}");
    }

    [Fact]
    public void GivesTheSameBytesForTheSameInput()
    {
        PcmAudio source = Signal.Chirp(2, 44100, 22050);

        Assert.Equal(VorbisEncoder.Encode(source, 0.4f, serial: 9), VorbisEncoder.Encode(source, 0.4f, serial: 9));
    }

    [Fact]
    public void EncodesAnEmptySound()
    {
        PcmAudio decoded = Decode(VorbisEncoder.Encode(new PcmAudio(1, 44100, []), 0.4f));

        Assert.Equal(0, decoded.Frames);
    }

    [Fact]
    public void RejectsAQualityOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VorbisEncoder.Encode(new PcmAudio(1, 44100, new float[10]), 1.5f));
    }

    [Fact]
    public void ReportsTheLibraryVersion()
    {
        Assert.Contains("1.3.7", VorbisEncoder.LibraryVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void MeasuresTheTimeOfOneMinuteOfStereo()
    {
        PcmAudio source = Signal.Chirp(2, 48000, 48000 * 60);
        VorbisEncoder.Encode(Signal.Chirp(2, 48000, 4800), 0.4f);

        var watch = Stopwatch.StartNew();
        byte[] ogg = VorbisEncoder.Encode(source, 0.4f);
        watch.Stop();

        output.WriteLine($"60 s of stereo at 48 kHz, quality 0.4: {watch.Elapsed.TotalSeconds:F2} s, {ogg.Length / 1024} KiB");
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(1));
    }

    private static PcmAudio Decode(byte[] ogg)
    {
        Assert.Equal(AudioFormat.Ogg, AudioFile.Detect(ogg));
        using var reader = new VorbisReader(new MemoryStream(ogg), true);
        var samples = new List<float>();
        var buffer = new float[4096 * reader.Channels];
        int read;
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }

        // The length is the granule position of the last page. NVorbis 0.10.5 may decode a few
        // frames past it at the end of some streams, which other decoders (ffmpeg) do not.
        long frames = reader.TotalSamples;
        Assert.True(samples.Count >= frames * reader.Channels, $"NVorbis decoded {samples.Count / reader.Channels} frames of {frames}");
        return new PcmAudio(reader.Channels, reader.SampleRate, samples.GetRange(0, (int)(frames * reader.Channels)).ToArray());
    }
}

/// <summary>Test signals and their comparison.</summary>
internal static class Signal
{
    /// <summary>
    /// A chirp from 200 Hz to 3 kHz (a quarter of the rate at most), different on each channel, with
    /// a little noise; not periodic, so its alignment is unique. It stays under the low-pass of the
    /// lowest qualities, which would remove its top otherwise.
    /// </summary>
    public static PcmAudio Chirp(int channels, int rate, int frames)
    {
        var random = new Random(1234);
        var samples = new float[channels * frames];
        var phase = new double[channels];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / frames;
            for (int c = 0; c < channels; c++)
            {
                double frequency = 200 + (t * (Math.Min(rate / 4.0, 3000) - 200)) + (c * 50);
                phase[c] += 2 * Math.PI * frequency / rate;
                samples[(i * channels) + c] = (float)((0.4 * Math.Sin(phase[c])) + (0.02 * (random.NextDouble() - 0.5)));
            }
        }

        return new PcmAudio(channels, rate, samples);
    }

    public static int BestLag(PcmAudio source, PcmAudio decoded, int channel)
    {
        int best = 0;
        double bestScore = double.MinValue;
        long margin = source.Frames / 10;
        for (int lag = -2048; lag <= 2048; lag++)
        {
            double score = 0;
            for (long i = margin; i < source.Frames - margin; i += 3)
            {
                long j = i + lag;
                if (j >= 0 && j < decoded.Frames)
                {
                    score += source.Samples[(i * source.Channels) + channel] * decoded.Samples[(j * decoded.Channels) + channel];
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        return best;
    }

    public static double SignalToNoise(PcmAudio source, PcmAudio decoded, int channel)
    {
        double signal = 0, noise = 0;
        long margin = source.Frames / 10;
        for (long i = margin; i < source.Frames - margin; i++)
        {
            float s = source.Samples[(i * source.Channels) + channel];
            double error = decoded.Samples[(i * decoded.Channels) + channel] - s;
            signal += s * s;
            noise += error * error;
        }

        return 10 * Math.Log10(signal / Math.Max(noise, 1e-12));
    }
}
