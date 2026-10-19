// Internal decoder/rendering proof. No player UI or external player process.
#define COBJMACROS
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <libplacebo/d3d11.h>
#include <libplacebo/renderer.h>
#include <libplacebo/utils/libav.h>
#include <libavutil/imgutils.h>
#include <libavutil/pixdesc.h>

static void log_message(void *priv, enum pl_log_level level, const char *msg)
{
    (void) priv;
    fprintf(stderr, "[placebo:%d] %s\n", level, msg);
}

static int save_raw(const char *name, AVFrame *frame)
{
    FILE *file = fopen(name, "wb");
    if (!file) return 0;
    int bytes = frame->format == AV_PIX_FMT_RGBA ? 4 : 8;
    int ok = 1;
    for (int y = 0; y < frame->height; ++y)
        if (fwrite(frame->data[0] + y * frame->linesize[0], bytes, frame->width, file)
            != (size_t) frame->width) { ok = 0; break; }
    if (fclose(file)) ok = 0;
    return ok;
}

static int render_frame(pl_gpu gpu, pl_renderer renderer, struct pl_frame *src,
                        const char *path, int hdr)
{
    int ok = 0;
    AVFrame *out = av_frame_alloc();
    pl_tex textures[4] = {0};
    struct pl_frame target = {0};
    if (!out) return 0;
    out->format = hdr ? AV_PIX_FMT_RGBA64LE : AV_PIX_FMT_RGBA;
    out->width = src->planes[0].texture->params.w;
    out->height = src->planes[0].texture->params.h;
    out->color_range = AVCOL_RANGE_JPEG;
    out->colorspace = AVCOL_SPC_RGB;
    out->color_primaries = hdr ? AVCOL_PRI_BT2020 : AVCOL_PRI_BT709;
    out->color_trc = hdr ? AVCOL_TRC_SMPTE2084 : AVCOL_TRC_IEC61966_2_1;
    if (av_frame_get_buffer(out, 32) < 0) goto done;
    if (!pl_frame_recreate_from_avframe(gpu, &target, textures, out)) goto done;
    target.color.hdr.max_luma = hdr ? 1000 : 203;
    target.color.hdr.min_luma = hdr ? 0.001f : 0.203f;
    if (!pl_render_image(renderer, src, &target, &pl_render_default_params)) goto done;
    if (!pl_download_avframe(gpu, &target, out)) goto done;
    ok = save_raw(path, out);
done:
    for (int i = 0; i < 4; ++i) pl_tex_destroy(gpu, &textures[i]);
    av_frame_free(&out);
    return ok;
}

static int receive_frame(AVFormatContext *format, AVCodecContext *codec,
                         int video, double seconds, AVFrame *frame)
{
    AVPacket *packet = av_packet_alloc();
    int result = AVERROR_EOF, draining = 0;
    int64_t target = (int64_t) (seconds / av_q2d(format->streams[video]->time_base));
    if (!packet) return AVERROR(ENOMEM);
    if (seconds > 0) {
        result = av_seek_frame(format, video, target, AVSEEK_FLAG_BACKWARD);
        if (result < 0) goto done;
        avcodec_flush_buffers(codec);
    }
    for (;;) {
        result = avcodec_receive_frame(codec, frame);
        if (result == 0) {
            int64_t pts = frame->best_effort_timestamp;
            if (pts == AV_NOPTS_VALUE || pts >= target) break;
            av_frame_unref(frame);
            continue;
        }
        if (result != AVERROR(EAGAIN)) break;
        do {
            av_packet_unref(packet);
            result = av_read_frame(format, packet);
        } while (result >= 0 && packet->stream_index != video);
        if (result < 0) {
            if (draining) break;
            draining = 1;
            result = avcodec_send_packet(codec, NULL);
        } else {
            result = avcodec_send_packet(codec, packet);
        }
        if (result < 0) break;
    }
done:
    av_packet_free(&packet);
    return result;
}

