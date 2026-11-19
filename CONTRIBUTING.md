# Contributing to Phone Audio Bridge

Thanks for helping. Bug reports, fixes and new features are all welcome.

## Reporting a problem

Open an issue and include:

- Windows version, phone model and Android version
- The tray tooltip text (hover over the tray icon)
- The last lines of the log (tray menu > *Open log*, stored in `%APPDATA%\PhoneAudioBridge\log.txt`)
- Your Windows output and input devices (Settings > System > Sound)

## Making a change

1. Fork the repository and create a branch.
2. Build both sides (see *Building from source* in the README). For Android, `./gradlew assembleDebug`
   needs no signing key.
3. Test on real hardware: speaker direction, microphone direction, call mode and music mode.
4. If you change the wire format, update the protocol notes in the README, `Bridge.cs` and
   `AudioService.kt` together and bump the magic (`PAB1` -> `PAB2`).
5. Open a pull request describing what changed and how you tested it.

## Ground rules

- Keep the link USB/adb-only. Do not add network listeners; privacy of the microphone is a core feature.
- Never commit signing keys, `keystore.properties`, binaries or `platform-tools`.
- Contributions are accepted under the MIT licence.
