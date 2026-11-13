/* Build on Windows, from this folder:
   x86_64-w64-mingw32-gcc -shared -O2 -o JesusKpekpesTune.dll tune.c -lm
   The text file is the equalizer. Classic mode is the default. It raises the foot band, and the step tick
   (shoe grit at 3.4 kHz) only while it is a hit, so a steady sound in that band is not turned up.
   Both are raised by the same amount in every ear, so the loudness difference between the ears is kept.
   The lift only ever adds level: it eases back to nothing as the foot band gets loud and is never below the input.
   While a gun is loud in any ear the tick lift steps aside, so a shot's crack is not made sharper.
   A gun is turned down in its own band, on the ear that fired. The other ear is never touched.
   Nova mode is off until the host sets the Nova parameter. In that mode a step is lifted only while it is a hit.
   Loud is a last stage, off until the host sets it. It raises a quiet stretch and does not delay the sound. */
#include <math.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "step_model.h"

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
    float foot_env, gun_env, shot_env, shot_slow, shot_peak;
    float foot_attack, foot_release, gun_attack, gun_release;
    float weight_fast, weight_slow, tick_fast, tick_slow;
    float fast_a, fast_r, weight_r, slow_a, slow_r;
} Lane;

/* Boost step. A small learned detector listens for footsteps and, only while one is playing, lifts the sound.
   Its features and its weights come from ai/features.py and ai/train.py. It is off until the host sets Boost. */
enum { STEP_BANDS = 12, STEP_LAG = 3 };
typedef struct {
    Stage band[STEP_BANDS];
    float acc[STEP_BANDS], slow[STEP_BANDS], hist[STEP_LAG][STEP_BANDS];
    float level[STEP_BANDS];
    float hop_peak, last_peak;
    float slow_rate, atk, rel, gain, prob;
    int hop, count, pos;
} Step;

/* Cinema loudness. Off until the host sets Loud. The detector is high-passed so bass rumble
   does not pull the gain down. The sound itself is not delayed and is not high-passed. */
typedef struct {
    float z[8];
    float hp, det, det_up, det_down, gain_db, g_up, g_down, lim, lim_r, target, amount;
    int hold, hold_len;
} Loud;

typedef struct {
    Lane lane[8];
    Step step;
    Loud loud;
    float rate;
    float hit;
    float power;
    int mode;
    int boost;
    int loud_on;
    float own;
    int own_hold;
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
    set_band(&lane->tick, 3400.f, 1.4f, rate);
    lane->fast_a = 1.f - expf(-1.f / (rate * 0.0025f));
    lane->fast_r = 1.f - expf(-1.f / (rate * 0.040f));
    lane->slow_a = 1.f - expf(-1.f / (rate * 0.080f));
    lane->slow_r = 1.f - expf(-1.f / (rate * 0.400f));
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
    float gun_band, foot_band, tick_band, other;
} Split;

static void follow(float* env, float level, float attack, float release);

static void split(Lane* lane, float x, Split* out) {
    float gun_band = tick(&lane->gun, x);
    float foot_band = tick(&lane->foot, x);
    float tick_band = tick(&lane->tick, x);
    float foot_level = fabsf(foot_band);
    float gun_level = fabsf(gun_band);
    lane->foot_env += (foot_level - lane->foot_env) * (foot_level > lane->foot_env ? lane->foot_attack : lane->foot_release);
    lane->gun_env += (gun_level - lane->gun_env) * (gun_level > lane->gun_env ? lane->gun_attack : lane->gun_release);
    if (lane->foot_env < 1e-20f) lane->foot_env = 0.f;
    if (lane->gun_env < 1e-20f) lane->gun_env = 0.f;
    follow(&lane->tick_fast, fabsf(tick_band), lane->fast_a, lane->fast_r);
    follow(&lane->tick_slow, fabsf(tick_band), lane->slow_a, lane->slow_r);
    out->gun_band = gun_band;
    out->foot_band = foot_band;
    out->tick_band = tick_band;
    out->other = x - gun_band - foot_band - tick_band;
}

/* Linear gain for the foot band, never below 1. Up to +10 dB for a quiet foot, easing to 1 as the band gets loud. */
static float lift(float env) {
    float db = 10.f - reduction(env, -20.f, 1.f);
    return db > 0.f ? powf(10.f, db / 20.f) : 1.f;
}

static float finish(Lane* lane, const Split* part, float foot_gain, float tick_gain) {
    float gun_gain = powf(10.f, -reduction(lane->gun_env, -16.f, 2.f / 3.f) / 20.f);
    float y = part->other + part->foot_band * foot_gain + part->tick_band * tick_gain + part->gun_band * gun_gain;
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
    lane->weight_r = 1.f - expf(-1.f / (rate * 0.080f));
    lane->slow_a = 1.f - expf(-1.f / (rate * 0.080f));
    lane->slow_r = 1.f - expf(-1.f / (rate * 0.400f));
    lane->gun_attack = 1.f - expf(-1.f / (rate * 0.003f));
    lane->gun_release = 1.f - expf(-1.f / (rate * 0.122f));
}

/* One gain for every ear. A steady band (ratio near 1) stays at 1.
   A hit is lifted even when it is not tiny, up to max_db. The floor keeps a normal foot from being ignored. */
