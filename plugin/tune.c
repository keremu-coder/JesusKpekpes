/* Build on Windows, from this folder:
   x86_64-w64-mingw32-gcc -shared -O2 -o JesusKpekpesTune.dll tune.c -lm
   The text file is the equalizer. Classic mode is the default. It raises only the foot band.
   The foot band is raised by the same amount in every ear, so the loudness difference between the ears is kept.
   The lift only ever adds level: it eases back to nothing as the foot band gets loud and is never below the input.
   A gun is turned down in its own band, on the ear that fired. The other ear is never touched.
   Nova mode is off until the host sets the Nova parameter. In that mode a step is lifted only while it is a hit. */
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
    OP_PARAM_NAME = 8,
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
    Stage gun, foot, tick;
    float foot_env, gun_env;
    float foot_attack, foot_release, gun_attack, gun_release;
    float weight_fast, weight_slow, tick_fast, tick_slow;
    float fast_a, fast_r, slow_a, slow_r;
} Lane;

/* Steady-ambience trim, the Calm parameter. It is off unless Calm is above 0, and then the output of every other
   part of this file is unchanged. It turns down only what is outside the foot band and the tick band, and only after
   that sound has stayed steady for half a second. A change in any band lets it go again within about 12 ms. */
typedef struct {
    Stage tick;
    float foot_f, foot_s, tick_f, tick_s, gun_f, gun_s, rest_f, rest_s;
} AmbLane;

typedef struct {
    AmbLane lane[8];
    float fast, slow, reference, engage, release;
    float rest_ref;
    float gain;
    float floor;
    int hold, steady, hold_len, steady_len;
} Amb;

typedef struct {
    Lane lane[8];
    float rate;
    float hit;
    int mode;
    float calm;
    Amb amb;
} Bank;

