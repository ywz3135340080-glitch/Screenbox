#pragma once
#include <stdint.h>
#ifdef _WIN32
#define SB_API __declspec(dllexport)
#else
#define SB_API
#endif
typedef int (*sb_read_fn)(void *user, uint8_t *buffer, int length);
typedef int64_t (*sb_seek_fn)(void *user, int64_t offset, int origin);
typedef struct sb_decoder sb_decoder;
typedef struct sb_info {
    int32_t width, height, sample_rate, channels;
    int32_t fps_num, fps_den;
    int64_t duration_ticks;
    uint32_t channel_mask;
    int32_t reserved;
} sb_info;
typedef struct sb_sample {
    uint8_t *data;
    int32_t length, reserved;
    int64_t timestamp_ticks, duration_ticks;
} sb_sample;
/* A decoder owns one selected stream. Use independent input streams for audio/video.
   All calls on one decoder must be serialized. Returned pixels remain valid until next(). */
SB_API int sb_abi_version(void);
SB_API int sb_open(sb_read_fn read, sb_seek_fn seek, void *user, int kind,
                   sb_decoder **decoder, sb_info *info);
SB_API int sb_next(sb_decoder *decoder, sb_sample *sample);
SB_API int sb_seek(sb_decoder *decoder, int64_t ticks);
SB_API void sb_close(sb_decoder *decoder);
SB_API int sb_self_test(void);
