/* Build on Windows, from this folder:
   x86_64-w64-mingw32-gcc -shared -O2 -o JesusKpekpesTune.dll tune.c -lm
   The text file is the equalizer. This plugin raises only the foot band.
   The foot band is raised by the same amount in every ear, so the loudness difference between the ears is kept.
   The lift only ever adds level: it eases back to nothing as the foot band gets loud and is never below the input.
   A gun is turned down in its own band, on the ear that fired. The other ear is never touched. */
#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#else
#define EXPORT
#endif

typedef int32_t i32;
typedef intptr_t iptr;

enum {
    OP_OPEN = 0,
    OP_CLOSE = 1,
    OP_RATE = 10,
    OP_BLOCK = 11,
    OP_MAINS = 12,
    OP_NAME = 45,
    OP_VENDOR = 47,
    OP_PRODUCT = 48,
    OP_VERSION = 49,
    OP_CANDO = 51,
    OP_VST = 58,
    OP_START = 71,
    OP_STOP = 72
};

typedef struct Effect Effect;
typedef iptr (*Dispatch)(Effect*, i32, i32, iptr, void*, float);
typedef void (*Replace)(Effect*, float**, float**, i32);

struct Effect {
    i32 magic;
    Dispatch dispatch;
    void (*older)(Effect*, float**, float**, i32);
    void (*set_param)(Effect*, i32, float);
    float (*get_param)(Effect*, i32);
    i32 programs;
    i32 params;
    i32 inputs;
    i32 outputs;
    i32 flags;
    iptr reserved_a;
    iptr reserved_b;
    i32 delay;
    i32 unused_a;
    i32 unused_b;
    float ratio;
    void* object;
    void* user;
    i32 identity;
    i32 version;
    Replace replace;
    Replace replace_double;
    char later[56];
};

typedef struct {
    float b0, b2, a1, a2, x1, x2, y1, y2;
} Stage;

typedef struct {
    Stage gun, foot;
    float foot_env, gun_env;
    float foot_attack, foot_release, gun_attack, gun_release;
} Lane;

typedef struct {
    Lane lane[8];
    float rate;
} Bank;

static void set_band(Stage* stage, float hz, float q, float rate) {
    float w = 2.f * 3.14159265f * hz / rate;
    float c = cosf(w);
    float alpha = sinf(w) / (2.f * q);
    float a0 = 1.f + alpha;
    stage->b0 = alpha / a0;
    stage->b2 = -alpha / a0;
    stage->a1 = -2.f * c / a0;
    stage->a2 = (1.f - alpha) / a0;
    stage->x1 = stage->x2 = stage->y1 = stage->y2 = 0.f;
}

static float tick(Stage* stage, float x) {
    float y = stage->b0 * x + stage->b2 * stage->x2 - stage->a1 * stage->y1 - stage->a2 * stage->y2;
    if (fabsf(y) < 1e-20f) y = 0.f;
    stage->x2 = stage->x1;
    stage->x1 = x;
    stage->y2 = stage->y1;
    stage->y1 = y;
    return y;
}

static void arm(Lane* lane, float rate) {
    if (rate < 1000.f) rate = 48000.f;
    memset(lane, 0, sizeof *lane);
    set_band(&lane->gun, 1400.f, 0.6f, rate);
    set_band(&lane->foot, 180.f, 1.0f, rate);
    lane->foot_attack = 1.f - expf(-1.f / (rate * 0.008f));
    lane->foot_release = 1.f - expf(-1.f / (rate * 0.100f));
    lane->gun_attack = 1.f - expf(-1.f / (rate * 0.003f));
    lane->gun_release = 1.f - expf(-1.f / (rate * 0.045f));
}

static float reduction(float env, float threshold, float slope) {
    float over = 20.f * log10f(env + 1e-8f) - threshold;
    float t;
    if (over > 3.f) return (over - 3.f) * slope;
    if (over > -3.f) {
        t = over + 3.f;
        return slope * t * t / 12.f;
    }
    return 0.f;
}

typedef struct {
    float gun_band, foot_band, other;
} Split;