static float transient_db(float fast, float ratio, float max_db) {
    float novelty, edge, level, quiet, db;
    if (max_db <= 0.05f || fast < 1e-6f || ratio < 1.15f) return 0.f;
    novelty = 20.f * log10f(ratio);
    if (novelty > 18.f) novelty = 18.f;
    edge = novelty / 12.f;
    if (edge > 1.f) edge = 1.f;
    level = 20.f * log10f(fast + 1e-8f);
    quiet = (-2.f - level) / 22.f;
    if (quiet < 0.65f) quiet = 0.65f;
    if (quiet > 1.f) quiet = 1.f;
    db = max_db * quiet * edge;
    if (db <= 0.f) return 0.f;
    return db;
}

static float transient_gain(float fast, float ratio, float max_db) {
    float db = transient_db(fast, ratio, max_db);
    if (db <= 0.f) return 1.f;
    return powf(10.f, db / 20.f);
}

/* Classic step tick. Up to +9 dB while it is a hit. Fades out as the loudest gun trim reaches 4 dB. */
static float classic_tick_gain(float fast, float ratio, float gun_cut_db) {
    float guard = 1.f - gun_cut_db / 4.f;
    float db;
    if (guard <= 0.f) return 1.f;
    if (guard > 1.f) guard = 1.f;
    db = transient_db(fast, ratio, 9.f) * guard;
    if (db <= 0.f) return 1.f;
    return powf(10.f, db / 20.f);
}

static void consider(float fast, float slow, float* best_fast, float* best_ratio) {
    float ratio = fast / (slow + 1e-5f);
    if (fast > *best_fast) *best_fast = fast;
    if (ratio > *best_ratio) *best_ratio = ratio;
}

static int who_fired(float left, float right, float centre, int ears, float crack, int gunish, int* hold) {
    float side, hi, bal;
    /* A footstep is not a gun, and a steady tone is not a shot. Drop never reaches this. */
    if (crack < 1.8f || !gunish) {
        *hold = 0;
        return 0;
    }
    if (ears < 2) right = left;
    if (ears < 3) centre = 0.f;
    side = left > right ? left : right;
    hi = centre > side ? centre : side;
    bal = (left < right ? left : right) / (side + 1e-8f);
    /* Loud and in both ears: your gun, or a teammate close enough to be in both ears. */
    if (hi > 0.08f && bal > 0.55f) *hold = 1;
    /* Keep that decision until the crack ends, so the tail is not turned back up as an enemy. */
    if (*hold && bal > 0.55f) return 1;
    /* Quieter, or shoved into one ear: an enemy. */
    if (hi > 0.004f && hi <= 0.08f) return 2;
    if (hi > 0.02f && bal < 0.45f) return 2;
    return 0;
}

static void nova_replace(Effect* effect, float** inputs, float** outputs, i32 frames) {
    Bank* bank = effect->object;
    float max_weight = 12.f * bank->hit;
    float max_tick = 16.f * bank->hit;
    float peak_a = 1.f - expf(-1.f / (bank->rate * 0.0004f));
    float peak_r = 1.f - expf(-1.f / (bank->rate * 0.040f));
    float duck_down = 1.f - expf(-1.f / (bank->rate * 0.0015f));
    float duck_up = 1.f - expf(-1.f / (bank->rate * 0.012f));
    i32 i, ch, nch = effect->inputs;
    if (nch > 8) nch = 8;
    for (i = 0; i < frames; i++) {
        float best_w_fast = 0.f, best_w_ratio = 1.f, best_t_fast = 0.f, best_t_ratio = 1.f;
        float weight_gain, tick_gain;
        float weight_b[8] = { 0 }, tick_b[8] = { 0 }, gun_b[8] = { 0 }, other_b[8] = { 0 };
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
            follow(&lane->weight_fast, fabsf(w), lane->fast_a, lane->weight_r);
            follow(&lane->weight_slow, fabsf(w), lane->slow_a, lane->slow_r);
            follow(&lane->tick_fast, fabsf(t), lane->fast_a, lane->fast_r);
            follow(&lane->tick_slow, fabsf(t), lane->slow_a, lane->slow_r);
            follow(&lane->gun_env, fabsf(g), lane->gun_attack, lane->gun_release);
            follow(&lane->shot_env, fabsf(x), lane->gun_attack, lane->gun_release);
            follow(&lane->shot_slow, fabsf(x), lane->slow_a, lane->slow_r);
            follow(&lane->shot_peak, fabsf(x), peak_a, peak_r);
            consider(lane->weight_fast, lane->weight_slow, &best_w_fast, &best_w_ratio);
            consider(lane->tick_fast, lane->tick_slow, &best_t_fast, &best_t_ratio);
        }
        weight_gain = transient_gain(best_w_fast, best_w_ratio, max_weight);
        tick_gain = transient_gain(best_t_fast, best_t_ratio, max_tick);
        {
            float step = weight_gain > tick_gain ? weight_gain : tick_gain;
            float crack = 1.f, gmax = 0.f, fmax = 0.f, tmax = 0.f;
            int who, ear, gunish;
            float body, target, coeff;
            for (ear = 0; ear < nch && ear < 8; ear++) {
                Lane* lane = &bank->lane[ear];
                if (ear < 3) {
                    float c = lane->shot_env / (lane->shot_slow + 1e-5f);
                    if (c > crack) crack = c;
                }
                if (lane->gun_env > gmax) gmax = lane->gun_env;
                if (lane->weight_fast > fmax) fmax = lane->weight_fast;
                if (lane->tick_fast > tmax) tmax = lane->tick_fast;
            }
            /* Gun band louder than the foot and the tick: this crack is a shot, not a step. */
            gunish = gmax > fmax * 1.35f && gmax > tmax * 1.35f && gmax > 0.0015f;
            if (gunish && crack >= 1.8f) {
                weight_gain = 1.f;
                tick_gain = 1.f;
                step = 1.f;
            }
            who = who_fired(nch > 0 ? bank->lane[0].shot_peak : 0.f,
                nch > 1 ? bank->lane[1].shot_peak : 0.f,
                nch > 2 ? bank->lane[2].shot_peak : 0.f, nch, crack, gunish, &bank->own_hold);
            target = who == 1 ? 0.10f : 1.f;
            coeff = target < bank->own ? duck_down : duck_up;
            bank->own += (target - bank->own) * coeff;
            body = bank->own;
            for (ch = 0; ch < nch; ch++) {
                Lane* lane;
                float gun_gain, y, mag;
                if (!inputs[ch] || !outputs[ch]) continue;
                lane = &bank->lane[ch];
                gun_gain = 1.f;
                if (who == 1) gun_gain = body;
                else if (who == 2) gun_gain = 2.f;
                else if ((nch <= 2 || ch < 3) && !(step >= 1.15f && !gunish))
                    gun_gain = powf(10.f, -reduction(lane->gun_env, -16.f, 2.f / 3.f) / 20.f);
                y = (other_b[ch] + weight_b[ch] * weight_gain + tick_b[ch] * tick_gain) * body + gun_b[ch] * gun_gain;
                mag = fabsf(y);
                if (mag > 0.8912509f)
                    y = copysignf(0.8912509f + tanhf((mag - 0.8912509f) / 0.05f) * 0.05f, y);
                outputs[ch][i] = y;
            }
        }
    }
}

