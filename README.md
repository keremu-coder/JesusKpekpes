Jesus Kpekpes

**Run it from here. This folder is the source, and it does not contain the program.**

1. Download [JesusKpekpes-Setup.exe](https://github.com/keremu-coder/JesusKpekpes/releases/latest/download/JesusKpekpes-Setup.exe).
2. If Windows says it protected your PC, choose More info, then Run anyway.
3. Click Yes when it asks for Administrator.
4. Press Install. Jesus Kpekpes is then in the Start menu.

The zip is the same program without the installer: [JesusKpekpes-win-x64.zip](https://github.com/keremu-coder/JesusKpekpes/releases/latest/download/JesusKpekpes-win-x64.zip). Extract the whole zip and run `JesusKpekpes.exe` inside it. Do not use the green Code button. That download is source, and there is no program in it.

The window has two separate parts.

Audio tune. Drop and Well are the two footstep mixes. Both trim the boxy 500 Hz and raise the shoe grit at 3.4 kHz with the Footstep hit slider. With the plug-in, the 3.4 kHz step tick is lifted up to 9 dB only while it is a hit, and the rest of that step is lifted by one extra gain, up to 9 dB, the same amount in every ear. A steady tone is not lifted. Both lifts step aside while a gun is loud. Footstep hit at 0 leaves the extra gain off. Nova Pro is a third mix, only for the Arctis Nova Pro on the small USB adapter. It does not replace Drop or Well. The step tick sits at 3.2 kHz, where that headset is dipped, and the 8 kHz peak is pulled down. A step is lifted only while it is a hit, so a steady sound is not turned up. Stereo and 7.1 both keep a left step on the left. It is not a hearing aid. Leave Sonar, the GameDAC EQ, and Insured Audio off, or they stack. Stereo is the default. Turn it off and the rest of your audio, including Rara, is left in place. Keep my other audio setup is on by default.

Get the audio programs opens the official pages for Equalizer APO, HeSuVi, ReaPlugs and VoiceMeeter. Equalizer APO and ReaPlugs also have Download + open: the file is checked against a pinned SHA-256, and only then is the vendor's own setup opened for you to click through. A file that does not match is deleted and never opened. When a vendor ships a newer version, the pin must be updated first. Press Check again afterwards. HeSuVi, ReaPlugs and VoiceMeeter are not turned on.

PC tune. What will change lists the PC and controller settings before anything is written. Optimize removes Game Bar and every other Xbox app (the Xbox app, Gaming Services, the Identity Provider) and switches off the Xbox background services, because Battle.net and DS4Windows need none of them. DS4Windows' own controller driver is not touched. Games from the Microsoft Store or Game Pass for PC do need them, so set `RemoveXboxApps` to false in config.json if you ever play those. It also silences the "Get an app to open this ms-gamebar link" window that Windows shows once Game Bar is gone. On dual-CCD X3D chips only Game Bar is kept, because AMD's V-Cache driver needs it. If Game Bar is installed again, the silencer steps aside. Optimize does not touch audio. Undo puts those PC settings back and leaves audio alone.

Check this PC reads Secure Boot, Resizable BAR, RAM speed and the Call of Duty config path. It does not write the game file. Optimize sets NVIDIA Power management mode to Prefer maximum performance for Call of Duty, and Undo puts the previous value back. If NVIDIA's interface is missing, nothing is written and the check tells you to set it by hand. The check can see Resizable BAR. It cannot change the BIOS.

The Call of Duty settings window sets Reflex to On + boost, and the in-game frame cap to one under the primary monitor. A 200 Hz panel gets 199. Resolution and DLSS stay as they are. Close the game first. Undo puts the original file back.

Monitor and frames. If Windows is running the monitor below the highest rate it already lists at the current resolution, Optimize raises it to that rate. A 60 Hz desktop on a panel that lists 200 Hz at the same resolution becomes 200. The resolution is not changed, and a rate that is not in Windows' list is not invented. Undo puts the old rate back. Record before and Record after run PresentMon for one minute and print average FPS, 1% low and 0.1% low from the displayed frames. Those numbers are the capture. They are not a guess, and one pair is not proof.

Startup menu. Tick a program to stop it opening with Windows. The program is not deleted. Windows Security and this program stay on. Undo turns the ones this window turned off back on.

Install. JesusKpekpes-Setup.exe puts the program in Program Files, adds a Start menu shortcut, and adds an entry under Windows Settings, Apps. Uninstall from there puts the PC changes back and leaves audio as it is. The zip is the same program without the installer. The exe is not signed unless a certificate is stored in the repository secrets, and updates come from inside the program: the Update button appears on the first window when a newer release exists (see USING.txt for how it is checked and what to protect).

Name the hitch watches for about 20 seconds while you play, then prints the driver or process behind the longest stall. It does not change a setting, it does not turn that driver off, and a name by itself does not add frames. If Windows will not give the trace, it says so and stops.

Download. The Release workflow builds JesusKpekpes-Setup.exe and JesusKpekpes-win-x64.zip. Run the setup to install, or extract the whole zip and run JesusKpekpes.exe from inside it. [USING.txt](USING.txt) is the short guide: the buttons, the PC test to copy back, and the Rara comparison. Those two results have to come from your PC. This page cannot invent them. Screenshots have to be taken there too.

The plug-in does not include Steinberg's VST2 SDK. [plugin/NOTICE.txt](plugin/NOTICE.txt) is the finding. Do not sell it as a VST product until a lawyer has looked at it.

Secure Boot stays on.
