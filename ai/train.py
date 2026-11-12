"""Train the footstep detector from a folder of clips and write plugin/step_model.h.
  python ai/train.py --data DIR [--eval] [--out plugin/step_model.h]
DIR has steps/*.wav (clean footsteps), keep/*.wav (sounds to leave alone: parachute, cars, shots) and
optionally background/*.wav (real ambience). Add clips and run it again; the model is rebuilt each time.
Clips are mixed with synthetic noise at random levels, so more clips means more variety, not more files to label."""
import argparse, glob, os, sys, json
import numpy as np, scipy.io.wavfile as wav, scipy.signal as sg
from sklearn.neural_network import MLPClassifier
sys.path.insert(0, os.path.dirname(__file__))
import features as F

HIDDEN = 24
SNRS = (-3, 3, 9, 15, 22)
EVAL_SNRS = (3, 9, 15)   # steps 3 to 15 dB above the background
THRESHOLD = 0.5

def load(folder):
    out = {}
    for f in sorted(glob.glob(os.path.join(folder, "*.wav"))):
        sr, x = wav.read(f)
        if x.ndim > 1: x = x.mean(1)
        x = x.astype(np.float64) / (32768.0 if x.dtype.kind == "i" else 1.0)
        if sr != F.RATE:
            x = sg.resample_poly(x, F.RATE, sr)
        out[os.path.basename(f)[:-4]] = x
    return out

def noise(rng, n, kind):
    x = rng.standard_normal(n)
    if kind == 1:   # pink-ish
        x = sg.lfilter([1], [1, -0.97], x)
    elif kind == 2:  # wind rumble
        x = sg.lfilter(*sg.butter(2, 1200 / 24000), x)
    elif kind == 3:  # hiss
        x = sg.lfilter(*sg.butter(2, [3000 / 24000, 15000 / 24000], "band"), x)
    elif kind == 4:  # steady hum plus rumble (engine-like, slowly moving)
        t = np.arange(n) / F.RATE
        x = sg.lfilter(*sg.butter(2, 600 / 24000), x) * 0.6 + np.sin(2 * np.pi * (70 + 8 * np.sin(2 * np.pi * 0.3 * t)) * t)
    return x / (np.std(x) + 1e-12)

def labels(sig, bed):
    """1 = a step is audible, 0 = no step, -1 = do not count. A step frame quieter than the background is
    not counted: nobody could hear it, so it cannot be a fair miss. Frames just under the step level are skipped too."""
    n = len(sig) // F.HOP
    e = (sig[: n * F.HOP] ** 2).reshape(n, F.HOP).mean(1)
    eb = (bed[: n * F.HOP] ** 2).reshape(n, F.HOP).mean(1)
    db = 10 * np.log10(e + 1e-12); dbb = 10 * np.log10(eb + 1e-12)
    top = np.percentile(db, 99)
    y = np.zeros(n, int)
    step = (db > top - 30) & (db > -75)
    y[step & (db > dbb)] = 1
    y[step & (db <= dbb)] = -1
    y[(~step) & (db > top - 40)] = -1
    return y

def mix(rng, clip, snr, kind):
    pad = F.RATE // 2
    gain = 10 ** (rng.uniform(-24, 0) / 20) / (np.abs(clip).max() + 1e-9)
    sig = np.concatenate([np.zeros(pad), clip * gain, np.zeros(pad)])
    ref = np.sqrt(np.mean(sig[sig != 0] ** 2))
    bed = noise(rng, len(sig), kind) * ref * 10 ** (-snr / 20)
    return sig + bed, sig, bed

CACHE = {}
def build(rng, steps, keep, names, snrs=SNRS):
    X, Y = [], []
    for nm in names:
        for snr in snrs:
            for kind in range(5):
                key = (nm, snr, kind)
                if key not in CACHE:
                    x, sig, bed = mix(np.random.default_rng(abs(hash(key)) % 2**32), steps[nm], snr, kind)
                    f = F.features(x); CACHE[key] = (f, labels(sig, bed)[: len(f)])
                f, y = CACHE[key]
                keepm = y >= 0
                X.append(f[keepm]); Y.append(y[keepm])
    return np.vstack(X), np.concatenate(Y)

