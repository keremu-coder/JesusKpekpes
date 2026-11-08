#pragma once
#include <stdint.h>

#define JK_RATE 48000
#define JK_FRAMES 128
#define JK_CHANNELS 8
#define JK_NAME L"Jesus Kpekpes"

/* One block is 128 frames, 8 speakers, 48 kHz.
   The game writes this device. The window reads it and plays the headphones. */
typedef struct JkBlock {
    float ch[JK_CHANNELS][JK_FRAMES];
} JkBlock;