static void step_arm(Step* st, float rate) {
    int i, j;
    if (rate < 1000.f) rate = 48000.f;
    memset(st, 0, sizeof *st);
    for (i = 0; i < STEP_BANDS; i++) {
        set_band(&st->band[i], 100.f * powf(76.f, (float)i / 11.f), 2.4f, rate);
        st->slow[i] = -100.f;
        for (j = 0; j < STEP_LAG; j++) st->hist[j][i] = -100.f;
    }
    st->hop = (int)(rate * 0.01f + 0.5f);
    st->slow_rate = 1.f - expf(-10.f / 400.f);
    st->atk = 1.f - expf(-1.f / (rate * 0.004f));
    st->rel = 1.f - expf(-1.f / (rate * 0.150f));
    st->gain = 1.f;
}

/* One mono sample in. Every 10 ms the probability that a footstep is playing is refreshed. */
static void step_feed(Step* st, float x) {
    int i;
    float ax = fabsf(x);
    if (ax > st->hop_peak) st->hop_peak = ax;
    for (i = 0; i < STEP_BANDS; i++) {
        float y = tick(&st->band[i], x);
        st->acc[i] += y * y;
    }
    if (++st->count < st->hop) return;
    {
        float f[STEP_IN], h[STEP_HID], z;
        int j, k;
        for (i = 0; i < STEP_BANDS; i++) {
            float level = 10.f * log10f(st->acc[i] / (float)st->hop + 1e-10f);
            st->slow[i] += st->slow_rate * (level - st->slow[i]);
            f[i] = level;
            f[STEP_BANDS + i] = level - st->slow[i];
            f[2 * STEP_BANDS + i] = level - st->hist[st->pos][i];
            st->hist[st->pos][i] = level;
            st->level[i] = level;
            st->acc[i] = 0.f;
        }
        st->last_peak = st->hop_peak;
        st->hop_peak = 0.f;
        st->pos = (st->pos + 1) % STEP_LAG;
        st->count = 0;
        z = step_b2;
        for (j = 0; j < STEP_HID; j++) {
            float sum = step_b1[j];
            for (k = 0; k < STEP_IN; k++) sum += step_w1[j * STEP_IN + k] * f[k];
            h[j] = sum > 0.f ? sum : 0.f;
            z += step_w2[j] * h[j];
        }
        st->prob = 1.f / (1.f + expf(-z));
    }
}

/* A footstep is quiet. A gun sits around 1 to 2 kHz and must not be lifted. */
static int step_blocked(const Step* st) {
    float gun = st->level[6] + st->level[7] + st->level[8];
    float foot = st->level[1] + st->level[2] + st->level[9];
    if (st->last_peak > 0.18f && gun > foot + 3.f) return 1;
    if (st->last_peak > 0.08f && gun > foot + 6.f) return 1;
    return 0;
}

/* The most a step is lifted: up to +16 dB at full power.
   Nothing happens below 62% sure, full lift from 90%. A gunshot is never lifted. */
static float step_target(const Step* st, float power, float guard) {
    float t;
    if (step_blocked(st)) return 1.f;
    t = (st->prob - 0.62f) / 0.28f;
    if (t <= 0.f || power <= 0.f) return 1.f;
    if (t > 1.f) t = 1.f;
    return powf(10.f, 16.f * power * t * guard / 20.f);
}