static void set_band(Stage* stage, float hz, float q, float rate) {
    /* 3.14159274f is the same float the C# preview uses for pi, so the two filters match. */
    float w = 2.f * 3.14159274f * hz / rate;
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

static void follow(float* env, float level, float attack, float release) {
    *env += (level - *env) * (level > *env ? attack : release);
    if (*env < 1e-20f) *env = 0.f;
}

static void arm_nova(Lane* lane, float rate) {
    if (rate < 1000.f) rate = 48000.f;
    memset(lane, 0, sizeof *lane);
    set_band(&lane->gun, 1400.f, 0.7f, rate);
    set_band(&lane->foot, 220.f, 1.0f, rate);
    set_band(&lane->tick, 3200.f, 1.1f, rate);
    lane->fast_a = 1.f - expf(-1.f / (rate * 0.0025f));
    lane->fast_r = 1.f - expf(-1.f / (rate * 0.040f));
    lane->slow_a = 1.f - expf(-1.f / (rate * 0.080f));
    lane->slow_r = 1.f - expf(-1.f / (rate * 0.400f));
    lane->gun_attack = 1.f - expf(-1.f / (rate * 0.003f));
    lane->gun_release = 1.f - expf(-1.f / (rate * 0.045f));
}

/* One gain for every ear. A steady band (ratio near 1) stays at 1. A quiet hit is lifted, up to max_db. */
static float transient_gain(float fast, float ratio, float max_db) {
    float novelty, edge, level, quiet, db;
    if (max_db <= 0.05f || fast < 1e-6f || ratio < 1.15f) return 1.f;
    novelty = 20.f * log10f(ratio);
    if (novelty > 18.f) novelty = 18.f;
    edge = novelty / 12.f;
    if (edge > 1.f) edge = 1.f;
    level = 20.f * log10f(fast + 1e-8f);
    quiet = (-6.f - level) / 26.f;
    if (quiet < 0.f) quiet = 0.f;
    if (quiet > 1.f) quiet = 1.f;
    db = max_db * quiet * edge;
    if (db <= 0.f) return 1.f;
    return powf(10.f, db / 20.f);
}

static void consider(float fast, float slow, float* best_fast, float* best_ratio) {
    float ratio = fast / (slow + 1e-5f);
    if (fast > *best_fast) *best_fast = fast;
    if (ratio > *best_ratio) *best_ratio = ratio;
}

#define AMB_MAX_DB 9.f

static void amb_arm(Bank* bank) {
    Amb* a = &bank->amb;
    float rate = bank->rate < 1000.f ? 48000.f : bank->rate;
    int ch;
    memset(a, 0, sizeof *a);
    for (ch = 0; ch < 8; ch++) set_band(&a->lane[ch].tick, 3200.f, 1.1f, rate);
    a->fast = 1.f - expf(-1.f / (rate * 0.008f));
    a->slow = 1.f - expf(-1.f / (rate * 0.120f));
    a->reference = 1.f - expf(-1.f / (rate * 0.800f));
    a->engage = 1.f - expf(-1.f / (rate * 0.300f));
    a->release = 1.f - expf(-1.f / (rate * 0.012f));
    a->hold_len = (int)(rate * 0.150f);
    a->steady_len = (int)(rate * 0.500f);
    a->floor = powf(10.f, -AMB_MAX_DB * bank->calm / 20.f);
    a->gain = 1.f;
}

static void amb_follow(float* fast, float* slow, float level, float fast_a, float slow_a) {
    *fast += (level - *fast) * fast_a;
    *slow += (level - *slow) * slow_a;
    if (*fast < 1e-20f) *fast = 0.f;
    if (*slow < 1e-20f) *slow = 0.f;
}

static int amb_moved(float fast, float slow, float limit) {
    return fast > 1e-4f && fast > slow * limit;
}

/* One gain for every ear, so the gap between the ears is kept. Each array holds this sample of one part for every ear:
   the foot band, the tick band, the gun band, and everything outside the foot and tick bands. */
static float amb_gain(Bank* bank, const float* foot, const float* tickb, const float* gun, const float* rest, i32 nch) {
    Amb* a = &bank->amb;
    float strongest_f = 0.f, strongest_s = 0.f, target, coeff;
    int moved = 0;
    i32 ch;
    for (ch = 0; ch < nch; ch++) {
        AmbLane* l = &a->lane[ch];
        amb_follow(&l->foot_f, &l->foot_s, fabsf(foot[ch]), a->fast, a->slow);
        amb_follow(&l->tick_f, &l->tick_s, fabsf(tickb[ch]), a->fast, a->slow);
        amb_follow(&l->gun_f, &l->gun_s, fabsf(gun[ch]), a->fast, a->slow);
        amb_follow(&l->rest_f, &l->rest_s, fabsf(rest[ch]), a->fast, a->slow);
        if (amb_moved(l->foot_f, l->foot_s, 1.25f) || amb_moved(l->tick_f, l->tick_s, 1.25f) ||
            amb_moved(l->gun_f, l->gun_s, 1.25f) || amb_moved(l->rest_f, l->rest_s, 1.4f))
            moved = 1;
        if (l->rest_s > strongest_s) { strongest_s = l->rest_s; strongest_f = l->rest_f; }
    }
    a->rest_ref += (strongest_s - a->rest_ref) * a->reference;
    if (moved) {
        a->hold = a->hold_len;
        a->steady = 0;
    } else if (a->hold > 0) {
        a->hold--;
        a->steady = 0;
    } else if (strongest_s > 3e-4f && strongest_f > strongest_s * 0.8f &&
               strongest_s < a->rest_ref * 1.19f && strongest_s * 1.19f > a->rest_ref) {
        if (a->steady < a->steady_len) a->steady++;
    } else {
        a->steady = 0;
    }
    target = a->steady >= a->steady_len ? a->floor : 1.f;
    coeff = target < a->gain ? a->engage : a->release;
    a->gain += (target - a->gain) * coeff;
    if (a->gain > 0.99999f) a->gain = 1.f;
    return a->gain;
}

/* The same as finish, with the trim added before the limiter. */
static float finish_calm(Lane* lane, const Split* part, float foot_gain, float rest, float calm_gain) {
    float gun_gain = powf(10.f, -reduction(lane->gun_env, -16.f, 2.f / 3.f) / 20.f);
    float y = part->other + part->foot_band * foot_gain + part->gun_band * gun_gain;
    float mag;
    if (calm_gain != 1.f) y += (calm_gain - 1.f) * rest;
    mag = fabsf(y);
    if (mag <= 0.8912509f) return y;
    return copysignf(0.8912509f + tanhf((mag - 0.8912509f) / 0.05f) * 0.05f, y);
}

static void nova_replace(Effect* effect, float** inputs, float** outputs, i32 frames) {
    Bank* bank = effect->object;
    float max_weight = 7.f * bank->hit;
    float max_tick = 10.f * bank->hit;
    i32 i, ch, nch = effect->inputs;
    if (nch > 8) nch = 8;
    for (i = 0; i < frames; i++) {
        float best_w_fast = 0.f, best_w_ratio = 1.f, best_t_fast = 0.f, best_t_ratio = 1.f;
        float weight_gain, tick_gain, calm_gain = 1.f;
        float weight_b[8] = { 0 }, tick_b[8] = { 0 }, gun_b[8] = { 0 }, other_b[8] = { 0 }, rest_b[8] = { 0 };
        for (ch = 0; ch < nch; ch++) {
            Lane* lane = &bank->lane[ch];
            float x, w, t, g;
            if (!inputs[ch] || !outputs[ch]) continue;
            x = inputs[ch][i];
            w = tick(&lane->foot, x);
            t = tick(&lane->tick, x);
            g = tick(&lane->gun, x);
            weight_b[ch] = w;
            tick_b[ch] = t;
            gun_b[ch] = g;
            other_b[ch] = x - w - t - g;
            if (bank->calm > 0.f) rest_b[ch] = x - w - t;
            follow(&lane->weight_fast, fabsf(w), lane->fast_a, lane->fast_r);
            follow(&lane->weight_slow, fabsf(w), lane->slow_a, lane->slow_r);
            follow(&lane->tick_fast, fabsf(t), lane->fast_a, lane->fast_r);
            follow(&lane->tick_slow, fabsf(t), lane->slow_a, lane->slow_r);
            follow(&lane->gun_env, fabsf(g), lane->gun_attack, lane->gun_release);
            consider(lane->weight_fast, lane->weight_slow, &best_w_fast, &best_w_ratio);
            consider(lane->tick_fast, lane->tick_slow, &best_t_fast, &best_t_ratio);
        }
        weight_gain = transient_gain(best_w_fast, best_w_ratio, max_weight);
        tick_gain = transient_gain(best_t_fast, best_t_ratio, max_tick);
        if (bank->calm > 0.f) calm_gain = amb_gain(bank, weight_b, tick_b, gun_b, rest_b, nch);
        for (ch = 0; ch < nch; ch++) {
            Lane* lane;
            float gun_gain, y, mag;
            if (!inputs[ch] || !outputs[ch]) continue;
            lane = &bank->lane[ch];
            gun_gain = powf(10.f, -reduction(lane->gun_env, -16.f, 2.f / 3.f) / 20.f);
            y = other_b[ch] + weight_b[ch] * weight_gain + tick_b[ch] * tick_gain + gun_b[ch] * gun_gain;
            if (calm_gain != 1.f) y += (calm_gain - 1.f) * rest_b[ch];
            mag = fabsf(y);
            if (mag > 0.8912509f)
                y = copysignf(0.8912509f + tanhf((mag - 0.8912509f) / 0.05f) * 0.05f, y);
            outputs[ch][i] = y;
        }
    }
}

static void replace(Effect* effect, float** inputs, float** outputs, i32 frames) {
    Bank* bank = effect ? effect->object : 0;
    if (bank && bank->mode) {
        if (!inputs || !outputs || frames < 1) return;
        nova_replace(effect, inputs, outputs, frames);
        return;
    }
    Split part[8];
    i32 i, ch;
    if (!bank || frames < 1 || !inputs || !outputs) return;
    for (i = 0; i < frames; i++) {
        float strongest = 0.f;
        float foot_gain, calm_gain = 1.f;
        float foot_b[8] = { 0 }, tick_b[8] = { 0 }, gun_b[8] = { 0 }, rest_b[8] = { 0 };
        for (ch = 0; ch < effect->inputs && ch < 8; ch++) {
            if (!inputs[ch] || !outputs[ch]) continue;
            split(&bank->lane[ch], inputs[ch][i], &part[ch]);
            if (bank->lane[ch].foot_env > strongest) strongest = bank->lane[ch].foot_env;
            if (bank->calm > 0.f) {
                foot_b[ch] = part[ch].foot_band;
                gun_b[ch] = part[ch].gun_band;
                tick_b[ch] = tick(&bank->amb.lane[ch].tick, inputs[ch][i]);
                rest_b[ch] = inputs[ch][i] - foot_b[ch] - tick_b[ch];
            }
        }
        foot_gain = lift(strongest);
        if (bank->calm > 0.f) calm_gain = amb_gain(bank, foot_b, tick_b, gun_b, rest_b, effect->inputs < 8 ? effect->inputs : 8);
        for (ch = 0; ch < effect->inputs && ch < 8; ch++) {
            if (!inputs[ch] || !outputs[ch]) continue;
            if (bank->calm > 0.f) outputs[ch][i] = finish_calm(&bank->lane[ch], &part[ch], foot_gain, rest_b[ch], calm_gain);
            else outputs[ch][i] = finish(&bank->lane[ch], &part[ch], foot_gain);
        }
    }
}

static void set_param(Effect* effect, i32 index, float value) {
    Bank* bank = effect ? effect->object : 0;
    i32 ch;
    if (!bank) return;
    if (index == 0) {
        int mode = value >= 0.5f ? 1 : 0;
        if (mode == bank->mode) return;
        bank->mode = mode;
        for (ch = 0; ch < 8; ch++) {
            if (mode) arm_nova(&bank->lane[ch], bank->rate);
            else arm(&bank->lane[ch], bank->rate);
        }
        amb_arm(bank);
        return;
    }
    if (index == 1) {
        if (value < 0.f) value = 0.f;
        if (value > 1.f) value = 1.f;
        bank->hit = value;
        return;
    }
    if (index == 2) {
        if (!(value > 0.f)) value = 0.f;
        if (value > 1.f) value = 1.f;
        if (value == bank->calm) return;
        bank->calm = value;
        amb_arm(bank);
    }
}

static float get_param(Effect* effect, i32 index) {
    Bank* bank = effect ? effect->object : 0;
    if (!bank) return 0.f;
    if (index == 0) return bank->mode ? 1.f : 0.f;
    if (index == 1) return bank->hit;
    if (index == 2) return bank->calm;
    return 0.f;
}

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
        for (ch = 0; ch < 8; ch++) {
            if (bank->mode) arm_nova(&bank->lane[ch], opt);
            else arm(&bank->lane[ch], opt);
        }
        amb_arm(bank);
        return 1;
    }
    if (op == OP_PARAM_NAME && ptr) {
        const char* name = index == 0 ? "Nova" : index == 1 ? "Hit" : index == 2 ? "Calm" : "";
        strncpy((char*)ptr, name, 8);
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
    bank->hit = 1.f;
    bank->mode = 0;
    bank->calm = 0.f;
    amb_arm(bank);
    effect->magic = 0x56737450;
    effect->dispatch = dispatch;
    effect->older = (void (*)(Effect*, float**, float**, i32))replace;
    effect->set_param = set_param;
    effect->get_param = get_param;
    effect->inputs = 8;
    effect->outputs = 8;
    effect->flags = 1 << 4;
    effect->params = 3;
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
#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

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

static double energy(const float* x, int from, int count) {
    double sum = 0;
    int i;
    for (i = 0; i < count; i++) sum += (double)x[from + i] * x[from + i];
    return sum;
}

/* Nova is a separate mode. A steady 3.2 kHz tone must not be lifted. A short hit must be. */
static int check_nova(void) {
    enum { sr = 48000, n = 48000 };
    static float tone[n], tone_r[n], dry[n];
    static float burst[n], burst_r[n], burst_dry[n];
    Effect* e = VSTPluginMain(0);
    float* in[8] = { tone, tone_r };
    float* out[8] = { tone, tone_r };
    char name0[9], name1[9];
    int i, bad = 0;
    int burst_at = n / 3;
    int burst_len = (int)(sr * 0.032f);
    double steady, hit;
    memset(name0, 0, sizeof name0);
    memset(name1, 0, sizeof name1);
    dispatch(e, OP_PARAM_NAME, 0, 0, name0, 0.f);
    dispatch(e, OP_PARAM_NAME, 1, 0, name1, 0.f);
    if (strcmp(name0, "Nova") != 0 || strcmp(name1, "Hit") != 0 || e->params != 3) bad = 1;
    if (get_param(e, 0) != 0.f || get_param(e, 1) != 1.f) bad = 1;
    e->set_param(e, 0, 1.f);
    e->set_param(e, 1, 1.f);
    for (i = 0; i < n; i++) tone[i] = sinf(2.f * 3.14159265f * 3200.f * i / sr) * 0.03f;
    memcpy(dry, tone, sizeof dry);
    memset(tone_r, 0, sizeof tone_r);
    replace(e, in, out, n);
    steady = energy(tone, n - n / 5, n / 5) / energy(dry, n - n / 5, n / 5);
    printf("nova steady gain %.3f\n", steady);
    if (!(steady > 0.7 && steady < 1.4)) bad = 1;
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);

    memset(burst, 0, sizeof burst);
    memset(burst_r, 0, sizeof burst_r);
    for (i = 0; i < burst_len; i++) {
        float env = sinf((float)i / (float)burst_len * 3.14159265f);
        burst[burst_at + i] = sinf(2.f * 3.14159265f * 3200.f * (burst_at + i) / sr) * 0.03f * env;
    }
    memcpy(burst_dry, burst, sizeof burst);
    e = VSTPluginMain(0);
    e->set_param(e, 0, 1.f);
    in[0] = burst; in[1] = burst_r; out[0] = burst; out[1] = burst_r;
    replace(e, in, out, n);
    hit = energy(burst, burst_at, burst_len + 400) / energy(burst_dry, burst_at, burst_len + 400);
    printf("nova burst gain %.3f\n", hit);
    if (!(hit > steady * 2.0)) bad = 1;
    {
        static float z[64], zr[64];
        float* zin[8] = { z, zr };
        float* zout[8] = { z, zr };
        replace(e, zin, zout, 64);
        for (i = 0; i < 64; i++) if (z[i] != 0.f || zr[i] != 0.f) bad = 1;
    }
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    return bad;
}

