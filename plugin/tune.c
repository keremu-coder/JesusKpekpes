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
#include "freq_guard.h"

#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#else
#define EXPORT
#endif