static void loud_arm(Loud* ld, float rate, float amount) {
    if (rate < 1000.f) rate = 48000.f;
    if (amount < 0.f) amount = 0.f;
    if (amount > 1.f) amount = 1.f;
    memset(ld, 0, sizeof *ld);
    ld->hp = expf(-2.f * 3.14159274f * 70.f / rate);
    ld->det_up = 1.f - expf(-1.f / (rate * 0.180f));
    ld->det_down = 1.f - expf(-1.f / (rate * 0.400f));
    ld->g_up = 1.f - expf(-1.f / (rate * 0.550f));
    ld->g_down = 1.f - expf(-1.f / (rate * 0.040f));
    ld->lim_r = 1.f - expf(-1.f / (rate * 0.080f));
    ld->lim = 1.f;
    ld->hold_len = (int)(rate * 0.045f);
    if (ld->hold_len < 1) ld->hold_len = 1;
    ld->amount = amount;
    /* Balanced is about -20 dB, Loud is -18. Safe never calls this. */
    ld->target = -24.f + 6.f * amount;
}

/* One shared gain for every ear. A quiet stretch rises slowly. A hot stretch falls in about 40 ms.
   A real shot (loud, and far above the bed) drops the lift on that sample so the shot is not raised.
   A footstep is quieter than that test, so it keeps the lift. No lookahead. */
static void loud_run(Loud* ld, float** out, i32 nch, i32 frames) {
    const float ceiling = 0.8912509f;
    i32 i, ch;
    if (!ld || !out || frames < 1 || nch < 1) return;
    if (nch > 8) nch = 8;
    for (i = 0; i < frames; i++) {
        float p = 0.f, slow, level, want, gate, g, pk, need;
        int used = 0;
        for (ch = 0; ch < nch; ch++) {
            float x, z, h;
            if (!out[ch]) continue;
            x = out[ch][i];
            z = ld->hp * ld->z[ch] + (1.f - ld->hp) * x;
            if (fabsf(z) < 1e-20f) z = 0.f;
            ld->z[ch] = z;
            h = x - z;
            p += h * h;
            used++;
        }
        if (used > 1) p /= (float)used;
        slow = ld->det;
        /* A shot is a jump well above an established bed, not the start of a loud tone.
           The bed keeps moving, capped, so a shot cannot drag it by more than about 9 dB. */
        if (slow > 1e-12f && p > slow * 6.f && p > 0.01f) {
            ld->hold = ld->hold_len;
            if (ld->gain_db > 0.f) ld->gain_db = 0.f;
        }
        {
            float tracked = p;
            float coef;
            if (slow > 1e-12f && tracked > slow * 8.f) tracked = slow * 8.f;
            coef = tracked > slow ? ld->det_up : ld->det_down;
            slow += (tracked - slow) * coef;
            if (slow < 1e-20f) slow = 0.f;
            ld->det = slow;
        }
        level = 10.f * log10f(slow + 1e-20f);
        want = (ld->target - level) * 0.72f;
        if (want < -10.f) want = -10.f;
        if (want > 12.f) want = 12.f;
        gate = (level + 66.f) / 10.f;
        if (gate < 0.f) gate = 0.f;
        else if (gate > 1.f) gate = 1.f;
        gate = gate * gate * (3.f - 2.f * gate);
        want *= gate;
        if (ld->hold > 0) {
            want = ld->gain_db < 0.f ? ld->gain_db : 0.f;
            ld->hold--;
        }
        ld->gain_db += (want - ld->gain_db) * (want > ld->gain_db ? ld->g_up : ld->g_down);
        g = powf(10.f, ld->gain_db / 20.f);
        pk = 0.f;
        for (ch = 0; ch < nch; ch++) {
            float y, ay;
            if (!out[ch]) continue;
            y = out[ch][i] * g;
            out[ch][i] = y;
            ay = fabsf(y);
            if (ay > pk) pk = ay;
        }
        need = pk > ceiling ? ceiling / pk : 1.f;
        if (need < ld->lim) ld->lim = need;
        else ld->lim += (1.f - ld->lim) * ld->lim_r;
        for (ch = 0; ch < nch; ch++) {
            float y, mag;
            if (!out[ch]) continue;
            y = out[ch][i] * ld->lim;
            mag = fabsf(y);
            if (mag > ceiling) y = copysignf(ceiling + tanhf((mag - ceiling) / 0.05f) * 0.05f, y);
            out[ch][i] = y;
        }
    }
}

static void replace_core(Effect* effect, float** inputs, float** outputs, i32 frames);

