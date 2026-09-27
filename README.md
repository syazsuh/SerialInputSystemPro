# SISPro: Arduino / Serial Input Toolkit for Unity

Connect Arduino boards to Unity's **Input System** over serial. SISPro detects your board's pins, lets you choose what each pin does, and generates the device layout and Input Actions for you. Buttons, sensors and potentiometers then show up in Unity like any other controller, and you can drive LEDs, PWM outputs and servos from Unity too.

## Features

- **Set Up Arduino window**: connect to a board running StandardFirmata, scan its pins, and assign each one a role (Digital In, Analog In, Digital Out, PWM Out, Servo Out, or Unused).
- **One-click profile + Input Actions**: creates a `SerialDeviceProfile` and a matching `.inputactions` asset with bindings for exactly the pins you picked.
- **Real Input System devices**: each board is registered as its own device, so you can bind to it in the Input Actions editor without entering Play Mode.
- **Multiple boards**: several boards can run side by side, even with the same firmware, without their inputs merging.
- **Outputs**: drive digital, PWM and servo pins from Unity.
- **Setup checks**: warns about project settings that silently break serial ports, with a one-click fix.
- **Debugging tools**: serial input monitor and input debugger.
- **Advanced tools**: Input Actions Wizard and a code generator for a custom `InputDevice` class plus a matching Arduino sketch.

## Requirements

- Unity with the **Input System** package installed
- **Desktop only**: Windows, macOS or Linux (Editor or standalone builds). WebGL, mobile and consoles have no serial port access.
- **API Compatibility Level** set to **.NET Framework** (SISPro checks this and can fix it for you)
- An Arduino (Uno, Nano, Mega, etc.) flashed with **StandardFirmata** (Arduino IDE → File → Examples → Firmata → StandardFirmata)

## Installation

**Option A: .unitypackage (easiest)**
1. Download the latest `.unitypackage` from the [Releases](../../releases) page.
2. In Unity: Assets → Import Package → Custom Package… and select the file.

**Option B: from source**
1. Clone or download this repository.
2. Copy the `SISPro` folder (with its `.meta` files) into your project's `Assets/` folder.

## Quick start

1. Flash your Arduino with **StandardFirmata** and plug it in.
2. In Unity, open **Tools → SISPro → Control Panel** and make sure the setup checks are green.
3. Click **Set Up Arduino** (or **Tools → SISPro → Set Up Arduino**).
4. Pick your serial port, connect, and scan the board.
5. Choose a role for each pin you're using.
6. Apply. SISPro creates the device profile and its Input Actions asset.
7. Use the generated actions like any other Input System actions in your scripts or UI.

## Menu reference

| Menu | What it does |
|---|---|
| Tools → SISPro → Control Panel | Setup checks and entry point to everything |
| Tools → SISPro → Set Up Arduino | Connect, scan pins, assign roles, generate profile + actions |
| Tools → SISPro → Advanced → Input Actions Wizard | Regenerate actions for a profile into a specific asset, action map or control scheme |
| Tools → SISPro → Generate SerialDevice + Arduino IDE Sketch | Generate a custom device class and matching sketch (advanced) |

## Troubleshooting

- **Board won't connect**: check that API Compatibility Level is .NET Framework (Control Panel shows this), that no other program (such as the Arduino Serial Monitor) has the port open, and that the baud rate matches your firmware (StandardFirmata uses 57600).
- **Device doesn't appear in the binding picker**: re-open the Input Actions asset after the profile is created; layouts are registered on every script reload.
- **Analog pin skipped**: click **Re-scan Board** so SISPro can learn the board's analog channel mapping.

## License

SISPro is licensed under the **GNU General Public License v3.0 (GPL-3.0)**. See [LICENSE](LICENSE) for the full text.

In short: you're free to use, study, modify and share SISPro. If you distribute SISPro or software that includes or is derived from it, you must make that software's source code available under the same GPL-3.0 license and keep the copyright and license notices intact.
