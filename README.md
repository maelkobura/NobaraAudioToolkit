# Nobara Audio Toolkit

Reads WAV and FLAC files and encodes audio to Ogg Vorbis with libvorbis, the
reference encoder. It is meant for build tools that convert sound sources,
such as the resource compiler of the Nobara engine, not for games at run
time.

```csharp
using Nobara.AudioToolkit;

byte[] source = File.ReadAllBytes("click.wav");
PcmAudio audio = AudioFile.Decode(source);      // WAV or FLAC
byte[] ogg = VorbisEncoder.Encode(audio.ToMono(), quality: 0.4f);
File.WriteAllBytes("click.ogg", ogg);
```

## What it reads and writes

| Part | Formats |
| --- | --- |
| `WavReader` | RIFF WAVE, integer PCM of 8, 16, 24, or 32 bits and 32-bit float, plain or extensible header |
| `FlacDecoder` | Native FLAC (RFC 9639), every subframe type and channel assignment, 4 to 32 bits; the samples are checked against the MD5 signature of the stream |
| `PcmAudio.ToMono` | Mixes channels down to one, by their mean |
| `VorbisEncoder` | Ogg Vorbis, VBR quality from -0.1 to 1, with libvorbis 1.3.7; the same input and serial number give the same bytes |

The decoders are written in C#. The encoder calls the native library
`nat_vorbis`, which this repository builds from the sources of libogg and
libvorbis (submodules of `native/`) and the small C interface
`native/nat_vorbis.c`. The package ships it for **win-x64** only.

## Building

```powershell
git clone --recurse-submodules https://github.com/maelkobura/NobaraAudioToolkit.git
cd NobaraAudioToolkit
./scripts/build-native.ps1          # CMake and an x64 C compiler (MSVC)
dotnet test tests/Nobara.AudioToolkit.Tests -c Release
dotnet pack src/Nobara.AudioToolkit -c Release -o artifacts/packages
```

`build-native.ps1` takes `-Generator` to pick a CMake generator, such as
`"Visual Studio 15 2017"`. The tests encode every common rate (8 to
48 kHz), mono and stereo, at every quality, and decode the result with
NVorbis, an independent decoder: the length, the alignment, and the signal
must match the source. The FLAC fixtures of `tests/.../Fixtures/` and their
reference samples are made by `make-fixtures.py` with ffmpeg.

## Releases

A tag `v<version>` that matches the `<Version>` of the project builds,
tests, packs, and publishes the package to nuget.org, with the secret
`NUGET_API_KEY` of the repository.

## License

MIT for the code of this repository. The native library contains libogg and
libvorbis, under the BSD 3-clause license of the Xiph.Org Foundation: see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The package license is
`MIT AND BSD-3-Clause`.