static void replace(Effect* effect, float** inputs, float** outputs, i32 frames) {
    Bank* bank = effect ? effect->object : 0;
    enum { chunk = 512 };
    float gains[chunk];
    i32 off, i, ch, nch;
    float clip = 0.708f;
    if (!bank || !bank->boost) {
        replace_core(effect, inputs, outputs, frames);
        if (bank && bank->loud_on && outputs && frames > 0)
            loud_run(&bank->loud, outputs, effect->inputs > 8 ? 8 : effect->inputs, frames);
        return;
    }
    if (frames < 1 || !inputs || !outputs) return;
    if (bank->loud_on) clip = 0.8912509f;
    nch = effect->inputs > 8 ? 8 : effect->inputs;
    for (off = 0; off < frames; off += chunk) {
        float* in[8] = { 0 };
        float* out[8] = { 0 };
        i32 n = frames - off < chunk ? frames - off : chunk;
        float guard = 1.f, cut = 0.f;
        for (ch = 0; ch < nch; ch++) {
            float c = reduction(bank->lane[ch].gun_env, -16.f, 2.f / 3.f);
            if (c > cut) cut = c;
            if (inputs[ch]) in[ch] = inputs[ch] + off;
            if (outputs[ch]) out[ch] = outputs[ch] + off;
        }
        /* While a shot is loud the lift steps aside, as the step tick does, so a shot is never made sharper. */
        guard = 1.f - cut / 4.f;
        if (guard < 0.f) guard = 0.f;
        for (i = 0; i < n; i++) {
            float sum = 0.f, target, g = bank->step.gain;
            int used = 0;
            for (ch = 0; ch < nch; ch++)
                if (in[ch]) { sum += in[ch][i]; used++; }
            step_feed(&bank->step, used ? sum / (float)used : 0.f);
            target = step_target(&bank->step, bank->power, guard);
            g += (target - g) * (target > g ? bank->step.atk : bank->step.rel);
            bank->step.gain = g;
            gains[i] = g;
        }
        replace_core(effect, in, out, n);
        for (ch = 0; ch < nch; ch++) {
            if (!out[ch]) continue;
            for (i = 0; i < n; i++) {
                float y, mag;
                if (gains[i] < 1.001f) continue;
                y = out[ch][i] * gains[i];
                mag = fabsf(y);
                /* 0.708 leaves room for the old Level preamp. Cinema holds its own ceiling. */
                if (mag > clip) y = copysignf(clip + tanhf((mag - clip) / 0.05f) * 0.05f, y);
                out[ch][i] = y;
            }
        }
    }
    if (bank->loud_on) loud_run(&bank->loud, outputs, nch, frames);
}

static void replace_core(Effect* effect, float** inputs, float** outputs, i32 frames) {
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
        float strongest = 0.f, best_t_fast = 0.f, best_t_ratio = 1.f, gun_cut = 0.f;
        float foot_gain, tick_gain;
        for (ch = 0; ch < effect->inputs && ch < 8; ch++) {
            Lane* lane = &bank->lane[ch];
            float cut;
            if (!inputs[ch] || !outputs[ch]) continue;
            split(lane, inputs[ch][i], &part[ch]);
            if (lane->foot_env > strongest) strongest = lane->foot_env;
            consider(lane->tick_fast, lane->tick_slow, &best_t_fast, &best_t_ratio);
            cut = reduction(lane->gun_env, -16.f, 2.f / 3.f);
            if (cut > gun_cut) gun_cut = cut;
        }
        foot_gain = lift(strongest);
        tick_gain = classic_tick_gain(best_t_fast, best_t_ratio, gun_cut);
        for (ch = 0; ch < effect->inputs && ch < 8; ch++) {
            if (!inputs[ch] || !outputs[ch]) continue;
            outputs[ch][i] = finish(&bank->lane[ch], &part[ch], foot_gain, tick_gain);
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
        bank->own = 1.f;
        bank->own_hold = 0;
        for (ch = 0; ch < 8; ch++) {
            if (mode) arm_nova(&bank->lane[ch], bank->rate);
            else arm(&bank->lane[ch], bank->rate);
        }
        return;
    }
    if (index == 1) {
        if (value < 0.f) value = 0.f;
        if (value > 1.f) value = 1.f;
        bank->hit = value;
    }
    if (index == 2) {
        int on = value >= 0.5f ? 1 : 0;
        if (on && !bank->boost) step_arm(&bank->step, bank->rate);
        bank->boost = on;
    }
    if (index == 3) {
        if (value < 0.f) value = 0.f;
        if (value > 1.f) value = 1.f;
        bank->power = value;
    }
    if (index == 4) {
        if (value < 0.f) value = 0.f;
        if (value > 1.f) value = 1.f;
        if (value < 0.001f) {
            bank->loud_on = 0;
            bank->loud.amount = 0.f;
            return;
        }
        if (!bank->loud_on || bank->loud.amount != value)
            loud_arm(&bank->loud, bank->rate, value);
        bank->loud_on = 1;
    }
}