def negatives(rng, keep):
    X, Y = [], []
    for kind in range(5):
        for lvl_db in (-50, -40, -30, -20, -10):
            f = F.features(noise(rng, F.RATE * 5, kind) * 10 ** (lvl_db / 20)); X.append(f); Y.append(np.zeros(len(f), int))
    for nm, clip in keep.items():
        for lvl_db in (-30, -18, -6):
            clip_n = clip / (np.abs(clip).max() + 1e-9) * 10 ** (lvl_db / 20)
            long = np.tile(np.concatenate([clip_n, np.zeros(F.RATE // 4)]), max(1, int(F.RATE * 3 / (len(clip_n) + F.RATE // 4)) + 1))
            for kind in (None, 0, 2, 3):
                x = long if kind is None else long + noise(rng, len(long), kind) * 10 ** ((lvl_db - 12) / 20)
                f = F.features(x); X.append(f); Y.append(np.zeros(len(f), int))
    return np.vstack(X), np.concatenate(Y)

def fit(X, Y, seed=0):
    mu, sd = X.mean(0), X.std(0) + 1e-6
    clf = MLPClassifier((HIDDEN,), activation="relu", alpha=1e-3, max_iter=300, early_stopping=True, random_state=seed)
    clf.fit((X - mu) / sd, Y)
    return clf, mu, sd

def predict(model, X):
    clf, mu, sd = model
    return clf.predict_proba((X - mu) / sd)[:, 1]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "plugin", "step_model.h"))
    ap.add_argument("--eval", action="store_true")
    ap.add_argument("--min-accuracy", type=float, default=0.85)
    a = ap.parse_args()
    rng = np.random.default_rng(7)
    steps = load(os.path.join(a.data, "steps")); keep = load(os.path.join(a.data, "keep"))
    names = list(steps)
    print(len(names), "step clips,", len(keep), "keep clips")
    Xn, Yn = negatives(rng, keep)
    if a.eval:
        accs, f1s = [], []
        print("held-out clip".ljust(44), "accuracy  precision  recall")
        for hold in names:
            tr = [n for n in names if n != hold]
            Xt, Yt = build(rng, steps, keep, tr)
            model = fit(np.vstack([Xt, Xn]), np.concatenate([Yt, Yn]))
            Xh, Yh = build(rng, steps, keep, [hold], snrs=EVAL_SNRS)
            p = predict(model, Xh) >= THRESHOLD
            acc = (p == Yh).mean(); tp = (p & (Yh == 1)).sum()
            prec = tp / max(1, p.sum()); rec = tp / max(1, (Yh == 1).sum())
            accs.append(acc); f1s.append(2 * prec * rec / max(1e-9, prec + rec))
            print(hold[:44].ljust(44), f"{acc:.3f}     {prec:.3f}      {rec:.3f}")
        print(f"MEAN held-out accuracy {np.mean(accs):.3f}  worst {np.min(accs):.3f}  mean F1 {np.mean(f1s):.3f}")
        # parachute and other keep clips must not count as steps
        model = fit(np.vstack([build(rng, steps, keep, names)[0], Xn]), np.concatenate([build(rng, steps, keep, names)[1], Yn]))
        for nm, clip in keep.items():
            f = F.features(np.concatenate([np.zeros(F.RATE // 2), clip / (np.abs(clip).max() + 1e-9) * 0.3, np.zeros(F.RATE // 2)]))
            print("keep clip", nm[:30], "fraction called a step:", round(float((predict(model, f) >= THRESHOLD).mean()), 3), "(training clip, not held out)")
        if np.mean(accs) < a.min_accuracy:
            print("BELOW", a.min_accuracy); return 1
        return 0
    Xt, Yt = build(rng, steps, keep, names)
    model = fit(np.vstack([Xt, Xn]), np.concatenate([Yt, Yn]))
    export(model, a.out, names, keep)
    write_parity(model, steps, os.path.join(os.path.dirname(a.out), "..", "ai", "parity.bin"))
    return 0

def write_parity(model, steps, path):
    """Two seconds of steps over hiss, and what this model says about it, so the C code can be checked against Python."""
    rng = np.random.default_rng(5)
    clip = steps[sorted(steps)[1]]
    x, _, _ = mix(rng, clip[: F.RATE * 2], 9, 3)
    x = x[: F.RATE * 2].astype(np.float32)
    p = predict(model, F.features(x.astype(np.float64))).astype(np.float32)
    with open(path, "wb") as f:
        f.write(np.int32(len(x)).tobytes() + x.tobytes() + np.int32(len(p)).tobytes() + p.tobytes())
    print("wrote", path, len(x), "samples", len(p), "frames")

def arr(name, a):
    a = np.asarray(a, dtype=np.float32).ravel()
    return "static const float %s[%d] = {\n    %s\n};\n" % (name, len(a), ",\n    ".join(", ".join("%.8ef" % v for v in a[i:i + 6]) for i in range(0, len(a), 6)))

def export(model, path, names, keep):
    clf, mu, sd = model
    W1 = clf.coefs_[0].T / sd[None, :]            # fold the scaling into the first layer
    b1 = clf.intercepts_[0] - (W1 * mu[None, :]).sum(1)
    w2 = clf.coefs_[1][:, 0]; b2 = float(clf.intercepts_[1][0])
    h = "/* Made by ai/train.py. Do not edit by hand. Clips: %d step, %d keep. */\n#define STEP_IN %d\n#define STEP_HID %d\n" % (len(names), len(keep), F.N_FEATURES, HIDDEN)
    h += arr("step_w1", W1) + arr("step_b1", b1) + arr("step_w2", w2) + "static const float step_b2 = %.8ef;\n" % b2
    open(path, "w").write(h)
    print("wrote", path)

if __name__ == "__main__":
    sys.exit(main())
