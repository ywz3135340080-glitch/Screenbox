#define COBJMACROS
#include <windows.h>
#include <errno.h>
#include <stdio.h>
#include <limits.h>
#include <string.h>
#include "screenbox-color.h"
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/imgutils.h>
#include <libavutil/mathematics.h>
#include <libswresample/swresample.h>
#include <libplacebo/d3d11.h>
#include <libplacebo/renderer.h>
#include <libplacebo/utils/libav.h>

static void native_log(void *priv, enum pl_log_level level, const char *message)
{
    (void)priv;
    fprintf(stderr, "Native color (%d): %s\n", level, message);
}
static const AVRational ticks_base = {1, 10000000};
struct sb_decoder {
    AVFormatContext *format;
    AVIOContext *io;
    AVCodecContext *codec;
    AVFrame *frame, *output;
    AVPacket *packet;
    SwrContext *resampler;
    AVChannelLayout output_layout;
    int stream_index, kind, draining;
    int64_t origin_ticks, discard_before, next_audio_ticks;
    uint8_t *buffer;
    unsigned buffer_capacity;
    sb_read_fn read;
    sb_seek_fn seek;
    void *user;
    pl_log log;
    pl_d3d11 device;
    pl_renderer renderer;
    pl_tex input_tex[4], output_tex[4];
    struct pl_frame target;
};

int sb_abi_version(void) { return 1; }

static int input_read(void *opaque, uint8_t *data, int length)
{
    sb_decoder *d = opaque;
    int result = d->read(d->user, data, length);
    return result > 0 ? result : result == 0 ? AVERROR_EOF : AVERROR(EIO);
}
static int64_t input_seek(void *opaque, int64_t offset, int origin)
{
    sb_decoder *d = opaque;
    return d->seek(d->user, offset, origin);
}

void sb_close(sb_decoder *d)
{
    if (!d) return;
    if (d->device) {
        for (int i = 0; i < 4; ++i) {
            pl_tex_destroy(d->device->gpu, &d->input_tex[i]);
            pl_tex_destroy(d->device->gpu, &d->output_tex[i]);
        }
    }
    pl_renderer_destroy(&d->renderer);
    pl_d3d11_destroy(&d->device);
    pl_log_destroy(&d->log);
    av_frame_free(&d->frame);
    av_frame_free(&d->output);
    av_packet_free(&d->packet);
    swr_free(&d->resampler);
    av_channel_layout_uninit(&d->output_layout);
    avcodec_free_context(&d->codec);
    avformat_close_input(&d->format);
    if (d->io) av_freep(&d->io->buffer);
    avio_context_free(&d->io);
    av_free(d->buffer);
    av_free(d);
}

static int init_video(sb_decoder *d, int width, int height)
{
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384 ||
        (width & 1) || (height & 1)) return AVERROR_INVALIDDATA;
    d->log = pl_log_create(PL_API_VER, pl_log_params(.log_cb = native_log, .log_level = PL_LOG_WARN));
    d->device = pl_d3d11_create(d->log, pl_d3d11_params(.allow_software = true));
    if (!d->device) return AVERROR_EXTERNAL;
    d->renderer = pl_renderer_create(d->log, d->device->gpu);
    d->output = av_frame_alloc();
    if (!d->renderer || !d->output) return AVERROR(ENOMEM);
    d->output->format = AV_PIX_FMT_P010LE;
    d->output->width = width;
    d->output->height = height;
    d->output->color_range = AVCOL_RANGE_MPEG;
    d->output->colorspace = AVCOL_SPC_BT2020_NCL;
    d->output->color_primaries = AVCOL_PRI_BT2020;
    d->output->color_trc = AVCOL_TRC_SMPTE2084;
    d->output->chroma_location = AVCHROMA_LOC_LEFT;
    int result = av_frame_get_buffer(d->output, 32);
    if (result < 0) return result;
    if (!pl_frame_recreate_from_avframe(d->device->gpu, &d->target,
                                      d->output_tex, d->output)) return AVERROR_EXTERNAL;
    d->target.color.hdr.max_luma = 1000;
    d->target.color.hdr.min_luma = 0.001f;
    return 0;
}