int wmain(int argc, wchar_t **argv)
{
    int result = 1, selftest = argc == 2 && !wcscmp(argv[1], L"--self-test");
    AVFormatContext *format = NULL;
    AVCodecContext *codec = NULL;
    AVFrame *frame = av_frame_alloc();
    pl_d3d11 d3d = NULL;
    pl_renderer renderer = NULL;
    pl_tex textures[4] = {0};
    struct pl_frame image = {0};
    int mapped = 0;
    char *input = NULL;
    FILE *report = NULL;
    pl_log log = pl_log_create(PL_API_VER, pl_log_params(
        .log_cb = log_message, .log_level = PL_LOG_INFO));
    if (!frame || !log) goto done;
    setbuf(stdout, NULL);
    av_log_set_level(AV_LOG_WARNING);
    if (selftest) {
        frame->format = AV_PIX_FMT_YUV420P10LE;
        frame->width = 64; frame->height = 32;
        frame->color_range = AVCOL_RANGE_MPEG;
        frame->colorspace = AVCOL_SPC_BT709;
        frame->color_primaries = AVCOL_PRI_BT709;
        frame->color_trc = AVCOL_TRC_BT709;
        if (av_frame_get_buffer(frame, 32) < 0) goto done;
        for (int p = 0; p < 3; ++p) {
            int h = p ? 16 : 32, w = p ? 32 : 64;
            for (int y = 0; y < h; ++y) {
                uint16_t *line = (uint16_t *) (frame->data[p] + y * frame->linesize[p]);
                for (int x = 0; x < w; ++x) line[x] = 512;
            }
        }
    } else {
        if (argc != 3) {
            fprintf(stderr, "Usage: color-probe input.mp4 seconds\n");
            goto done;
        }
        int len = WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, NULL, 0, NULL, NULL);
        input = calloc(len, 1);
        if (!input) goto done;
        WideCharToMultiByte(CP_UTF8, 0, argv[1], -1, input, len, NULL, NULL);
        if (avformat_open_input(&format, input, NULL, NULL) < 0) goto done;
        if (avformat_find_stream_info(format, NULL) < 0) goto done;
        const AVCodec *decoder = NULL;
        int video = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
        if (video < 0 || !decoder) goto done;
        codec = avcodec_alloc_context3(decoder);
        if (!codec) goto done;
        if (avcodec_parameters_to_context(codec, format->streams[video]->codecpar) < 0) goto done;
        codec->thread_count = 0;
        if (avcodec_open2(codec, decoder, NULL) < 0) goto done;
        double seconds = wcstod(argv[2], NULL);
        if (seconds < 0 || receive_frame(format, codec, video, seconds, frame) < 0) goto done;
        if (!av_frame_get_side_data(frame, AV_FRAME_DATA_DOVI_METADATA)) {
            fprintf(stderr, "FAIL: decoded frame has no Dolby Vision metadata\n");
            goto done;
        }
    }
    d3d = pl_d3d11_create(log, pl_d3d11_params(.allow_software = selftest));
    if (!d3d) goto done;
    renderer = pl_renderer_create(log, d3d->gpu);
    if (!renderer) goto done;
    if (!pl_map_avframe_ex(d3d->gpu, &image,
        pl_avframe_params(.frame = frame, .tex = textures, .map_dovi = true))) goto done;
    mapped = 1;
    int dolby = image.repr.sys == PL_COLOR_SYSTEM_DOLBYVISION && image.repr.dovi;
    if (!selftest && !dolby) {
        fprintf(stderr, "FAIL: renderer did not accept Dolby Vision metadata\n");
        goto done;
    }
    if (!render_frame(d3d->gpu, renderer, &image, "corrected-sdr.rgba", 0)) goto done;
    if (!render_frame(d3d->gpu, renderer, &image, "corrected-hdr.rgba64", 1)) goto done;
    if (!selftest) {
        struct pl_frame control = image;
        control.repr.sys = PL_COLOR_SYSTEM_BT_2020_NC;
        control.repr.dovi = NULL;
        if (!render_frame(d3d->gpu, renderer, &control, "uncorrected-sdr.rgba", 0)) goto done;
    }
    report = fopen("result.json", "wb");
    if (!report) goto done;
    fprintf(report,
        "{\n  \"width\": %d,\n  \"height\": %d,\n"
        "  \"pixel_format\": \"%s\",\n  \"dolby_metadata_applied\": %s,\n"
        "  \"software_adapter\": %s,\n  \"renderer\": \"libplacebo-d3d11\",\n"
        "  \"hdr_output\": \"BT.2020 PQ RGBA16, 1000 nit target\",\n"
        "  \"scope\": \"single-frame decode and offscreen render; no playback validation\"\n}\n",
        frame->width, frame->height, av_get_pix_fmt_name(frame->format),
        dolby ? "true" : "false", d3d->software ? "true" : "false");
    if (fclose(report)) { report = NULL; goto done; }
    report = NULL;
    printf("Frame decoded and rendered successfully: %dx%d, DV=%d, WARP=%d\n",
           frame->width, frame->height, dolby, d3d->software);
    result = 0;
done:
    if (report) fclose(report);
    if (mapped) pl_unmap_avframe(d3d->gpu, &image);
    if (d3d) for (int i = 0; i < 4; ++i) pl_tex_destroy(d3d->gpu, &textures[i]);
    pl_renderer_destroy(&renderer);
    pl_d3d11_destroy(&d3d);
    pl_log_destroy(&log);
    av_frame_free(&frame);
    avcodec_free_context(&codec);
    avformat_close_input(&format);
    free(input);
    if (result) fprintf(stderr, "Color proof failed; no player build is certified.\n");
    return result;
}
