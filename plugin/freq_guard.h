#ifndef FREQ_GUARD_H
#define FREQ_GUARD_H

#include <math.h>
#include <string.h>

#define FG_MAX_CHANNELS 8

enum {
    FG_P_GUN_VOL   = 0, FG_P_TARGET    = 1, FG_P_RELEASE   = 2, 
    FG_P_SC_MIX    = 3, FG_P_DET_REL   = 4, FG_P_LIM_REL   = 5, 
    FG_P_SC_LOW    = 6, FG_P_SC_HIGH   = 7, FG_P_GATE      = 8, 
    FG_P_BAND_MODE = 9, FG_P_AMB_GUARD = 10, FG_P_ENABLED   = 11, 
    FG_P_BYPASS    = 12   
};

typedef struct { float b0, b1, b2, a1, a2, z1, z2; } fg_biquad_t;
typedef struct { float g_hpf, a_lpf, x_prev, y1_prev, y2_prev; } fg_detector_t;

typedef struct {
    fg_detector_t det;
    fg_biquad_t   filters[6];
} fg_channel_t;

typedef struct {
    float params[13];
    float sample_rate;
    int   num_channels;
    float global_envelope;
    float current_gain;
    float attack_coeff;
    float release_coeff;
    float sum_sq;
    fg_channel_t ch[FG_MAX_CHANNELS];
} fg_state_t;

static inline float smoothstep01(float x) {
    if (x < 0.0f) x = 0.0f;
    if (x > 1.0f) x = 1.0f;
    return x * x * (3.0f - 2.0f * x);
}

static inline float fg_coeff_from_ms(float ms, float sr) {
    if (ms <= 0.0f) return 0.0f;
    return 1.0f - expf(-1.0f / (ms * 0.001f * sr));
}

static void fg_biquad_init_hpf(fg_biquad_t *f, float fc, float fs, float Q) {
    float w0 = 2.0f * 3.14159265f * fc / fs;
    float cw = cosf(w0); float sw = sinf(w0);
    float alpha = sw / (2.0f * Q); float a0 = 1.0f + alpha; float n = 1.0f / a0;
    f->b0 = (1.0f + cw) * 0.5f * n; f->b1 = -(1.0f + cw) * n; f->b2 = f->b0;
    f->a1 = -2.0f * cw * n; f->a2 = (1.0f - alpha) * n;
    f->z1 = f->z2 = 0.0f;
}

static void fg_biquad_init_lpf(fg_biquad_t *f, float fc, float fs, float Q) {
    float w0 = 2.0f * 3.14159265f * fc / fs;
    float cw = cosf(w0); float sw = sinf(w0);
    float alpha = sw / (2.0f * Q); float a0 = 1.0f + alpha; float n = 1.0f / a0;
    f->b0 = (1.0f - cw) * 0.5f * n; f->b1 = (1.0f - cw) * n; f->b2 = f->b0;
    f->a1 = -2.0f * cw * n; f->a2 = (1.0f - alpha) * n;
    f->z1 = f->z2 = 0.0f;
}

static inline float fg_biquad_process(fg_biquad_t *f, float x) {
    float y = f->b0 * x + f->z1;
    f->z1 = f->b1 * x - f->a1 * y + f->z2;
    f->z2 = f->b2 * x - f->a2 * y;
    return y;
}

static inline float fg_detector_process(fg_detector_t *f, float x) {
    float y1 = (x + f->y1_prev - f->x_prev) * f->g_hpf;
    f->x_prev = x; f->y1_prev = y1;
    float y2 = f->y2_prev + f->a_lpf * (y1 - f->y2_prev);
    f->y2_prev = y2;
    return y2;
}

static void fg_init(fg_state_t *s, float sr, int num_channels) {
    memset(s, 0, sizeof(fg_state_t));
    s->sample_rate = sr; s->num_channels = num_channels;
    s->params[FG_P_GUN_VOL] = 0.18f;
    s->params[FG_P_TARGET] = 0.62f;
    s->params[FG_P_RELEASE] = 0.122f;
    s->params[FG_P_SC_MIX] = 0.50f;
    s->params[FG_P_AMB_GUARD] = 0.72f;
    s->params[FG_P_ENABLED] = 1.0f;
    s->attack_coeff = fg_coeff_from_ms(1.0f, sr);
    s->release_coeff = fg_coeff_from_ms(122.0f, sr);
    s->current_gain = 1.0f;
    for (int c = 0; c < num_channels; c++) {
        s->ch[c].det.g_hpf = 1.0f - expf(-2.0f * 3.14159265f * 20.0f / sr);
        s->ch[c].det.a_lpf = 1.0f - expf(-2.0f * 3.14159265f * 5000.0f / sr);
        fg_biquad_init_hpf(&s->ch[c].filters[0], 80.0f, sr, 0.707f);
        fg_biquad_init_lpf(&s->ch[c].filters[1], 4000.0f, sr, 0.707f);
    }
}

static void fg_process_block(fg_state_t *s, float **audio, int num_samples) {
    if (s->params[FG_P_ENABLED] < 0.5f) return;
    float threshold = s->params[FG_P_TARGET];
    float vol_reduction = s->params[FG_P_GUN_VOL];
    for (int i = 0; i < num_samples; i++) {
        s->sum_sq = 0.0f;
        for (int c = 0; c < s->num_channels; c++) {
            float x = audio[c][i];
            float det_out = fg_detector_process(&s->ch[c].det, x);
            s->sum_sq += (det_out * det_out);
        }
        float mixed_energy = s->sum_sq / (float)s->num_channels;
        float e_coeff = (mixed_energy > s->global_envelope) ? s->attack_coeff : s->release_coeff;
        s->global_envelope = (1.0f - e_coeff) * mixed_energy + e_coeff * s->global_envelope;
        float target_gain = 1.0f;
        if (s->global_envelope > threshold) {
            float excess = (s->global_envelope - threshold) / (1.0f - threshold);
            float suppression = smoothstep01(excess);
            target_gain = 1.0f - suppression * (1.0f - vol_reduction);
        }
        float g_coeff = (target_gain < s->current_gain) ? s->attack_coeff : s->release_coeff;
        s->current_gain = (1.0f - g_coeff) * target_gain + g_coeff * s->current_gain;
        if (s->current_gain < vol_reduction) s->current_gain = vol_reduction;
        if (s->current_gain > 1.0f) s->current_gain = 1.0f;
        for (int c = 0; c < s->num_channels; c++) {
            float x = audio[c][i];
            x = fg_biquad_process(&s->ch[c].filters[0], x);
            x = fg_biquad_process(&s->ch[c].filters[1], x);
            audio[c][i] = x * s->current_gain;
        }
    }
}
#endif
