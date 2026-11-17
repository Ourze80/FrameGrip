using System.Globalization;

namespace FrameGrip.Core;

/// <summary>Trasforma tipo e valore di un sensore in testo leggibile.</summary>
public static class SensorFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Come Format(tipo, valore), ma con il testo delle voci che non sono numeri (API, driver).</summary>
    public static (string Value, string Unit) Format(SensorReading r) =>
        r.Text != null ? (r.Text, "") : r.Type is "Api" or "Driver" ? ("—", "") : Format(r.Type, r.Value);

    public static (string Value, string Unit) Format(string type, float? value)
    {
        if (value is not { } v || float.IsNaN(v) || float.IsInfinity(v)) return ("—", UnitOf(type));

        switch (type)
        {
            case "Throughput": // byte al secondo
                return v >= 1024 * 1024
                    ? ((v / (1024 * 1024)).ToString("0.0", Inv), "MB/s")
                    : ((v / 1024).ToString("0", Inv), "KB/s");
            case "Voltage": return (v.ToString("0.000", Inv), "V");
            case "Current": return (v.ToString("0.0", Inv), "A");
            case "Power": return (v.ToString(v < 10 ? "0.0" : "0", Inv), "W");
            case "Data": return (v.ToString("0.0", Inv), "GB");
            case "Factor": return (v.ToString("0.00", Inv), "");
            case "Session":   // secondi di gioco
                var t = TimeSpan.FromSeconds(Math.Max(0, v));
                return ($"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}", "");
            default: return (v.ToString("0", Inv), UnitOf(type));
        }
    }

    private static string UnitOf(string type) => type switch
    {
        "Temperature" => "°C",
        "Load" or "Control" or "Level" or "Humidity" => "%",
        "Power" => "W",
        "Fan" => "RPM",
        "Clock" => "MHz",
        "Voltage" => "V",
        "Current" => "A",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "KB/s",
        "Frequency" => "Hz",
        "Flow" => "L/h",
        "TimeSpan" => "s",
        "Energy" => "mWh",
        "Noise" => "dBA",
        "MemSpeed" => "MT/s",
        _ => "",
    };

    public static string TypeName(string type) => type switch
    {
        "Temperature" => "Temperature",
        "Load" => "Load",
        "Power" => "Power",
        "Fan" => "Fan",
        "Control" => "Fan control",
        "Clock" => "Frequency",
        "Voltage" => "Voltage",
        "Current" => "Current",
        "Data" or "SmallData" => "Memory",
        "Throughput" => "Data rate",
        "Level" => "Level",
        "Frequency" => "Frequency",
        "Flow" => "Flow",
        "Factor" => "Factor",
        "TimeSpan" => "Time",
        "Energy" => "Energy",
        "Noise" => "Noise",
        "Humidity" => "Humidity",
        "Api" => "Graphics API",
        "Session" => "Play time",
        "Driver" => "Driver",
        "MemSpeed" => "Memory speed",
        _ => type,
    };

    public static int TypeOrder(string type) => type switch
    {
        "Load" => 0, "Temperature" => 1, "Power" => 2, "Data" => 3, "SmallData" => 4,
        "Clock" => 5, "Fan" => 6, "Control" => 7, "Voltage" => 8, "Current" => 9,
        "Throughput" => 10, _ => 20,
    };
}