static float get_param(Effect* effect, i32 index) {
    Bank* bank = effect ? effect->object : 0;
    if (!bank) return 0.f;
    if (index == 0) return bank->mode ? 1.f : 0.f;
    if (index == 1) return bank->hit;
    if (index == 2) return bank->boost ? 1.f : 0.f;
    if (index == 3) return bank->power;
    if (index == 4) return bank->loud_on ? bank->loud.amount : 0.f;
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
        step_arm(&bank->step, opt);
        if (bank->loud_on) loud_arm(&bank->loud, opt, bank->loud.amount);
        return 1;
    }
    if (op == OP_PARAM_NAME && ptr) {
        const char* name = index == 0 ? "Nova" : index == 1 ? "Hit" : index == 2 ? "Boost" : index == 3 ? "Power" : index == 4 ? "Loud" : "";
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
    bank->power = 0.75f;
    bank->boost = 0;
    bank->mode = 0;
    bank->own = 1.f;
    step_arm(&bank->step, 48000.f);
    effect->magic = 0x56737450;
    effect->dispatch = dispatch;
    effect->older = (void (*)(Effect*, float**, float**, i32))replace;
    effect->set_param = set_param;
    effect->get_param = get_param;
    effect->inputs = 8;
    effect->outputs = 8;
    effect->flags = 1 << 4;
    effect->params = 5;
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

/* Classic step tick. A steady 3.4 kHz tone must not be lifted, a short quiet hit must be,
   and the same hit under a loud gun must not get the tick lift. */
static int check_classic_tick(void) {
    enum { sr = 48000, n = 48000 };
    static float a[n], b[n], dry[n], gl[n], gr[n], gdry[n];
    float* in[8] = { a, b };
    float* out[8] = { a, b };
    int i, bad = 0, at = n / 3, len = (int)(sr * 0.032f);
    double steady, hit, guarded;
    Effect* e = VSTPluginMain(0);
    for (i = 0; i < n; i++) a[i] = sinf(2.f * 3.14159265f * 3400.f * i / sr) * 0.03f;
    memcpy(dry, a, sizeof dry);
    memset(b, 0, sizeof b);
    replace(e, in, out, n);
    steady = energy(a, n - n / 5, n / 5) / energy(dry, n - n / 5, n / 5);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);

    memset(a, 0, sizeof a);
    memset(b, 0, sizeof b);
    for (i = 0; i < len; i++)
        a[at + i] = sinf(2.f * 3.14159265f * 3400.f * (at + i) / sr) * 0.01f * sinf((float)i / (float)len * 3.14159265f);
    memcpy(dry, a, sizeof dry);
    memcpy(gl, a, sizeof gl);
    e = VSTPluginMain(0);
    replace(e, in, out, n);
    hit = energy(a, at, len) / energy(dry, at, len);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);

    for (i = 0; i < n; i++) gr[i] = sinf(2.f * 3.14159265f * 1400.f * i / sr) * 0.9f;
    memcpy(gdry, gl, sizeof gdry);
    e = VSTPluginMain(0);
    in[0] = gl; in[1] = gr; out[0] = gl; out[1] = gr;
    replace(e, in, out, n);
    guarded = energy(gl, at, len) / energy(gdry, at, len);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    printf("classic tick steady %.3f hit %.3f under gun %.3f\n", steady, hit, guarded);
    if (!(steady > 0.9 && steady < 1.1)) bad = 1;
    if (!(hit > 2.0)) bad = 1;
    if (!(guarded < hit * 0.6)) bad = 1;
    return bad;
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
    if (strcmp(name0, "Nova") != 0 || strcmp(name1, "Hit") != 0 || e->params != 5) bad = 1;
    dispatch(e, OP_PARAM_NAME, 2, 0, name0, 0.f);
    dispatch(e, OP_PARAM_NAME, 3, 0, name1, 0.f);
    if (strcmp(name0, "Boost") != 0 || strcmp(name1, "Power") != 0 || get_param(e, 2) != 0.f) bad = 1;
    dispatch(e, OP_PARAM_NAME, 4, 0, name0, 0.f);
    if (strcmp(name0, "Loud") != 0 || get_param(e, 4) != 0.f) bad = 1;
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
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    {
        static float ol[n], orr[n], el[n], er[n];
        int gun_at = n / 5, gun_len = (int)(sr * 0.04f), win = (int)(sr * 0.08f);
        double own_dry = 0, enemy_dry = 0, own_out, enemy_out;
        memset(ol, 0, sizeof ol);
        memset(orr, 0, sizeof orr);
        memset(el, 0, sizeof el);
        memset(er, 0, sizeof er);
        for (i = 0; i < gun_len; i++) {
            float env = sinf((float)i / (float)gun_len * 3.14159265f);
            float s = sinf(2.f * 3.14159265f * 1400.f * (gun_at + i) / sr) * env;
            ol[gun_at + i] = orr[gun_at + i] = s * 0.55f;
            el[gun_at + i] = s * 0.045f;
            own_dry += (double)ol[gun_at + i] * ol[gun_at + i];
            enemy_dry += (double)el[gun_at + i] * el[gun_at + i];
        }
        e = VSTPluginMain(0);
        e->set_param(e, 0, 1.f);
        e->set_param(e, 1, 1.f);
        in[0] = ol; in[1] = orr; out[0] = ol; out[1] = orr;
        replace(e, in, out, n);
        own_out = energy(ol, gun_at, win);
        dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
        e = VSTPluginMain(0);
        e->set_param(e, 0, 1.f);
        e->set_param(e, 1, 1.f);
        in[0] = el; in[1] = er; out[0] = el; out[1] = er;
        replace(e, in, out, n);
        enemy_out = energy(el, gun_at, win);
        dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
        printf("nova own shot %.3f enemy shot %.3f\n", own_out / own_dry, enemy_out / (enemy_dry + 1e-20));
        if (!(own_out < own_dry * 0.08)) bad = 1;
        if (!(enemy_out > enemy_dry * 0.9)) bad = 1;
        if (!(enemy_out > own_out)) bad = 1;
    }
    {
        static float z[64], zr[64];
        float* zin[8] = { z, zr };
        float* zout[8] = { z, zr };
        e = VSTPluginMain(0);
        e->set_param(e, 0, 1.f);
        replace(e, zin, zout, 64);
        for (i = 0; i < 64; i++) if (z[i] != 0.f || zr[i] != 0.f) bad = 1;
    }
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    return bad;
}

