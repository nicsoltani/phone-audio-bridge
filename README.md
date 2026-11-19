# Phone Audio Bridge

Use an Android phone as the **microphone and speaker** of a Windows 11 PC over a USB cable.

No Wi-Fi, no cloud, no account: audio goes over the USB cable through `adb`, so it is private and has low delay.

## Download

Get the latest build from the [Releases page](https://github.com/nicsoltani/phone-audio-bridge/releases):

| File | What it is |
|---|---|
| `PhoneAudioBridge.exe` | Windows tray app (self-contained, nothing to install) |
| `PhoneAudioBridge.apk` | Android app (Android 8.0 or newer) |

The full source for both is in [`src/`](src). Version 1.0.0 has been tested on Windows 11 with an Android phone
and VB-CABLE.

## How it works

```
Windows apps --> default playback device --(WASAPI loopback)--> PhoneAudioBridge.exe ==USB/adb==> phone speaker
Windows apps <-- "CABLE Output" mic <-- VB-CABLE <-- PhoneAudioBridge.exe <==USB/adb== phone microphone
```

- The link is `adb forward` to a private socket on the phone. Nothing listens on Wi-Fi or the network, and the
  phone app only accepts connections coming from adb, so no other app or device can tap the microphone.
- In **call mode** the phone runs as a speakerphone with its built-in echo canceller, so the PC sound coming
  out of the phone is removed from the microphone signal.

## One-time setup

**Phone**
1. Settings > About phone > tap *Build number* seven times to unlock Developer options.
2. Settings > Developer options > turn on **USB debugging**.

**PC**
1. **adb** - download Google's *SDK Platform-Tools for Windows* from
   <https://developer.android.com/tools/releases/platform-tools>, unzip it, and put the `platform-tools`
   folder next to `PhoneAudioBridge.exe` (it is a portable zip, not an installer). If adb is already on your
   PATH, skip this. You can also point the app at any `adb.exe` from the tray menu.
2. **VB-CABLE** - open Windows Sound settings and confirm that *CABLE Input* (playback) and *CABLE Output*
   (recording) exist. If not, install it from <https://vb-audio.com/Cable/>.
3. Run `PhoneAudioBridge.exe`. A round microphone icon appears in the system tray.
4. Plug the phone in. On the phone, tick *Always allow from this computer* and tap **Allow**.
5. Right-click the tray icon > **Install or update the phone app...**
6. The phone app opens by itself. Allow the microphone and notification permissions.

The tray icon turns **green** when the link is up (amber = waiting, grey = phone not found).
Hover over it for the exact status.

## Daily use

1. Plug in the phone and make sure `PhoneAudioBridge.exe` is running (tray menu > *Start with Windows* to
   make that automatic). The phone app is started for you.
2. In Teams / Zoom / WhatsApp / browser, choose the microphone **CABLE Output (VB-Audio Virtual Cable)**.
   To make it the default everywhere: Sound settings > Input > CABLE Output.
3. Leave the speaker setting on your normal Windows output device. Whatever plays there is copied to the
   phone, so turn the PC speakers down or mute them. On most sound cards the copy sent to the phone is
   unaffected by the Windows volume; if the phone goes quiet when you mute, leave the PC volume low instead.
4. Use the phone's volume buttons for loudness.

**Do not** set *CABLE Input* as the Windows output device - that would feed PC sound into the microphone.
The app refuses that combination and says so in the status line.

### Sound modes (on the phone)

| Mode | Use for | Notes |
|---|---|---|
| Call mode (default) | Meetings and calls | Echo cancellation and noise suppression; voice-grade sound |
| Music mode | Music and video | Full-quality stereo; no echo cancellation, so not for calls on speaker |

### Tray menu

- *Play PC sound on the phone* / *Use the phone as microphone* - switch either direction off.
- *PC sound source* - which playback device is copied to the phone (default: the Windows default output).
- *Microphone cable* - which virtual cable receives the phone microphone (default: VB-CABLE).
- *Reconnect and start the phone app*, *Open log*, *Start with Windows*, *Exit*.

Settings and the log are kept in `%APPDATA%\PhoneAudioBridge`.

## Troubleshooting

| Status / symptom | What to do |
|---|---|
| `adb.exe not found` | Put `platform-tools` next to the exe, or tray menu > *Choose adb.exe...* |
| `Phone not detected` | Check the cable (some are charge-only), USB debugging, and that the USB mode is not "No data transfer" |
| `Unlock the phone and tap Allow` | Accept the USB debugging prompt on the phone |
| `open Phone Audio Bridge on the phone and tap Start` | The phone app is installed but stopped - tap **Start** |
| `VB-CABLE not found, microphone off` | Install VB-CABLE, or pick the cable under *Microphone cable* |
| Phone app says "Could not start" | Unlock the phone, open the app and tap **Start** (Android only lets a microphone service start while the app is on screen) |
| The other party hears themselves | Use call mode, lower the phone volume a little |
| Occasional tiny click | The phone and PC clocks drift; the app drops a few milliseconds to keep delay low |
| Sound is delayed | Expected delay is roughly a tenth to a fifth of a second each way; fine for calls, noticeable for gaming |
| Samsung phone not seen by adb | Install the Samsung USB driver, then re-plug |
| No microphone sound | Windows output must be your real speakers, **not** any CABLE device (*CABLE Input* or *CABLE In 16ch*); Windows input must be *CABLE Output*. On the phone, check the *Microphone access* quick-settings tile and the app's microphone permission |
| Calls play on the PC, not the phone | Set your real speakers as the Windows *Default Communication Device* too (Sound control panel > Playback) |

## Building from source

**Android** (JDK 17+, Android SDK platform 35):

```
cd src/android
./gradlew assembleRelease        # gradlew.bat on Windows
# -> app/build/outputs/apk/release/app-release.apk
```

Release builds are signed only if `src/android/keystore.properties` exists (it is git-ignored, and so is any
`keystore/` folder). To sign your own build, create a keystore and copy
[`keystore.properties.example`](src/android/keystore.properties.example) to `keystore.properties` with your values.
Without it, use `./gradlew assembleDebug` for a debug-signed APK. An APK signed with a different key cannot be
installed over the official release without uninstalling it first.

**Windows** (.NET 8 SDK; cross-builds from Linux too):

```
cd src/windows
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
```

### Protocol v1 (little-endian)

```
PC    -> phone  hello: "PAB1" | u32 speakerSampleRate | u8 speakerChannels | u8 flags
phone -> PC     hello: "PAB1" | u32 micSampleRate     | u8 micChannels     | u8 mode (0 call, 1 music)
frames, both directions: u8 type | u16 length | payload     type 1 = PCM s16le, type 2 = keep-alive
```

Transport: `adb forward tcp:38127 localabstract:phoneaudiobridge`. Either side drops the link after
5 seconds of silence on the wire and the PC reconnects automatically.

## Contributing

Bug reports and pull requests are welcome - see [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

[MIT](LICENSE) © 2026 Nic Soltani. Third-party notices: [NAudio](https://github.com/naudio/NAudio) (MIT).
VB-CABLE and Android SDK Platform-Tools are separate downloads under their own licences and are not
included in this repository.

### Credits

Written from scratch; no code was copied from other projects. Design ideas came from
[AndroidMic](https://github.com/teamclouday/AndroidMic) (phone mic over adb) and
[Audio Share](https://github.com/mkckr0/audio-share) (PC sound to phone). Uses
[NAudio](https://github.com/naudio/NAudio) (MIT) on Windows.
