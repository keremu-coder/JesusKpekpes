Jesus Kpekpes

Step 1. engine/engine.ts
Reads eight speakers, then plays stereo.

Step 2. host/
On the PC, from this folder:

  dotnet run --project host -- test
  dotnet run --project host -- devices

test plays a side step, then a front shot, and prints the buffer Windows gave the headphones.

  dotnet run --project host -- listen --out "Headphone name"

listen reads the default device and plays into a different one. Same device howls.
If the default device is not 7.1, the side step is already gone before the engine sees it.

This program is not a device the game can select. Windows only adds that from a signed driver. That is step 3.