static void split(Lane* lane, float x, Split* out) {
    float gun_band = tick(&lane->gun, x);
    float foot_band = tick(&lane->foot, x);
    float foot_level = fabsf(foot_band);
    float gun_level = fabsf(gun_band);
    lane->foot_env += (foot_level - lane->foot_env) * (foot_level > lane->foot_env ? lane->foot_attack : lane->foot_release);
    lane->gun_env += (gun_level - lane->gun_env) * (gun_level > lane->gun_env ? lane->gun_attack : lane->gun_release);
    if (lane->foot_env < 1e-20f) lane->foot_env = 0.f;
    if (lane->gun_env < 1e-20f) lane->gun_env = 0.f;
    out->gun_band = gun_band;
    out->foot_band = foot_band;
    out->other = x - gun_band - foot_band;
}

/* Linear gain for the foot band, never below 1. Up to +6 dB for a quiet foot, easing to 1 as the band gets loud. */
static float lift(float env) {
    float db = 6.f - reduction(env, -20.f, 0.5f);
    return db > 0.f ? powf(10.f, db / 20.f) : 1.f;
}

static float finish(Lane* lane, const Split* part, float foot_gain) {
    float gun_gain = powf(10.f, -reduction(lane->gun_env, -16.f, 2.f / 3.f) / 20.f);
    float y = part->other + part->foot_band * foot_gain + part->gun_band * gun_gain;
    float mag = fabsf(y);
    if (mag <= 0.8912509f) return y;
    return copysignf(0.8912509f + tanhf((mag - 0.8912509f) / 0.05f) * 0.05f, y);
}

static void replace(Effect* effect, float** inputs, float** outputs, i32 frames) {
    Bank* bank = effect ? effect->object : 0;
    Split part[8];
    i32 i, ch;
    if (!bank || frames < 1 || !inputs || !outputs) return;
    for (i = 0; i < frames; i++) {
        float strongest = 0.f;
        float foot_gain;
        for (ch = 0; ch < effect->inputs && ch < 8; ch++) {
            if (!inputs[ch] || !outputs[ch]) continue;
            split(&bank->lane[ch], inputs[ch][i], &part[ch]);
            if (bank->lane[ch].foot_env > strongest) strongest = bank->lane[ch].foot_env;
        }
        foot_gain = lift(strongest);
        for (ch = 0; ch < effect->inputs && ch < 8; ch++) {
            if (!inputs[ch] || !outputs[ch]) continue;
            outputs[ch][i] = finish(&bank->lane[ch], &part[ch], foot_gain);
        }
    }
}

static void set_param(Effect* effect, i32 index, float value) { (void)effect; (void)index; (void)value; }
static float get_param(Effect* effect, i32 index) { (void)effect; (void)index; return 0.f; }

static iptr dispatch(Effect* effect, i32 op, i32 index, iptr value, void* ptr, float opt) {
    Bank* bank = effect ? effect->object : 0;
    i32 ch;
    (void)index;
    (void)value;
    if (!effect) return 0;
    if (op == OP_OPEN) return 1;
    if (op == OP_CLOSE) {
        if (bank) free(bank);
        free(effect);
        return 1;
    }
    if (op == OP_RATE && bank && opt > 0.f) {
        bank->rate = opt;
        for (ch = 0; ch < 8; ch++) arm(&bank->lane[ch], opt);
        return 1;
    }
    if (op == OP_BLOCK || op == OP_MAINS || op == OP_START || op == OP_STOP) return 1;
    if (op == OP_VST) return 2400;
    if (op == OP_VERSION) return 1000;
    if (op == OP_NAME || op == OP_PRODUCT) {
        if (ptr) strncpy(ptr, "Jesus Kpekpes", 24);
        return 1;
    }
    if (op == OP_VENDOR) {
        if (ptr) strncpy(ptr, "Jesus Kpekpes", 24);
        return 1;
    }
    if (op == OP_CANDO) return 0;
    return 0;
}

EXPORT Effect* VSTPluginMain(void* host) {
    Effect* effect = (Effect*)calloc(1, sizeof(Effect));
    Bank* bank = (Bank*)calloc(1, sizeof(Bank));
    i32 ch;
    (void)host;
    if (!effect || !bank) {
        free(effect);
        free(bank);
        return 0;
    }
    for (ch = 0; ch < 8; ch++) arm(&bank->lane[ch], 48000.f);
    bank->rate = 48000.f;
    effect->magic = 0x56737450;
    effect->dispatch = dispatch;
    effect->older = (void (*)(Effect*, float**, float**, i32))replace;
    effect->set_param = set_param;
    effect->get_param = get_param;
    effect->inputs = 8;
    effect->outputs = 8;
    effect->flags = 1 << 4;
    effect->delay = 0;
    effect->object = bank;
    effect->identity = 0x4A6B5475;
    effect->version = 1000;
    effect->replace = replace;
    return effect;
}

