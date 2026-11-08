export const CHANNEL = {
  L: 0,
  R: 1,
  C: 2,
  LFE: 3,
  SL: 4,
  SR: 5,
  RL: 6,
  RR: 7,
} as const;

export type CorrectionBand = { hz: number; db: number; q: number };

type Biquad = {
  b0: number;
  b1: number;
  b2: number;
  a1: number;
  a2: number;
  x1: number;
  x2: number;
  y1: number;
  y2: number;
};

function peaking(hz: number, db: number, q: number, sr: number): Biquad {
  const A = Math.pow(10, db / 40);
  const w0 = (2 * Math.PI * hz) / sr;
  const cos = Math.cos(w0);
  const alpha = Math.sin(w0) / (2 * q);
  const a0 = 1 + alpha / A;
  return {
    b0: (1 + alpha * A) / a0,
    b1: (-2 * cos) / a0,
    b2: (1 - alpha * A) / a0,
    a1: (-2 * cos) / a0,
    a2: (1 - alpha / A) / a0,
    x1: 0,
    x2: 0,
    y1: 0,
    y2: 0,
  };
}

function bandpass(hz: number, q: number, sr: number): Biquad {
  const w0 = (2 * Math.PI * hz) / sr;
  const cos = Math.cos(w0);
  const alpha = Math.sin(w0) / (2 * q);
  const a0 = 1 + alpha;
  return {
    b0: alpha / a0,
    b1: 0,
    b2: -alpha / a0,
    a1: (-2 * cos) / a0,
    a2: (1 - alpha) / a0,
    x1: 0,
    x2: 0,
    y1: 0,
    y2: 0,
  };
}

function tick(filter: Biquad, x: number) {
  const y = filter.b0 * x + filter.b1 * filter.x1 + filter.b2 * filter.x2 - filter.a1 * filter.y1 - filter.a2 * filter.y2;
  filter.x2 = filter.x1;
  filter.x1 = x;
  filter.y2 = filter.y1;
  filter.y1 = y;
  return y;
}

const SURROUND = [CHANNEL.SL, CHANNEL.SR, CHANNEL.RL, CHANNEL.RR];
const FRONT = [CHANNEL.L, CHANNEL.R, CHANNEL.C];

export type Engine = {
  setCorrection: (bands: CorrectionBand[]) => void;
  process: (input: Float32Array[]) => { left: Float32Array; right: Float32Array };
};

export function createEngine(sampleRate: number): Engine {
  let correction: Biquad[][] = [];
  const stepFilters = SURROUND.map(() => bandpass(3200, 1.4, sampleRate));
  const fast = SURROUND.map(() => 0);
  const slow = SURROUND.map(() => 0);
  const fastCoef = 1 - Math.exp(-1 / (sampleRate * 0.008));
  const slowCoef = 1 - Math.exp(-1 / (sampleRate * 0.08));
  let frontEnv = 0;
  let frontGain = 1;
  const frontCoef = 1 - Math.exp(-1 / (sampleRate * 0.004));
  const frontRelease = 1 - Math.exp(-1 / (sampleRate * 0.1));

  function setCorrection(bands: CorrectionBand[]) {
    correction = Array.from({ length: 8 }, () => bands.map((band) => peaking(band.hz, band.db, band.q, sampleRate)));
  }

  function process(input: Float32Array[]) {
    const n = input[0]?.length ?? 0;
    const left = new Float32Array(n);
    const right = new Float32Array(n);
    const ch = input.map((row) => Float32Array.from(row));

    for (let i = 0; i < n; i++) {
      for (let c = 0; c < 8; c++) {
        let x = ch[c]?.[i] ?? 0;
        const filters = correction[c];
        if (filters) {
          for (const filter of filters) x = tick(filter, x);
        }
        ch[c][i] = x;
      }

      let frontPeak = 0;
      for (const c of FRONT) frontPeak = Math.max(frontPeak, Math.abs(ch[c][i]));
      frontEnv = frontPeak > frontEnv ? frontEnv + (frontPeak - frontEnv) * frontCoef : frontEnv + (frontPeak - frontEnv) * frontRelease;
      const hold = frontEnv > 0.35 ? 0.62 : 1;
      frontGain += (hold - frontGain) * frontCoef;
      for (const c of FRONT) ch[c][i] *= frontGain;

      for (let s = 0; s < SURROUND.length; s++) {
        const c = SURROUND[s];
        const band = tick(stepFilters[s], ch[c][i]);
        const mag = Math.abs(band);
        fast[s] += (mag - fast[s]) * fastCoef;
        slow[s] += (fast[s] - slow[s]) * slowCoef;
        const rise = fast[s] / (slow[s] + 1e-5);
        const lift = Math.max(0, Math.min(1, (rise - 1.7) / 2));
        ch[c][i] += band * lift * 1.8;
      }

      const l = ch[CHANNEL.L][i];
      const r = ch[CHANNEL.R][i];
      const c = ch[CHANNEL.C][i];
      left[i] = l + 0.5 * c + 0.7 * ch[CHANNEL.SL][i] + 0.5 * ch[CHANNEL.RL][i];
      right[i] = r + 0.5 * c + 0.7 * ch[CHANNEL.SR][i] + 0.5 * ch[CHANNEL.RR][i];
    }

    return { left, right };
  }

  return { setCorrection, process };
}

export function emptyChannels(length: number) {
  return Array.from({ length: 8 }, () => new Float32Array(length));
}
