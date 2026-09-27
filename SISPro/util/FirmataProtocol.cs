#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
namespace SISPro
{
/// <summary>
/// Firmata wire-protocol constants and pin modes, shared between the editor-only
/// discovery tool (FirmataCapabilityClient) and the runtime transport
/// (SerialManager's Firmata read/write path). Single source of truth so the two
/// never drift on a magic byte value.
///
/// Values are from the Firmata protocol spec (stable since Firmata 2.x) -
/// see https://github.com/firmata/protocol
/// </summary>
public static class FirmataProtocol
{
    public const byte START_SYSEX = 0xF0;
    public const byte END_SYSEX = 0xF7;
    public const byte REPORT_FIRMWARE = 0x79;
    public const byte CAPABILITY_QUERY = 0x6B;
    public const byte CAPABILITY_RESPONSE = 0x6C;
    public const byte ANALOG_MAPPING_QUERY = 0x69;
    public const byte ANALOG_MAPPING_RESPONSE = 0x6A;
    public const byte EXTENDED_ANALOG = 0x6F; // sysex: analog/PWM/servo write for pins > 15

    public const byte SET_PIN_MODE = 0xF4;
    public const byte SYSTEM_RESET = 0xFF;

    // Lower nibble of each of these three carries the port/channel/pin number (0-15).
    public const byte REPORT_DIGITAL_PORT = 0xD0;
    public const byte REPORT_ANALOG_PIN = 0xC0;
    public const byte DIGITAL_MESSAGE = 0x90;
    public const byte ANALOG_MESSAGE = 0xE0;
}

public enum FirmataPinMode : byte
{
    Input = 0, Output = 1, Analog = 2, Pwm = 3, Servo = 4,
    Shift = 5, I2C = 6, OneWire = 7, Stepper = 8, Encoder = 9, Serial = 10, Pullup = 11
}
}
#endif