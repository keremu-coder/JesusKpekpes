# Unfinished stick-centre notes

Dumped before the package split. Not a finished package. Do not merge.

Read from dualshock-tools at commit b7cdcfaacfadfac5e80f4f40b16e85503a488e63.
No controller was attached. None of this was compiled or run.

## Which of the 12 values are centres

`FINETUNE_INPUT_SUFFIXES` in `js/modals/finetune-modal.js` is:

`LL, LT, RL, RT, LR, LB, RR, RB, LX, LY, RX, RY`

Slots 0 to 7 are the range and circularity trims. Slots 8 to 11 are the centres: left X, left Y, right X, right Y. The right-stick spin is slots 10 and 11.

## Live read and write

Feature report 0x80 sends the command. Feature report 0x81 is the answer.

DualSense read: command `[12, 2]`, wait about 100 ms, then read 0x81.
DualSense Edge read: command `[12, 4]`, same wait, same answer layout. The Edge is not the same command as the regular DualSense.

Header check used by both: byte 0 is 129, byte 1 is 12, byte 2 is 2 or 4, byte 3 is 2. Then 12 little-endian u16 values start at byte 4.

Live write, both models: command `[12, 1]` followed by the 12 values, each as low byte then high byte.

Regular DualSense finetune max is 65535. Edge finetune max is 4095.

The permanent save is a different NVS unlock and lock, and the Edge also locks its modules. Those commands were not copied. `calibrateSticks` and `calibrateRange` were not copied.

## Not done

No `CentreProtocol.cs`. No `LiveCentre.cs`. No backup file, no read-back, no rollback, no report-rate check. The direction and size of a centre step was not taken from the modal yet.
