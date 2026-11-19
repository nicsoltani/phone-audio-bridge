# Setup and troubleshooting guide

This guide covers the problems people actually hit when setting up Phone Audio Bridge, in the order you are
likely to meet them. If you are stuck, start with **[The correct Windows sound setup](#the-correct-windows-sound-setup)** -
most problems come from one wrong device setting.

- [The correct Windows sound setup](#the-correct-windows-sound-setup)
- [Problem: no microphone sound](#problem-no-microphone-sound)
- [Problem: sound plays on both the PC and the phone](#problem-sound-plays-on-both-the-pc-and-the-phone)
- [Problem: call audio plays on the PC or monitor, not the phone](#problem-call-audio-plays-on-the-pc-or-monitor-not-the-phone)
- [Problem: the phone is not detected](#problem-the-phone-is-not-detected)
- [How to read the status and the log](#how-to-read-the-status-and-the-log)
- [Still stuck?](#still-stuck)

---

## The correct Windows sound setup

VB-CABLE adds several devices to Windows. Only one of them is used, and only on the **input** side.

| Setting | Must be | Must **not** be |
|---|---|---|
| Windows **Output** (Settings > System > Sound) | Your real speakers or headphones, e.g. *Speakers / Headphones (Realtek Audio)* | *CABLE Input* or *CABLE In 16ch* |
| Windows **Input** | *CABLE Output (VB-Audio Virtual Cable)* | - |
| Tray menu > **Microphone cable** | *Automatic (VB-CABLE)* or *CABLE Input* | Your real speakers |
| Tray menu > **PC sound source** | *Windows default output* | *CABLE In 16ch* / *CABLE Input* |
| Calling apps (Teams, Zoom, WhatsApp...) microphone | *CABLE Output* | - |

When it is right, the tray tooltip reads like this:

```
Connected - speaker: Speakers / Headphones (Realtek Audio), mic: CABLE Input (VB-Audio Virtual Cable), call mode
```

The word after `speaker:` must be a **real** device. If it says `CABLE ...`, your Windows output is set to the cable.

### Why the output must not be a CABLE device

*CABLE Input* and *CABLE In 16ch* are two entrances to the **same** virtual cable. The app plays the phone
microphone into that cable. If Windows also plays PC sound into it, apps listening on *CABLE Output* get PC sound
mixed with (or instead of) your voice, and the phone also hears its own microphone. Result: the phone speaker
seems to work, but the microphone does not.

The app blocks this for *CABLE Input* (status: `PC sound source is the mic cable - choose another output device`),
but version 1.0.0 does **not** recognise *CABLE In 16ch* as the same cable, so that combination connects without a
warning. See [Known limitations](#known-limitations).

---

## Problem: no microphone sound

Work through these in order. Test after each step with **Win+R > `mmsys.cpl` > Recording tab**: talk into the
phone and watch the green bar next to **CABLE Output**.

1. **Windows output is a CABLE device.** Fix it as in [the correct setup](#the-correct-windows-sound-setup). This is
   the most common cause.
2. **Windows input is not CABLE Output**, or your calling app uses a different microphone. Select *CABLE Output*
   in both places.
3. **CABLE Output volume is low or muted.** Settings > Sound > Input > CABLE Output > volume about 100.
4. **Windows microphone privacy.** Settings > Privacy & security > Microphone: turn on *Microphone access*,
   *Let apps access your microphone* and *Let desktop apps access your microphone*. Windows treats CABLE Output as a
   real microphone.
5. **Phone microphone privacy toggle** (Android 12+). Swipe down quick settings and make sure the
   **Microphone access** tile is on. When it is off, every app records silence even though everything looks connected.
6. **Phone app permission.** Settings > Apps > Phone Audio Bridge > Permissions > Microphone must be
   *Allow only while using the app*. After changing it, tap **Stop** then **Start** in the phone app so the
   service restarts with microphone access.
7. **Mute microphone** is ticked in the phone app. Untick it.

---

## Problem: sound plays on both the PC and the phone

This is expected. The app copies whatever plays on your Windows output device, so that device keeps playing too.
Options:

- **Mute the PC speakers** (taskbar speaker icon). On most sound cards the copy sent to the phone is not
  affected. If the phone also goes silent, unmute and set the PC volume very low (2-5) instead.
- **Use a silent output device.** If your monitor has no speakers (or you turn them off), make the monitor the
  Windows output device and leave *PC sound source* on *Windows default output*. The phone gets the sound; the PC
  plays nothing.

Do **not** solve this by choosing a CABLE device as the output - that breaks the microphone (see above).

---

## Problem: call audio plays on the PC or monitor, not the phone

Windows has a separate **Default Communication Device**, and Teams, Zoom, WhatsApp and similar apps use it for
call audio. If it points at a monitor or another device, the other person's voice will not reach the phone.

Fix: Win+R > `mmsys.cpl` > **Playback** tab > right-click the same device you use as Windows output
(e.g. *Speakers / Headphones*) > **Set as Default Communication Device**. Or choose that speaker inside each
calling app.

---

## Problem: the phone is not detected

| Tray status | Fix |
|---|---|
| `adb.exe not found` | Download Google's SDK Platform-Tools, unzip, and put the `platform-tools` folder next to `PhoneAudioBridge.exe` - or tray menu > *Choose adb.exe...* |
| `Phone not detected` | Use a data cable (some are charge-only), turn on **USB debugging**, and set the phone's USB mode to anything except *No data transfer* |
| `Unlock the phone and tap Allow` | Unlock the phone and accept the USB debugging prompt (tick *Always allow from this computer*) |
| `open Phone Audio Bridge on the phone and tap Start` | The app is installed but stopped. Open it and tap **Start** |
| Samsung phone never appears | Install the Samsung USB driver, then re-plug |

Android only lets a microphone service start while its app is on screen, so if the phone app says
*Could not start*, unlock the phone, open the app and tap **Start**.

---

## How to read the status and the log

- **Tray icon colour:** green = connected, amber = waiting, grey = phone not found. Hover for the full status line.
- **Log:** tray menu > *Open log* (`%APPDATA%\PhoneAudioBridge\log.txt`). Each connection writes one line showing
  exactly which devices are in use, for example:

  ```
  Connected - speaker: CABLE In 16ch (VB-Audio Virtual Cable), mic: CABLE Input (VB-Audio Virtual Cable), music mode
  ```

  That line shows the classic mistake: the speaker source is a CABLE device, so the microphone will not work.
- **Phone app screen:** should say *Connected to PC - call mode* (or *music mode*).

---

## Known limitations

- *CABLE In 16ch* is not recognised as part of VB-CABLE by the safety check in 1.0.0 (see above).
- There is no live microphone level indicator yet; use the Windows Recording tab meter.
- Delay is roughly 0.1-0.2 s each way: fine for calls, noticeable for gaming.

---

## Still stuck?

[Open an issue](https://github.com/nicsoltani/phone-audio-bridge/issues) with the tray status text, the last
lines of the log, your Windows output and input devices, and your phone model and Android version.