int sb_open(sb_read_fn read, sb_seek_fn seek, void *user, int kind,
            sb_decoder **decoder, sb_info *info)
{
    if (!decoder || !info || !read || !seek || (kind != 0 && kind != 1))
        return AVERROR(EINVAL);
    *decoder = NULL;
    memset(info, 0, sizeof(*info));
    sb_decoder *d = av_mallocz(sizeof(*d));
    if (!d) return AVERROR(ENOMEM);
    d->kind = kind; d->read = read; d->seek = seek; d->user = user;
    d->next_audio_ticks = AV_NOPTS_VALUE;
    uint8_t *input = av_malloc(32768);
    if (!input) { sb_close(d); return AVERROR(ENOMEM); }
    d->io = avio_alloc_context(input, 32768, 0, d, input_read, NULL, input_seek);
    if (!d->io) { av_free(input); sb_close(d); return AVERROR(ENOMEM); }
    d->format = avformat_alloc_context();
    if (!d->format) { sb_close(d); return AVERROR(ENOMEM); }
    d->format->pb = d->io;
    d->format->flags |= AVFMT_FLAG_CUSTOM_IO;
    int result = avformat_open_input(&d->format, NULL, NULL, NULL);
    if (result < 0) goto error;
    result = avformat_find_stream_info(d->format, NULL);
    if (result < 0) goto error;
    const AVCodec *codec = NULL;
    d->stream_index = av_find_best_stream(d->format,
        kind == 0 ? AVMEDIA_TYPE_VIDEO : AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
    if (d->stream_index == AVERROR_STREAM_NOT_FOUND) { sb_close(d); return 1; }
    if (d->stream_index < 0) { result = d->stream_index; goto error; }
    d->codec = avcodec_alloc_context3(codec);
    if (!d->codec) { result = AVERROR(ENOMEM); goto error; }
    AVStream *stream = d->format->streams[d->stream_index];
    result = avcodec_parameters_to_context(d->codec, stream->codecpar);
    if (result < 0) goto error;
    d->codec->thread_count = kind == 0 ? 0 : 1;
    result = avcodec_open2(d->codec, codec, NULL);
    if (result < 0) goto error;
    d->packet = av_packet_alloc();
    d->frame = av_frame_alloc();
    if (!d->packet || !d->frame) { result = AVERROR(ENOMEM); goto error; }
    d->origin_ticks = d->format->start_time == AV_NOPTS_VALUE ? 0 :
        av_rescale_q(d->format->start_time, AV_TIME_BASE_Q, ticks_base);
    info->duration_ticks = d->format->duration == AV_NOPTS_VALUE ? 0 :
        av_rescale_q(d->format->duration, AV_TIME_BASE_Q, ticks_base);
    if (kind == 0) {
        info->width = d->codec->width;
        info->height = d->codec->height;
        AVRational rate = av_guess_frame_rate(d->format, stream, NULL);
        info->fps_num = rate.num > 0 ? rate.num : 25;
        info->fps_den = rate.den > 0 ? rate.den : 1;
        result = init_video(d, info->width, info->height);
        if (result < 0) goto error;
    } else {
        info->sample_rate = d->codec->sample_rate;
        info->channels = d->codec->ch_layout.nb_channels;
        if (info->sample_rate <= 0 || info->channels < 1 || info->channels > 8) {
            result = AVERROR_INVALIDDATA; goto error;
        }
        result = av_channel_layout_copy(&d->output_layout, &d->codec->ch_layout);
        if (result < 0) goto error;
        info->channel_mask = d->output_layout.order == AV_CHANNEL_ORDER_NATIVE ?
            (uint32_t)d->output_layout.u.mask : 0;
        result = swr_alloc_set_opts2(&d->resampler, &d->output_layout, AV_SAMPLE_FMT_S16,
            info->sample_rate, &d->codec->ch_layout, d->codec->sample_fmt,
            d->codec->sample_rate, 0, NULL);
        if (result < 0 || (result = swr_init(d->resampler)) < 0) goto error;
    }
    *decoder = d;
    return 0;
error:
    sb_close(d);
    return result;
}

/* Consume every packet on this independent input but decode only its selected stream. */
static int decode_frame(sb_decoder *d)
{
    av_frame_unref(d->frame);
    for (;;) {
        int result = avcodec_receive_frame(d->codec, d->frame);
        if (result >= 0) return 1;
        if (result == AVERROR_EOF) return 0;
        if (result != AVERROR(EAGAIN)) return result;
        do {
            av_packet_unref(d->packet);
            result = av_read_frame(d->format, d->packet);
        } while (result >= 0 && d->packet->stream_index != d->stream_index);
        if (result < 0) {
            if (d->draining) return result == AVERROR_EOF ? 0 : result;
            d->draining = 1;
            result = avcodec_send_packet(d->codec, NULL);
        } else {
            result = avcodec_send_packet(d->codec, d->packet);
        }
        if (result < 0 && result != AVERROR_EOF) return result;
    }
}

static int convert_video(sb_decoder *d, int require_dovi)
{
    AVFrame *frame = d->frame;
    if (frame->width != d->output->width || frame->height != d->output->height)
        return AVERROR_INPUT_CHANGED;
    if (require_dovi && !av_frame_get_side_data(frame, AV_FRAME_DATA_DOVI_METADATA))
        return AVERROR_INVALIDDATA;
    struct pl_frame source = {0};
    if (!pl_map_avframe_ex(d->device->gpu, &source,
        pl_avframe_params(.frame = frame, .tex = d->input_tex, .map_dovi = true)))
        return AVERROR_EXTERNAL;
    int result = AVERROR_EXTERNAL;
    if (require_dovi && (source.repr.sys != PL_COLOR_SYSTEM_DOLBYVISION || !source.repr.dovi))
        { fprintf(stderr, "Dolby metadata mapping failed\n"); goto done; }
    if (!pl_render_image(d->renderer, &source, &d->target, &pl_render_default_params))
        { fprintf(stderr, "P010 render failed\n"); goto done; }
    // D3D11 has no asynchronous transfer callbacks. Download each P010 plane
    // synchronously instead of the libav helper's async multi-plane path.
    for (int p = 0; p < d->target.num_planes; ++p) {
        if (!pl_tex_download(d->device->gpu, pl_tex_transfer_params(
            .tex = d->target.planes[p].texture,
            .row_pitch = d->output->linesize[p],
            .ptr = d->output->data[p]))) {
            fprintf(stderr, "P010 plane download failed\n");
            goto done;
        }
    }
    int size = av_image_get_buffer_size(AV_PIX_FMT_P010LE, frame->width, frame->height, 1);
    if (size < 0) { result = size; goto done; }
    av_fast_malloc(&d->buffer, &d->buffer_capacity, size);
    if (!d->buffer) { result = AVERROR(ENOMEM); goto done; }
    result = av_image_copy_to_buffer(d->buffer, d->buffer_capacity,
        (const uint8_t * const *)d->output->data, d->output->linesize,
        AV_PIX_FMT_P010LE, frame->width, frame->height, 1);
done:
    pl_unmap_avframe(d->device->gpu, &source);
    return result;
}

int sb_next(sb_decoder *d, sb_sample *sample)
{
    if (!d || !sample) return AVERROR(EINVAL);
    memset(sample, 0, sizeof(*sample));
    for (;;) {
        int result = decode_frame(d);
        if (result <= 0) return result;
        AVFrame *f = d->frame;
        AVStream *stream = d->format->streams[d->stream_index];
        int64_t pts = f->best_effort_timestamp == AV_NOPTS_VALUE ?
            (d->next_audio_ticks == AV_NOPTS_VALUE ? d->discard_before : d->next_audio_ticks) :
            av_rescale_q(f->best_effort_timestamp, stream->time_base, ticks_base) - d->origin_ticks;
        if (d->kind == 0) {
            if (pts < d->discard_before) continue;
            result = convert_video(d, 1);
            if (result < 0) return result;
            sample->length = result;
            AVRational rate = av_guess_frame_rate(d->format, stream, f);
            sample->duration_ticks = f->duration > 0 ?
                av_rescale_q(f->duration, stream->time_base, ticks_base) :
                (rate.num > 0 ? av_rescale_q(1, av_inv_q(rate), ticks_base) : 400000);
        } else {
            int channels = d->output_layout.nb_channels;
            int rate = d->codec->sample_rate;
            int capacity = swr_get_out_samples(d->resampler, f->nb_samples);
            if (capacity < 0 || capacity > INT_MAX / (channels * 2)) return AVERROR_INVALIDDATA;
            av_fast_malloc(&d->buffer, &d->buffer_capacity, capacity * channels * 2);
            if (!d->buffer) return AVERROR(ENOMEM);
            uint8_t *out[] = {d->buffer};
            int samples = swr_convert(d->resampler, out, capacity,
                (const uint8_t **)f->extended_data, f->nb_samples);
            if (samples < 0) return samples;
            int64_t duration = av_rescale_q(samples, (AVRational){1, rate}, ticks_base);
            d->next_audio_ticks = pts + duration;
            if (pts + duration <= d->discard_before) continue;
            int trim = pts < d->discard_before ?
                (int)av_rescale_rnd(d->discard_before - pts, rate, 10000000, AV_ROUND_UP) : 0;
            if (trim >= samples) continue;
            if (trim > 0) memmove(d->buffer, d->buffer + trim * channels * 2,
                                   (samples - trim) * channels * 2);
            pts += av_rescale_q(trim, (AVRational){1, rate}, ticks_base);
            sample->length = (samples - trim) * channels * 2;
            sample->duration_ticks = av_rescale_q(samples - trim, (AVRational){1, rate}, ticks_base);
        }
        sample->timestamp_ticks = pts;
        sample->data = d->buffer;
        return 1;
    }
}

int sb_seek(sb_decoder *d, int64_t ticks)
{
    if (!d || ticks < 0) return AVERROR(EINVAL);
    AVStream *stream = d->format->streams[d->stream_index];
    int64_t target = av_rescale_q(ticks + d->origin_ticks, ticks_base, stream->time_base);
    int result = av_seek_frame(d->format, d->stream_index, target, AVSEEK_FLAG_BACKWARD);
    if (result < 0) return result;
    avcodec_flush_buffers(d->codec);
    if (d->resampler) {
        swr_close(d->resampler);
        result = swr_init(d->resampler);
        if (result < 0) return result;
    }
    d->draining = 0;
    d->discard_before = ticks;
    d->next_audio_ticks = AV_NOPTS_VALUE;
    return 0;
}

int sb_self_test(void)
{
    sb_decoder *d = av_mallocz(sizeof(*d));
    if (!d) return AVERROR(ENOMEM);
    int result = init_video(d, 64, 32);
    if (result < 0) goto done;
    d->frame = av_frame_alloc();
    if (!d->frame) { result = AVERROR(ENOMEM); goto done; }
    d->frame->format = AV_PIX_FMT_YUV420P10LE;
    d->frame->width = 64; d->frame->height = 32;
    d->frame->color_range = AVCOL_RANGE_MPEG;
    d->frame->colorspace = AVCOL_SPC_BT709;
    d->frame->color_primaries = AVCOL_PRI_BT709;
    d->frame->color_trc = AVCOL_TRC_BT709;
    result = av_frame_get_buffer(d->frame, 32);
    if (result < 0) goto done;
    for (int p = 0; p < 3; ++p)
        for (int y = 0; y < (p ? 16 : 32); ++y) {
            uint16_t *row = (uint16_t *)(d->frame->data[p] + y * d->frame->linesize[p]);
            for (int x = 0; x < (p ? 32 : 64); ++x) row[x] = 512;
        }
    result = convert_video(d, 0);
    fprintf(stderr, "P010 self-test bytes: %d (expected 6144)\n", result);
    if (result != 6144) { result = AVERROR_INVALIDDATA; goto done; }
    uint16_t *pixels = (uint16_t *)d->buffer;
    fprintf(stderr, "P010 self-test Y/U/V: %u/%u/%u\n", pixels[0], pixels[2048], pixels[2049]);
    if (pixels[0] < (64 << 6) || pixels[0] >= (940 << 6) ||
        abs((int)pixels[2048] - 32768) > 128 ||
        abs((int)pixels[2049] - 32768) > 128) result = AVERROR_INVALIDDATA;
    else result = 0;
done:
    sb_close(d);
    return result;
}
