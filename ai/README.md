# Boost step: the footstep detector

A small neural network (36 numbers in, 24 hidden, 1 out) listens to the game sound and says how sure it is that a footstep is playing.
`plugin/tune.c` runs it inside the plug-in. When **Boost step** is on, a step is lifted by up to 16 dB at full power, only while it is playing.
Nothing else is turned down. When Boost step is off, nothing here runs and the sound is exactly what it was before.

## Keep training it
1. Put clips in a folder you keep safe (not in this repository, because their licences differ):
   `steps/` clean footsteps, `keep/` sounds that must NOT count as steps (parachute, cars, shots), optional `background/`.
2. `python ai/train.py --data FOLDER --eval` runs a leave-one-clip-out test. It trains without a clip and scores on that clip.
   Steps are mixed with synthetic hiss, wind and hum 3 to 15 dB below them. Steps that noise hides are not counted as misses.
   It fails if the mean accuracy is under 85%.
3. `python ai/train.py --data FOLDER` writes `plugin/step_model.h` and `ai/parity.bin`. Commit both. CI builds the plug-in and
   checks the C detector against Python on `parity.bin` (difference must stay under 0.02).

`ai/features.py` and `step_arm`/`step_feed` in `plugin/tune.c` must stay the same. Change one, change both, and re-run step 3.
The most useful new data is real game audio: steps, jumps, parachute, cars and shots recorded from the game.
