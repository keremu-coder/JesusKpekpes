"""Feature extractor. The C plugin (plugin/tune.c, step_*) must stay identical to this file.
12 band-pass bands (constant Q) at 48 kHz mono, band energy every 10 ms, then three views of it:
the level, the level minus its slow average (novelty), and the level minus the level 30 ms earlier."""
import numpy as np, scipy.signal as sg

RATE = 48000
HOP = 480            # 10 ms
BANDS = 12
LOW_HZ, HIGH_HZ = 100.0, 7600.0
Q = 2.4
FLOOR_DB = -100.0
SLOW = 1.0 - np.exp(-10.0 / 400.0)   # slow average, one step per hop
LAG = 3
N_FEATURES = BANDS * 3

def centres():
    return np.geomspace(LOW_HZ, HIGH_HZ, BANDS)

def biquad(hz):
    # Same band-pass as set_band() in tune.c (constant 0 dB peak gain), in float32 arithmetic order.
    w = 2.0 * 3.14159274 * hz / RATE
    alpha = np.sin(w) / (2.0 * Q)
    a0 = 1.0 + alpha
    b = np.array([alpha / a0, 0.0, -alpha / a0])
    a = np.array([1.0, -2.0 * np.cos(w) / a0, (1.0 - alpha) / a0])
    return b, a

def band_levels(x):
    """x: mono float signal at 48 kHz. Returns (frames, BANDS) levels in dB."""
    n = len(x) // HOP
    out = np.empty((n, BANDS))
    for i, hz in enumerate(centres()):
        b, a = biquad(hz)
        y = sg.lfilter(b, a, x)
        e = (y[: n * HOP] ** 2).reshape(n, HOP).mean(1)
        out[:, i] = 10.0 * np.log10(e + 1e-10)
    return out

def features(x):
    L = band_levels(x)
    n = len(L)
    slow = np.empty_like(L)
    s = np.full(BANDS, FLOOR_DB)
    for t in range(n):
        s = s + SLOW * (L[t] - s)
        slow[t] = s
    prev = np.vstack([np.full((LAG, BANDS), FLOOR_DB), L[:-LAG]]) if n > LAG else np.full_like(L, FLOOR_DB)
    # slow is the average including the current frame, as the plugin updates it before it reads it
    return np.hstack([L, L - slow, L - prev])
