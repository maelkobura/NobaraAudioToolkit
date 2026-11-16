// Copyright (c) 2026 Maël Kobura
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Nobara.AudioToolkit;

/// <summary>
/// Encodes audio to Ogg Vorbis with libvorbis, the reference encoder, in its
/// variable bit rate mode. The native library <c>nat_vorbis</c> ships in the
/// package for win-x64.
/// </summary>
/// <remarks>
/// Encoding is deterministic: the same audio, quality, and serial number give
/// the same bytes. The encoder is safe to call from several threads.
/// </remarks>
public static class VorbisEncoder
{
    /// <summary>The lowest quality that libvorbis accepts.</summary>
    public const float MinQuality = -0.1f;

    /// <summary>The highest quality that libvorbis accepts.</summary>
    public const float MaxQuality = 1f;

    private const string Library = "nat_vorbis";

    /// <summary>Gets the version of libvorbis, such as <c>Xiph.Org libVorbis 1.3.7</c>.</summary>
    public static string LibraryVersion => Marshal.PtrToStringAnsi(NativeMethods.nat_vorbis_version()) ?? string.Empty;

    /// <summary>Encodes audio to an Ogg Vorbis stream.</summary>
    /// <param name="audio">The audio: 1 to 255 channels, at a rate that libvorbis has a setup for.</param>
    /// <param name="quality">The VBR quality, from -0.1 (about 45 kbit/s for stereo at 44.1 kHz) to 1 (about 500 kbit/s); 0.4 gives about 128 kbit/s.</param>
    /// <param name="serial">The serial number of the Ogg stream.</param>
    /// <returns>The bytes of the Ogg file.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The quality is out of range.</exception>
    /// <exception cref="NotSupportedException">libvorbis has no setup for this channel count, rate, and quality.</exception>
    public static byte[] Encode(PcmAudio audio, float quality, uint serial = 1)
    {
        if (audio is null)
        {
            throw new ArgumentNullException(nameof(audio));
        }

        if (!(quality >= MinQuality && quality <= MaxQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(quality), quality, $"The quality is from {MinQuality} to {MaxQuality}.");
        }

        var error = new byte[256];
        int result;
        IntPtr output;
        long length;
        unsafe
        {
            fixed (float* samples = audio.Samples)
            fixed (byte* message = error)
            {
                result = NativeMethods.nat_vorbis_encode(samples, audio.Frames, audio.Channels, audio.SampleRate, quality, serial, out output, out length, message, error.Length);
            }
        }

        if (result != 0)
        {
            string text = Encoding.ASCII.GetString(error, 0, Math.Max(0, Array.IndexOf(error, (byte)0)));
            if (result == 2)
            {
                throw new NotSupportedException($"libvorbis cannot encode {audio.Channels} channels at {audio.SampleRate} Hz with quality {quality}: {text}");
            }

            throw new InvalidOperationException($"libvorbis failed ({result}): {text}");
        }

        try
        {
            var bytes = new byte[length];
            Marshal.Copy(output, bytes, 0, (int)length);
            return bytes;
        }
        finally
        {
            NativeMethods.nat_free(output);
        }
    }

    private static unsafe class NativeMethods
    {
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nat_vorbis_encode(float* samples, long frames, int channels, int sampleRate, float quality, uint serial, out IntPtr output, out long outputLength, byte* error, int errorCapacity);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern void nat_free(IntPtr data);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr nat_vorbis_version();
    }
}
