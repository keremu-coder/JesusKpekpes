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
#include "../freq_guard.h" // FIXED: Points to root directory

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

typedef struct {
    Lane lane[8];
    float rate;
    float hit;
    int mode;
    fg_state_t gun_guard;
} Bank;

static void set_band(Stage* stage, float hz, float q, float rate) {
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

static void nova_replace(Effect* effect, float** inputs, float** outputs, i32 frames) {
    Bank* bank = effect->object;
    float max_weight = 7.f * bank->hit;
    float max_tick = 10.f * bank->hit;
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
        for (ch = 0; ch < nch; ch++) {
            Lane* lane;
            float gun_gain, y, mag;
            if (!inputs[ch] || !outputs[ch]) continue;
            lane = &bank->lane[ch];
            gun_gain = powf(10.f, -reduction(lane->gun_env, -16.f, 2.f / 3.f) / 20.f);
            y = other_b[ch] + weight_b[ch] * weight_gain + tick_b[ch] * tick_gain + gun_b[ch] * gun_gain;
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
    
    // Apply the Gunshot Suppressor
    fg_process_block(&bank->gun_guard, outputs, frames);
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
        return;
    }
    if (index == 1) {
        if (value < 0.f) value = 0.f;
        if (value > 1.f) value = 1.f;
        bank->hit = value;
    }
}

static float get_param(Effect* effect, i32 index) {
    Bank* bank = effect ? effect->object : 0;
    if (!bank) return 0.f;
    if (index == 0) return bank->mode ? 1.f : 0.f;
    if (index == 1) return bank->hit;
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
        return 1;
    }
    if (op == OP_PARAM_NAME && ptr) {
        const char* name = index == 0 ? "Nova" : index == 1 ? "Hit" : "";
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
    
    // Initialize the Gunshot Suppressor (48000Hz, 8 channels)
    fg_init(&bank->gun_guard, 48000.0f, 8);

    effect->magic = 0x56737450;
    effect->dispatch = dispatch;
    effect->older = (void (*)(Effect*, float**, float**, i32))replace;
    effect->set_param = set_param;
    effect->get_param = get_param;
    effect->inputs = 8;
    effect->outputs = 8;
    effect->flags = 1 << 4;
    effect->params = 2;
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

// FIXED: Safely handles 8 channels to prevent Access Violation crash
static int check_ears(void) {
    enum { sr = 48000, n = 48000 };
    static float l[n], r[n], lin[n], rin[n], silent[n];
    memset(silent, 0, sizeof(silent));
    static const double levels[] = { -35, -30, -20, -12, -6 };
    int bad = 0, k, i;
    for (k = 0; k < 5; k++) {
        float a_l = powf(10.f, (float)levels[k] / 20.f) * 1.41421356f;
        float a_r = a_l * powf(10.f, -15.f / 20.f);
        Effect* e = VSTPluginMain(0);
        float* in[8] = { l, r, silent, silent, silent, silent, silent, silent };
        float* out[8] = { l, r, silent, silent, silent, silent, silent, silent };
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

// FIXED: Safely handles 8 channels
static int check_nova(void) {
    enum { sr = 48000, n = 48000 };
    static float tone[n], tone_r[n], dry[n], silent[n];
    static float burst[n], burst_r[n], burst_dry[n];
    memset(silent, 0, sizeof(silent));
    Effect* e = VSTPluginMain(0);
    float* in[8] = { tone, tone_r, silent, silent, silent, silent, silent, silent };
    float* out[8] = { tone, tone_r, silent, silent, silent, silent, silent, silent };
    char name0[9], name1[9];
    int i, bad = 0;
    int burst_at = n / 3;
    int burst_len = (int)(sr * 0.032f);
    double steady, hit;
    memset(name0, 0, sizeof name0);
    memset(name1, 0, sizeof name1);
    dispatch(e, OP_PARAM_NAME, 0, 0, name0, 0.f);
    dispatch(e, OP_PARAM_NAME, 1, 0, name1, 0.f);
    if (strcmp(name0, "Nova") != 0 || strcmp(name1, "Hit") != 0 || e->params != 2) bad = 1;
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
        float* zin[8] = { z, zr, silent, silent, silent, silent, silent, silent };
        float* zout[8] = { z, zr, silent, silent, silent, silent, silent, silent };
        replace(e, zin, zout, 64);
        for (i = 0; i < 64; i++) if (z[i] != 0.f || zr[i] != 0.f) bad = 1;
    }
    dispatch(e, OP_CLOSE, 0, 0, 0, 0.f);
    return bad;
}

static int read_full(void* dst, size_t bytes) {
    return fread(dst, 1, bytes, stdin) == bytes;
}

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
    static float left[n], right[n], silent[n];
    memset(silent, 0, sizeof(silent));
    Effect* effect;
    float* inputs[8] = { left, right, silent, silent, silent, silent, silent, silent };
    float* outputs[8] = { left, right, silent, silent, silent, silent, silent, silent };
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
    return check_nova();
}
#endif