/* Calm. Steady background is trimmed. Nothing that is not steady may be. */
enum { calm_max = 480000 };
static float calm_in[2][calm_max], calm_a[2][calm_max], calm_b[2][calm_max];

static float calm_noise(unsigned* seed) {
    *seed = *seed * 1664525u + 1013904223u;
    return (float)(*seed >> 8) / 8388608.f - 1.f;
}

static double tone_pow(const float* x, int from, int count, double hz, double rate) {
    double w = 6.283185307179586 * hz / rate, c = 2.0 * cos(w), s1 = 0, s2 = 0, s0;
    int i;
    for (i = 0; i < count; i++) { s0 = x[from + i] + c * s1 - s2; s2 = s1; s1 = s0; }
    return (s1 * s1 + s2 * s2 - c * s1 * s2) / ((double)count * (double)count) + 1e-30;
}

static double db_of(double a, double b) { return 10.0 * log10(a / b); }

static void calm_run(int mode, float calm, int rate, int n, float (*out)[calm_max]) {
    Effect* e = VSTPluginMain(0);
    float* in[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
    float* ou[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
    in[0] = calm_in[0]; in[1] = calm_in[1]; ou[0] = out[0]; ou[1] = out[1];
    dispatch(e, OP_RATE, 0, 0, 0, (float)rate);
    if (mode) { e->set_param(e, 0, 1.f); e->set_param(e, 1, 1.f); }
    if (calm > 0.f) e->set_param(e, 2, calm);
    replace(e, in, ou, n);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
}

/* The same input with Calm off (calm_a) and on (calm_b). */
static void calm_pair(int mode, float calm, int rate, int n) {
    calm_run(mode, 0.f, rate, n, calm_a);
    calm_run(mode, calm, rate, n, calm_b);
}

/* Steady background at 700 Hz and 6 kHz, 15 dB quieter in the right ear. */
static void calm_ambience(int rate, int n, int with_foot_tick) {
    int i;
    for (i = 0; i < n; i++) {
        double t = (double)i / rate;
        float s = (float)(0.03 * sin(6.283185307179586 * 700.0 * t) + 0.02 * sin(6.283185307179586 * 6000.0 * t));
        if (with_foot_tick) s += (float)(0.02 * sin(6.283185307179586 * 180.0 * t) + 0.02 * sin(6.283185307179586 * 3200.0 * t));
        calm_in[0][i] = s;
        calm_in[1][i] = s * 0.17782794f;
    }
}

static void calm_burst(float* x, int at, int len, double hz, double amp, int rate) {
    int i;
    for (i = 0; i < len; i++)
        x[at + i] += (float)(amp * sin(6.283185307179586 * hz * (double)(at + i) / rate) * (0.5 - 0.5 * cos(6.283185307179586 * (double)i / len)));
}

#define CALM_FAIL(cond, ...) do { if (cond) { printf(__VA_ARGS__); bad = 1; } } while (0)

static int check_calm(void) {
    static const int rates[] = { 48000, 44100, 96000 };
    int bad = 0, m, r, k, i, n, rate, from, len, at;
    float calm;
    double a7, a6, r7, ceiling;
    unsigned seed = 7u;
    char name2[9];

    /* A. Steady background is trimmed, by no more than the setting, and the ears keep their gap. */
    for (m = 0; m < 2; m++) for (r = 0; r < 3; r++) for (k = 0; k < 2; k++) {
        rate = rates[r]; n = rate * 5; from = rate * 4; len = rate; calm = k ? 0.5f : 1.f;
        ceiling = 9.0 * calm;
        calm_ambience(rate, n, 0);
        calm_pair(m, calm, rate, n);
        a7 = db_of(tone_pow(calm_b[0], from, len, 700, rate), tone_pow(calm_a[0], from, len, 700, rate));
        a6 = db_of(tone_pow(calm_b[0], from, len, 6000, rate), tone_pow(calm_a[0], from, len, 6000, rate));
        r7 = db_of(tone_pow(calm_b[1], from, len, 700, rate), tone_pow(calm_a[1], from, len, 700, rate));
        printf("calm steady mode %d rate %5d calm %.1f: 700 Hz %+.2f dB, 6 kHz %+.2f dB, ear gap moved %.2f dB\n", m, rate, calm, a7, a6, fabs(a7 - r7));
        CALM_FAIL(a7 > -3.0 * calm || a6 > -3.0 * calm, "  FAIL: steady background was not trimmed enough\n");
        CALM_FAIL(a7 < -(ceiling + 0.5) || a6 < -(ceiling + 0.5), "  FAIL: trimmed by more than the setting\n");
        CALM_FAIL(fabs(a7 - r7) > 0.1, "  FAIL: the ears lost their gap\n");
    }

    /* B. A steady foot tone and tick tone sit inside the protected bands and must not move. */
    for (m = 0; m < 2; m++) {
        rate = 48000; n = rate * 5; from = rate * 4; len = rate;
        calm_ambience(rate, n, 1);
        calm_pair(m, 1.f, rate, n);
        a7 = db_of(tone_pow(calm_b[0], from, len, 180, rate), tone_pow(calm_a[0], from, len, 180, rate));
        a6 = db_of(tone_pow(calm_b[0], from, len, 3200, rate), tone_pow(calm_a[0], from, len, 3200, rate));
        printf("calm protected mode %d: 180 Hz %+.2f dB, 3.2 kHz %+.2f dB\n", m, a7, a6);
        CALM_FAIL(fabs(a7) > 0.5 || fabs(a6) > 0.5, "  FAIL: the foot or tick band moved\n");
    }

    /* C. A footstep on top of steady background. Its foot and tick tones must come through untouched, and the trim must let go. */
    for (m = 0; m < 2; m++) {
        rate = 48000; n = rate * 6; at = rate * 9 / 2; len = rate * 40 / 1000;
        calm_ambience(rate, n, 0);
        calm_burst(calm_in[0], at, len, 180.0, 0.04, rate);
        calm_burst(calm_in[0], at, len, 3200.0, 0.04, rate);
        calm_pair(m, 1.f, rate, n);
        a7 = db_of(tone_pow(calm_b[0], at, len, 180, rate), tone_pow(calm_a[0], at, len, 180, rate));
        a6 = db_of(tone_pow(calm_b[0], at, len, 3200, rate), tone_pow(calm_a[0], at, len, 3200, rate));
        r7 = db_of(tone_pow(calm_b[0], at + rate * 8 / 100, rate / 10, 700, rate), tone_pow(calm_a[0], at + rate * 8 / 100, rate / 10, 700, rate));
        printf("calm footstep mode %d: foot tone %+.2f dB, tick tone %+.2f dB, background 80-180 ms later %+.2f dB\n", m, a7, a6, r7);
        CALM_FAIL(fabs(a7) > 0.3 || fabs(a6) > 0.3, "  FAIL: a footstep tone was turned down\n");
        CALM_FAIL(r7 < -1.0, "  FAIL: the trim did not let go after a footstep\n");
    }

    /* C2. A footstep click outside the protected bands, over the same background. It is the hardest case. */
    for (m = 0; m < 2; m++) {
        rate = 48000; n = rate * 6; at = rate * 9 / 2; len = rate * 20 / 1000;
        calm_ambience(rate, n, 0);
        calm_burst(calm_in[0], at, len, 7500.0, 0.05, rate);
        calm_pair(m, 1.f, rate, n);
        a7 = db_of(tone_pow(calm_b[0], at, len, 7500, rate), tone_pow(calm_a[0], at, len, 7500, rate));
        printf("calm click mode %d: 7.5 kHz footstep click %+.2f dB (steady background %+.2f dB)\n", m, a7, db_of(tone_pow(calm_b[0], at - rate / 4, rate / 8, 6000, rate), tone_pow(calm_a[0], at - rate / 4, rate / 8, 6000, rate)));
        CALM_FAIL(a7 < -3.0, "  FAIL: a footstep click lost more than 3 dB\n");
    }

    /* D. Speech-like level changes are not steady. */
    for (m = 0; m < 2; m++) {
        rate = 48000; n = rate * 6; from = rate * 4; len = rate * 2;
        for (i = 0; i < n; i++) {
            double t = (double)i / rate;
            calm_in[0][i] = (float)(0.03 * (1.0 + 0.8 * sin(6.283185307179586 * 4.0 * t)) * sin(6.283185307179586 * 700.0 * t));
            calm_in[1][i] = calm_in[0][i];
        }
        calm_pair(m, 1.f, rate, n);
        a7 = db_of(tone_pow(calm_b[0], from, len, 700, rate), tone_pow(calm_a[0], from, len, 700, rate));
        printf("calm speech-like mode %d: %+.2f dB\n", m, a7);
        CALM_FAIL(a7 < -0.5, "  FAIL: a modulated sound was trimmed\n");
    }

    /* E. A sound that keeps rising, like a vehicle coming closer, is not steady. */
    for (m = 0; m < 2; m++) {
        rate = 48000; n = rate * 5; from = rate * 4; len = rate;
        for (i = 0; i < n; i++) {
            double t = (double)i / rate;
            calm_in[0][i] = (float)(0.003 * pow(10.0, t * 4.0 / 20.0) * sin(6.283185307179586 * 700.0 * t));
            calm_in[1][i] = calm_in[0][i];
        }
        calm_pair(m, 1.f, rate, n);
        a7 = db_of(tone_pow(calm_b[0], from, len, 700, rate), tone_pow(calm_a[0], from, len, 700, rate));
        printf("calm rising mode %d: %+.2f dB\n", m, a7);
        CALM_FAIL(a7 < -0.5, "  FAIL: a rising sound was trimmed\n");
    }

    /* F. Noise as background. Reported, and it may never exceed the setting. */
    for (m = 0; m < 2; m++) {
        rate = 48000; n = rate * 8; from = rate * 6; len = rate * 2;
        for (i = 0; i < n; i++) { calm_in[0][i] = 0.05f * calm_noise(&seed); calm_in[1][i] = 0.05f * calm_noise(&seed); }
        calm_pair(m, 1.f, rate, n);
        a7 = db_of(energy(calm_b[0], from, len), energy(calm_a[0], from, len));
        printf("calm noise mode %d: %+.2f dB\n", m, a7);
        CALM_FAIL(a7 < -9.5, "  FAIL: noise was trimmed by more than the setting\n");
    }

    /* G. Silence stays silence. */
    for (m = 0; m < 2; m++) {
        rate = 48000; n = rate;
        memset(calm_in, 0, sizeof calm_in);
        calm_pair(m, 1.f, rate, n);
        for (i = 0; i < n; i++) if (calm_b[0][i] != 0.f || calm_b[1][i] != 0.f) { bad = 1; printf("  FAIL: silence changed\n"); break; }
    }

    /* H. The setting itself. */
    {
        Effect* e = VSTPluginMain(0);
        memset(name2, 0, sizeof name2);
        dispatch(e, OP_PARAM_NAME, 2, 0, name2, 0.f);
        CALM_FAIL(strcmp(name2, "Calm") != 0 || e->params != 3 || get_param(e, 2) != 0.f, "  FAIL: the Calm parameter is not there\n");
        e->set_param(e, 2, 2.f);  CALM_FAIL(get_param(e, 2) != 1.f, "  FAIL: Calm above 1 must clamp to 1\n");
        e->set_param(e, 2, -1.f); CALM_FAIL(get_param(e, 2) != 0.f, "  FAIL: Calm below 0 must clamp to 0\n");
        dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    }
    printf(bad ? "calm: FAILED\n" : "calm: all checks passed\n");
    return bad;
}

static int read_full(void* dst, size_t bytes) {
    return fread(dst, 1, bytes, stdin) == bytes;
}

/* Same samples in, same samples out, so the C# preview can be compared with this file.
   Header: TNP1, channels, frames, mode (0 classic, 1 nova), rate, hit. Then planar float32. */
static int pipe_mode(void) {
    char magic[4];
    i32 channels = 0, frames = 0, mode = 0, rate = 0, ch;
    float hit = 1.f;
    float* block = 0;
    float* inputs[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
    float* outputs[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
    Effect* effect;
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif
    if (!read_full(magic, 4) || memcmp(magic, "TNP1", 4) != 0) return 2;
    if (!read_full(&channels, 4) || !read_full(&frames, 4) || !read_full(&mode, 4) || !read_full(&rate, 4) || !read_full(&hit, 4)) return 2;
    if (channels < 1 || channels > 8 || frames < 1 || frames > 96000 || (mode != 0 && mode != 1) || rate < 1000) return 2;
    block = (float*)malloc((size_t)channels * (size_t)frames * sizeof(float));
    if (!block || !read_full(block, (size_t)channels * (size_t)frames * sizeof(float))) { free(block); return 2; }
    effect = VSTPluginMain(0);
    if (!effect) { free(block); return 2; }
    dispatch(effect, OP_RATE, 0, 0, 0, (float)rate);
    if (mode == 1) {
        effect->set_param(effect, 0, 1.f);
        effect->set_param(effect, 1, hit);
    }
    for (ch = 0; ch < channels; ch++) {
        inputs[ch] = block + (size_t)ch * (size_t)frames;
        outputs[ch] = inputs[ch];
    }
    replace(effect, inputs, outputs, frames);
    for (ch = 0; ch < channels; ch++) {
        if (fwrite(outputs[ch], sizeof(float), (size_t)frames, stdout) != (size_t)frames) {
            dispatch(effect, OP_CLOSE, 0, 0, 0, 0.f);
            free(block);
            return 2;
        }
    }
    dispatch(effect, OP_CLOSE, 0, 0, 0, 0.f);
    free(block);
    return 0;
}

int main(int argc, char** argv) {
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
    if (argc > 1 && strcmp(argv[1], "pipe") == 0) return pipe_mode();
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
    if (check_ears()) return 1;
    if (check_nova()) return 1;
    return check_calm();
}
#endif
