using System.Collections.Generic;
using System.Linq;

namespace SISPro
{
/// <summary>
/// Orders serial port names most-likely-Arduino first, and drops ones that are never
/// an Arduino over USB but can block for seconds on open (Bluetooth serial, and the
/// macOS tty.* twin of a cu.* device).
///
/// Used by SerialManager when Port Name is left blank. Picking ports[0] used to
/// select COM1 - usually a legacy motherboard port - on many Windows PCs.
/// No System.IO.Ports dependency, so this compiles on every platform.
/// </summary>
public static class SerialPortRanking
{
    public static List<string> Rank(string[] ports)
    {
        var candidates = new List<string>();
        if (ports == null) return candidates;

        foreach (var port in ports)
        {
            if (string.IsNullOrEmpty(port)) continue;
            string lower = port.ToLowerInvariant();
            if (lower.Contains("bluetooth")) continue;

            // macOS lists each device twice: tty.* (waits for carrier detect on open)
            // and cu.* (opens immediately). Keep only the cu.* twin.
            if (lower.StartsWith("/dev/tty.") &&
                ports.Contains("/dev/cu." + port.Substring("/dev/tty.".Length)))
                continue;

            candidates.Add(port);
        }

        return candidates.OrderBy(Score).ToList(); // OrderBy is stable: OS order kept within a score
    }

    static int Score(string port)
    {
        string l = port.ToLowerInvariant();
        if (l.Contains("usbmodem") || l.Contains("usbserial") || l.Contains("wchusbserial") ||
            l.Contains("ttyacm") || l.Contains("ttyusb"))
            return 0;               // mac/linux names for USB serial adapters
        if (l == "com1") return 2;  // on Windows usually a legacy motherboard port
        return 1;
    }
}
}
