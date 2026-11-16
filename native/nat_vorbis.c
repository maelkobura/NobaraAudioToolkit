/*
 * Copyright (c) 2026 Maël Kobura
 * SPDX-License-Identifier: MIT
 *
 * A small C interface over libvorbis and libogg: it encodes a whole buffer
 * of interleaved float samples to an Ogg Vorbis stream in memory, with the
 * VBR quality modes of libvorbis. One call, plain arguments, so that the
 * managed side needs no structure marshalling.
 */

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#include <ogg/ogg.h>
#include <vorbis/codec.h>
#include <vorbis/vorbisenc.h>

#if defined(_WIN32)
#  define NAT_EXPORT __declspec(dllexport)
#else
#  define NAT_EXPORT __attribute__((visibility("default")))
#endif

#define NAT_OK 0
#define NAT_INVALID_ARGUMENT 1
#define NAT_UNSUPPORTED 2
#define NAT_OUT_OF_MEMORY 3
#define NAT_ENCODER_FAILED 4

/* The frames given to the analysis at a time. */
#define NAT_CHUNK 1024

typedef struct
{
    unsigned char *data;
    size_t length;
    size_t capacity;
    int failed;
} nat_buffer;

static void nat_append(nat_buffer *buffer, const unsigned char *bytes, long count)
{
    if (buffer->failed || count <= 0)
    {
        return;
    }

    if (buffer->length + (size_t)count > buffer->capacity)
    {
        size_t capacity = buffer->capacity == 0 ? 65536 : buffer->capacity;
        while (capacity < buffer->length + (size_t)count)
        {
            capacity *= 2;
        }

        unsigned char *grown = (unsigned char *)realloc(buffer->data, capacity);
        if (grown == NULL)
        {
            buffer->failed = 1;
            return;
        }

        buffer->data = grown;
        buffer->capacity = capacity;
    }

    memcpy(buffer->data + buffer->length, bytes, (size_t)count);
    buffer->length += (size_t)count;
}

static void nat_write_page(nat_buffer *buffer, const ogg_page *page)
{
    nat_append(buffer, page->header, page->header_len);
    nat_append(buffer, page->body, page->body_len);
}

static void nat_error(char *error, int capacity, const char *message)
{
    if (error != NULL && capacity > 0)
    {
        snprintf(error, (size_t)capacity, "%s", message);
    }
}

/* Gives the analysis what it has blocks for, and writes the pages it completes. */
static int nat_drain(vorbis_dsp_state *dsp, vorbis_block *block, ogg_stream_state *stream, nat_buffer *buffer)
{
    ogg_packet packet;
    ogg_page page;
    while (vorbis_analysis_blockout(dsp, block) == 1)
    {
        if (vorbis_analysis(block, NULL) != 0 || vorbis_bitrate_addblock(block) != 0)
        {
            return NAT_ENCODER_FAILED;
        }

        while (vorbis_bitrate_flushpacket(dsp, &packet) == 1)
        {
            ogg_stream_packetin(stream, &packet);
            while (ogg_stream_pageout(stream, &page) != 0)
            {
                nat_write_page(buffer, &page);
            }
        }
    }

    return buffer->failed ? NAT_OUT_OF_MEMORY : NAT_OK;
}

/*
 * Encodes interleaved samples, from -1 to 1, to an Ogg Vorbis stream.
 *
 * samples:     frames * channels floats, left then right for stereo.
 * quality:     the VBR quality of libvorbis, from -0.1 to 1.
 * serial:      the serial number of the Ogg stream; the same input and
 *              serial always give the same bytes.
 * output:      receives a buffer that nat_free releases.
 * error:       receives a message when the call fails; may be NULL.
 *
 * Returns NAT_OK, or a NAT_ error code.
 */
NAT_EXPORT int nat_vorbis_encode(
    const float *samples,
    long long frames,
    int channels,
    int sample_rate,
    float quality,
    unsigned int serial,
    unsigned char **output,
    long long *output_length,
    char *error,
    int error_capacity)
{
    vorbis_info info;
    vorbis_comment comment;
    vorbis_dsp_state dsp;
    vorbis_block block;
    ogg_stream_state stream;
    ogg_packet header, header_comment, header_code;
    ogg_page page;
    nat_buffer buffer = { NULL, 0, 0, 0 };
    int result = NAT_OK;
    long long done = 0;

    if (output == NULL || output_length == NULL || (samples == NULL && frames > 0) || frames < 0
        || channels < 1 || channels > 255 || sample_rate < 1)
    {
        nat_error(error, error_capacity, "invalid argument");
        return NAT_INVALID_ARGUMENT;
    }

    *output = NULL;
    *output_length = 0;

    vorbis_info_init(&info);
    if (vorbis_encode_init_vbr(&info, channels, sample_rate, quality) != 0)
    {
        vorbis_info_clear(&info);
        nat_error(error, error_capacity, "libvorbis has no setup for this number of channels, sample rate, and quality");
        return NAT_UNSUPPORTED;
    }

    vorbis_comment_init(&comment);
    vorbis_analysis_init(&dsp, &info);
    vorbis_block_init(&dsp, &block);
    ogg_stream_init(&stream, (int)serial);

    vorbis_analysis_headerout(&dsp, &comment, &header, &header_comment, &header_code);
    ogg_stream_packetin(&stream, &header);
    ogg_stream_packetin(&stream, &header_comment);
    ogg_stream_packetin(&stream, &header_code);

    /* The audio starts on a page of its own, as the specification asks. */
    while (ogg_stream_flush(&stream, &page) != 0)
    {
        nat_write_page(&buffer, &page);
    }

    while (result == NAT_OK && done < frames)
    {
        int count = (int)(frames - done < NAT_CHUNK ? frames - done : NAT_CHUNK);
        float **target = vorbis_analysis_buffer(&dsp, count);
        const float *source = samples + (done * channels);
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                target[c][i] = source[(i * channels) + c];
            }
        }

        vorbis_analysis_wrote(&dsp, count);
        done += count;
        result = nat_drain(&dsp, &block, &stream, &buffer);
    }

    if (result == NAT_OK)
    {
        vorbis_analysis_wrote(&dsp, 0);
        result = nat_drain(&dsp, &block, &stream, &buffer);
    }

    if (result == NAT_OK)
    {
        while (ogg_stream_flush(&stream, &page) != 0)
        {
            nat_write_page(&buffer, &page);
        }

        if (buffer.failed)
        {
            result = NAT_OUT_OF_MEMORY;
        }
    }

    ogg_stream_clear(&stream);
    vorbis_block_clear(&block);
    vorbis_dsp_clear(&dsp);
    vorbis_comment_clear(&comment);
    vorbis_info_clear(&info);

    if (result != NAT_OK)
    {
        free(buffer.data);
        nat_error(error, error_capacity, result == NAT_OUT_OF_MEMORY ? "out of memory" : "libvorbis failed to analyze a block");
        return result;
    }

    *output = buffer.data;
    *output_length = (long long)buffer.length;
    return NAT_OK;
}

/* Releases a buffer returned by nat_vorbis_encode. */
NAT_EXPORT void nat_free(unsigned char *data)
{
    free(data);
}

/* The version of libvorbis, such as "Xiph.Org libVorbis 1.3.7". */
NAT_EXPORT const char *nat_vorbis_version(void)
{
    return vorbis_version_string();
}