EXPORT Effect* main_plugin(void* host) { return VSTPluginMain(host); }

#ifdef TUNE_CHECK
#include <stdio.h>

static double rms_db(const float* x, int from, int to) {
    double sum = 0;
    int i;
    for (i = from; i < to; i++) sum += (double)x[i] * x[i];
    return 10.0 * log10(sum / (to - from) + 1e-20);
}

/* A foot in one ear, 15 dB quieter in the other. The plugin must keep that gap and must never turn a foot-band tone down. */
static int check_ears(void) {
    enum { sr = 48000, n = 48000 };
    static float l[n], r[n], lin[n], rin[n];
    static const double levels[] = { -35, -30, -20, -12, -6 };
    int bad = 0, k, i;
    for (k = 0; k < 5; k++) {
        float a_l = powf(10.f, (float)levels[k] / 20.f) * 1.41421356f;
        float a_r = a_l * powf(10.f, -15.f / 20.f);
        Effect* e = VSTPluginMain(0);
        float* in[8] = { l, r };
        float* out[8] = { l, r };
        double gap_in, gap_out, l_in, r_in, l_out, r_out;
        for (i = 0; i < n; i++) {
            float s = sinf(2.f * 3.14159265f * 180.f * i / sr);
            lin[i] = l[i] = s * a_l;
            rin[i] = r[i] = s * a_r;
        }
        replace(e, in, out, n);
        l_in = rms_db(lin, n / 2, n); r_in = rms_db(rin, n / 2, n);
        l_out = rms_db(l, n / 2, n); r_out = rms_db(r, n / 2, n);
        gap_in = l_in - r_in; gap_out = l_out - r_out;
        printf("left %4.0f dBFS: gap in %.2f out %.2f, left %+.2f dB, right %+.2f dB\n", levels[k], gap_in, gap_out, l_out - l_in, r_out - r_in);
        if (fabs(gap_out - gap_in) > 0.1) bad = 1;
        if (l_out < l_in - 0.05 || r_out < r_in - 0.05) bad = 1;
        dispatch(e, OP_CLOSE, 0, 0, 0, 0);
    }
    return bad;
}
int main(void) {
    enum { sr = 48000, n = 48000 };
    static float left[n], right[n];
    Effect* effect;
    float* inputs[8] = { left, right };
    float* outputs[8] = { left, right };
    int at = (int)(sr * 0.4f);
    int len = (int)(sr * 0.032f);
    int i;
    double quiet = 0, loud = 0;
    float peak = 0.f;
    _Static_assert(sizeof(void*) == 8, "64-bit");
    _Static_assert(offsetof(Effect, replace) == 120, "host layout");
    effect = VSTPluginMain(0);
    for (i = 0; i < len; i++) {
        float env = sinf((float)i / (float)len * 3.14159265f);
        left[at + i] = sinf(2.f * 3.14159265f * 180.f * (at + i) / sr) * 0.03f * env;
    }
    for (i = 0; i < n; i++) right[i] = sinf(2.f * 3.14159265f * 1000.f * i / sr) * 0.9f;
    replace(effect, inputs, outputs, n);
    for (i = at; i < at + (int)(sr * 0.06f); i++) quiet += (double)left[i] * left[i];
    for (i = n / 2; i < n / 2 + 2000; i++) {
        loud += (double)right[i] * right[i];
        if (fabsf(right[i]) > peak) peak = fabsf(right[i]);
        if (fabsf(left[i]) > peak) peak = fabsf(left[i]);
    }
    printf("quiet_energy %.6f loud_rms %.4f peak %.3f layout %d\n", quiet, sqrt(loud / 2000.0), peak, (int)offsetof(Effect, replace));
    dispatch(effect, OP_CLOSE, 0, 0, 0, 0);
    if (!(quiet > 0.0008 && peak < 0.98f)) return 1;
    return check_ears();
}
#endif