static int read_full(void* dst, size_t bytes) {
    return fread(dst, 1, bytes, stdin) == bytes;
}

/* Same samples in, same samples out, so the C# preview can be compared with this file.
   Header: TNP1, channels, frames, mode (0 classic, 1 nova), rate, hit, loud. Then planar float32.
   loud under 0.001 leaves the samples exactly as they were without the cinema stage. */
static int pipe_mode(void) {
    char magic[4];
    i32 channels = 0, frames = 0, mode = 0, rate = 0, ch;
    float hit = 1.f, loud = 0.f;
    float* block = 0;
    float* inputs[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
    float* outputs[8] = { 0, 0, 0, 0, 0, 0, 0, 0 };
    Effect* effect;
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif
    if (!read_full(magic, 4) || memcmp(magic, "TNP1", 4) != 0) return 2;
    if (!read_full(&channels, 4) || !read_full(&frames, 4) || !read_full(&mode, 4) || !read_full(&rate, 4) || !read_full(&hit, 4) || !read_full(&loud, 4)) return 2;
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
    if (loud >= 0.001f) effect->set_param(effect, 4, loud);
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

/* The detector must agree with ai/features.py. ai/parity.bin holds two seconds of audio and Python's answer for it. */
static int check_step(void) {
    FILE* f = fopen("ai/parity.bin", "rb");
    i32 frames = 0, hops = 0, i, k = 0, worst_at = 0;
    float* audio = 0;
    float* want = 0;
    float worst = 0.f;
    Step* st = (Step*)calloc(1, sizeof(Step));
    float *in[8] = { 0 }, *out[8] = { 0 };
    double on_energy = 0, off_energy = 0, peak = 0;
    int rc = 1;
    if (!f) { printf("step: ai/parity.bin not found. Run the check from the repository folder.\n"); free(st); return 1; }
    if (fread(&frames, 4, 1, f) != 1 || frames < 48000 || frames > 480000) { fclose(f); free(st); return 1; }
    audio = (float*)malloc((size_t)frames * 4);
    if (!audio || fread(audio, 4, (size_t)frames, f) != (size_t)frames || fread(&hops, 4, 1, f) != 1 || hops < 10 || hops > 5000) goto done;
    want = (float*)malloc((size_t)hops * 4);
    if (!want || fread(want, 4, (size_t)hops, f) != (size_t)hops) goto done;
    step_arm(st, 48000.f);
    for (i = 0; i < frames; i++) {
        step_feed(st, audio[i]);
        if (st->count == 0 && k < hops) {
            float d = fabsf(st->prob - want[k]);
            if (d > worst) { worst = d; worst_at = k; }
            k++;
        }
    }
    printf("step: %d frames compared, worst difference from Python %.5f (frame %d)\n", k, worst, worst_at);
    if (k != hops || worst > 0.02f) goto done;
    /* Boost off must not change a sample. Boost at zero power must not either. Boost on must lift steps and stay under the ceiling. */
    {
        float* a = (float*)malloc((size_t)frames * 4);
        float* b = (float*)malloc((size_t)frames * 4);
        float* c = (float*)malloc((size_t)frames * 4);
        int run;
        for (run = 0; run < 3; run++) {
            float* dst = run == 0 ? a : run == 1 ? b : c;
            Effect* effect = VSTPluginMain(0);
            memcpy(dst, audio, (size_t)frames * 4);
            in[0] = out[0] = dst;
            if (run > 0) {
                effect->set_param(effect, 2, 1.f);
                effect->set_param(effect, 3, run == 1 ? 0.f : 1.f);
            }
            replace(effect, in, out, frames);
            dispatch(effect, OP_CLOSE, 0, 0, 0, 0.f);
        }
        for (i = 0; i < frames; i++) if (a[i] != b[i]) { printf("step: boost at zero power changed sample %d\n", i); goto inner_done; }
        for (i = 0; i < frames; i++) {
            if (fabsf(c[i]) > peak) peak = fabsf(c[i]);
            if (want[(i / 480) < hops ? i / 480 : hops - 1] > 0.9f) { on_energy += (double)c[i] * c[i]; off_energy += (double)a[i] * a[i]; }
        }
        printf("step: boosted peak %.3f, energy where a step is sure %.1fx the unboosted\n", peak, off_energy > 0 ? on_energy / off_energy : 0.0);
        if (peak <= 0.80 && off_energy > 0 && on_energy / off_energy > 4.0) rc = 0;
inner_done:
        free(a); free(b); free(c);
    }
done:
    free(audio); free(want); free(st); fclose(f);
    return rc;
}

/* Cinema. Off changes nothing. A quiet tone comes up. A hot tone and hiss do not. A shot stays under the ceiling. */
static int check_loud(void) {
    enum { sr = 48000, n = 96000, short_n = 48000 };
    static float ql[n], qr[n], qdry[n], rdry[n];
    static float hot[short_n], hiss[short_n], bed[n];
    static float ident_a[4096], ident_b[4096];
    float amp, gap_in, gap_out;
    double quiet_up, hot_down, hiss_up, bed_up, shot_up;
    float peak, a_l, a_r;
    int i, bad = 0, shot_at, shot_len;
    Effect* e;
    float* in[8];
    float* outp[8];

    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);

    for (i = 0; i < 4096; i++) ident_a[i] = sinf(2.f * 3.14159265f * 180.f * i / sr) * 0.02f;
    memcpy(ident_b, ident_a, sizeof ident_a);
    e = VSTPluginMain(0);
    in[0] = outp[0] = ident_a;
    replace(e, in, outp, 4096);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    e = VSTPluginMain(0);
    e->set_param(e, 4, 0.f);
    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);
    in[0] = outp[0] = ident_b;
    replace(e, in, outp, 4096);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    if (memcmp(ident_a, ident_b, sizeof ident_a) != 0) bad = 1;

    a_l = powf(10.f, -45.f / 20.f) * 1.41421356f;
    a_r = a_l * powf(10.f, -10.f / 20.f);
    for (i = 0; i < n; i++) {
        float s = sinf(2.f * 3.14159265f * 1000.f * i / sr);
        qdry[i] = ql[i] = s * a_l;
        rdry[i] = qr[i] = s * a_r;
    }
    e = VSTPluginMain(0);
    e->set_param(e, 4, 0.67f);
    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);
    in[0] = ql; in[1] = qr; outp[0] = ql; outp[1] = qr;
    replace(e, in, outp, n);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    quiet_up = rms_db(ql, n / 2, n) - rms_db(qdry, n / 2, n);
    gap_in = (float)(rms_db(qdry, n / 2, n) - rms_db(rdry, n / 2, n));
    gap_out = (float)(rms_db(ql, n / 2, n) - rms_db(qr, n / 2, n));

    amp = powf(10.f, -6.f / 20.f) * 1.41421356f;
    for (i = 0; i < short_n; i++) hot[i] = sinf(2.f * 3.14159265f * 1000.f * i / sr) * amp;
    memcpy(qdry, hot, sizeof hot);
    e = VSTPluginMain(0);
    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);
    in[0] = outp[0] = qdry;
    replace(e, in, outp, short_n);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    e = VSTPluginMain(0);
    e->set_param(e, 4, 1.f);
    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);
    in[0] = outp[0] = hot;
    replace(e, in, outp, short_n);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    hot_down = rms_db(hot, short_n / 2, short_n) - rms_db(qdry, short_n / 2, short_n);

    amp = powf(10.f, -70.f / 20.f) * 1.41421356f;
    for (i = 0; i < short_n; i++) hiss[i] = sinf(2.f * 3.14159265f * 1000.f * i / sr) * amp;
    memcpy(qdry, hiss, sizeof hiss);
    e = VSTPluginMain(0);
    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);
    in[0] = outp[0] = qdry;
    replace(e, in, outp, short_n);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    e = VSTPluginMain(0);
    e->set_param(e, 4, 1.f);
    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);
    in[0] = outp[0] = hiss;
    replace(e, in, outp, short_n);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    hiss_up = rms_db(hiss, short_n / 2, short_n) - rms_db(qdry, short_n / 2, short_n);

    amp = powf(10.f, -36.f / 20.f);
    shot_at = (int)(sr * 1.4f);
    shot_len = (int)(sr * 0.01f);
    for (i = 0; i < n; i++) bed[i] = sinf(2.f * 3.14159265f * 180.f * i / sr) * amp;
    for (i = 0; i < shot_len; i++) bed[shot_at + i] = sinf(2.f * 3.14159265f * 1400.f * (shot_at + i) / sr) * 0.7f;
    memcpy(qdry, bed, sizeof bed);
    e = VSTPluginMain(0);
    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);
    in[0] = outp[0] = qdry;
    replace(e, in, outp, n);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    e = VSTPluginMain(0);
    e->set_param(e, 4, 1.f);
    memset(in, 0, sizeof in);
    memset(outp, 0, sizeof outp);
    in[0] = outp[0] = bed;
    replace(e, in, outp, n);
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    peak = 0.f;
    for (i = 0; i < n; i++) if (fabsf(bed[i]) > peak) peak = fabsf(bed[i]);
    bed_up = rms_db(bed, sr, (int)(sr * 1.2f)) - rms_db(qdry, sr, (int)(sr * 1.2f));
    shot_up = rms_db(bed, shot_at, shot_at + shot_len) - rms_db(qdry, shot_at, shot_at + shot_len);
    printf("loud: quiet %+.1f dB, hot %+.1f dB, hiss %+.2f dB, gap %.2f, bed %+.1f dB, shot %+.2f dB, peak %.3f\n",
        quiet_up, hot_down, hiss_up, gap_out - gap_in, bed_up, shot_up, peak);
    if (!(quiet_up > 8.0)) bad = 1;
    if (!(hot_down < -4.0)) bad = 1;
    if (!(fabs(hiss_up) < 1.0)) bad = 1;
    if (!(fabsf(gap_out - gap_in) < 0.2f)) bad = 1;
    if (!(bed_up > 3.0)) bad = 1;
    if (!(shot_up < 1.0)) bad = 1;
    if (!(peak <= 0.95f)) bad = 1;
    return bad;
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
    if (check_classic_tick()) return 1;
    if (check_step()) return 1;
    if (check_loud()) return 1;
    return check_nova();
}
#endif
