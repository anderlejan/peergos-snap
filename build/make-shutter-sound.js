// Makes src/PeergosSnap/Assets/shutter.wav, the sound Peergos Snap plays when a picture is taken (Settings → Capture).
// A telephoto lens focusing – a geared motor that whirs up and settles, with a small tick when it locks – then the
// mirror and the shutter of a reflex camera: a bright "ka" and a heavier "chunk" with the body's low resonance.
// Made from scratch (no recordings); the noise is seeded, so every run writes the same file.
// Run: node build/make-shutter-sound.js
'use strict';
const fs = require('fs');
const path = require('path');

const RATE = 44100;
const out = new Float64Array(Math.round(RATE * 0.42));

let seed = 0x5eed2400;
function noise() {
  seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
  return seed / 4294967296 * 2 - 1;
}

// The focus motor: about 140 ms, pitch rising while it hunts, easing as it locks; a whine with gear grit.
{
  const T = 0.14;
  let ph = 0;
  for (let i = 0; i < Math.round(T * RATE); i++) {
    const t = i / RATE;
    const f = t < 0.09 ? 230 + 330 * (t / 0.09) : 560 - 120 * ((t - 0.09) / (T - 0.09));
    ph += 2 * Math.PI * f / RATE;
    const whine = Math.tanh(3 * Math.sin(ph)) * 0.35 + Math.sin(2 * ph) * 0.25 + Math.sin(3 * ph + 0.3) * 0.15;
    const grit = noise() * 0.22 * (0.5 + 0.5 * Math.sin(7 * ph));
    const env = Math.min(1, t / 0.012) * Math.min(1, (T - t) / 0.02);
    out[i] += 0.30 * env * (whine + grit);
  }
}

// A click: a noise burst (optionally brightened or dulled) with ringing partials [frequency, level, decay].
function click(t0, amp, tau, tone, rings) {
  const s0 = Math.round(t0 * RATE);
  const n = Math.round(tau * 9 * RATE + 0.06 * RATE);
  let prev = 0, low = 0;
  for (let i = 0; i < n && s0 + i < out.length; i++) {
    const t = i / RATE;
    const x = noise();
    let y;
    if (tone === 'bright') y = x - prev;                 // high-passed: a crisp snap
    else { low += 0.18 * (x - low); y = low * 2.2; }     // low-passed: a dull thud
    prev = x;
    let v = y * Math.exp(-t / tau);
    for (const [f, a, d] of rings) v += a * Math.exp(-t / d) * Math.sin(2 * Math.PI * f * t);
    out[s0 + i] += amp * v;
  }
}

click(0.144, 0.06, 0.0015, 'bright', [[3800, 0.3, 0.003]]);                         // focus locks
click(0.175, 0.55, 0.0025, 'bright', [[2900, 0.35, 0.006], [4700, 0.2, 0.004]]);    // "ka": mirror up, first curtain
click(0.237, 0.75, 0.0060, 'dull', [[180, 0.9, 0.028], [410, 0.45, 0.018], [1250, 0.2, 0.010]]); // "chunk"
click(0.249, 0.25, 0.0030, 'bright', [[2300, 0.15, 0.005]]);                        // the mirror bounces
click(0.261, 0.12, 0.0020, 'bright', []);                                           // and settles

// Peak at -6 dBFS, a short fade at the end, 16-bit mono PCM.
let peak = 0;
for (const v of out) peak = Math.max(peak, Math.abs(v));
const gain = 0.5 / peak;
const fade = Math.round(0.02 * RATE);
const pcm = Buffer.alloc(out.length * 2);
for (let i = 0; i < out.length; i++) {
  const f = i > out.length - fade ? (out.length - i) / fade : 1;
  pcm.writeInt16LE(Math.round(Math.max(-1, Math.min(1, out[i] * gain * f)) * 32767), i * 2);
}
const head = Buffer.alloc(44);
head.write('RIFF', 0); head.writeUInt32LE(36 + pcm.length, 4); head.write('WAVE', 8);
head.write('fmt ', 12); head.writeUInt32LE(16, 16); head.writeUInt16LE(1, 20); head.writeUInt16LE(1, 22);
head.writeUInt32LE(RATE, 24); head.writeUInt32LE(RATE * 2, 28); head.writeUInt16LE(2, 32); head.writeUInt16LE(16, 34);
head.write('data', 36); head.writeUInt32LE(pcm.length, 40);
const file = path.join(__dirname, '..', 'src', 'PeergosSnap', 'Assets', 'shutter.wav');
fs.writeFileSync(file, Buffer.concat([head, pcm]));
console.log(`${file}: ${(out.length / RATE).toFixed(2)} s, ${head.length + pcm.length} bytes`);
