import assert from "node:assert/strict";
import test from "node:test";
import { CHANNEL, createEngine, emptyChannels } from "./engine.ts";

const sr = 48_000;

function tone(hz: number, length: number, amp: number) {
  const data = new Float32Array(length);
  for (let i = 0; i < length; i++) data[i] = Math.sin((2 * Math.PI * hz * i) / sr) * amp;
  return data;
}

function energy(data: Float32Array) {
  let sum = 0;
  for (const x of data) sum += x * x;
  return sum / data.length;
}

test("a short side step is lifted more than a steady tone in the same band", () => {
  const click = emptyChannels(4000);
  for (let i = 2000; i < 2120; i++) {
    click[CHANNEL.SL][i] = Math.sin((2 * Math.PI * 3200 * i) / sr) * 0.25 * Math.sin(((i - 2000) / 120) * Math.PI);
  }
  const steady = emptyChannels(sr);
  steady[CHANNEL.SL].set(tone(3200, sr, 0.25));

  const clickGain = energy(createEngine(sr).process(click).left.slice(2000, 2300)) / energy(click[CHANNEL.SL].slice(2000, 2300));
  const steadyGain = energy(createEngine(sr).process(steady).left.slice(sr - 2000)) / energy(steady[CHANNEL.SL].slice(sr - 2000));
  assert.ok(clickGain > steadyGain * 1.4, `click gain ${clickGain} steady gain ${steadyGain}`);
});

test("a loud front hit does not turn the side step down", () => {
  const step = emptyChannels(1500);
  for (let i = 400; i < 520; i++) {
    step[CHANNEL.SL][i] = Math.sin((2 * Math.PI * 3200 * i) / sr) * 0.3 * Math.sin(((i - 400) / 120) * Math.PI);
  }
  const withGun = step.map((row) => Float32Array.from(row));
  for (let i = 400; i < 900; i++) withGun[CHANNEL.L][i] = Math.sin((2 * Math.PI * 90 * i) / sr) * 0.95;

  const alone = energy(createEngine(sr).process(step).left.slice(400, 700));
  const besideGun = energy(createEngine(sr).process(withGun).left.slice(400, 700));
  assert.ok(besideGun > alone * 0.85, `side step fell from ${alone} to ${besideGun}`);
});

test("headphone correction is applied before the fold", () => {
  const input = emptyChannels(2000);
  input[CHANNEL.L].set(tone(1000, 2000, 0.2));
  const plain = createEngine(sr);
  const corrected = createEngine(sr);
  corrected.setCorrection([{ hz: 1000, db: 6, q: 2 }]);
  const dry = energy(plain.process(input).left);
  const lifted = energy(corrected.process(input.map((row) => Float32Array.from(row))).left);
  assert.ok(lifted > dry * 1.5);
});
