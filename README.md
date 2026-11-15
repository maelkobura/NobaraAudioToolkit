# Nobara Audio Toolkit

A small .NET library for build tools that turn sound sources into Ogg Vorbis:

- it reads **WAV** (integer PCM and float) and **FLAC** files, in C#;
- it mixes stereo down to mono, for sounds placed in a 3D world;
- it encodes to **Ogg Vorbis** with **libvorbis**, the reference encoder,
  through a small native library built from the official Xiph.Org sources.

It was written for the resource compiler of the Nobara engine, which
converts the WAV and FLAC sources of a game to Ogg Vorbis when the game is
built, so that games ship one compressed format and decode it with a
managed decoder. It is not meant to run inside a game: the native library
ships for win-x64, for build machines.

## Status

Work in progress: the first version is under review.

## License

MIT for the code of this repository. The native library contains libogg and
libvorbis, under the BSD 3-clause license of the Xiph.Org Foundation.
