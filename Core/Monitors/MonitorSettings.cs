using System.Text.Json.Serialization;

namespace Thokolosi.Core.Monitors;

/// <summary>One background monitor (solar inverter, incubator, ...). Stored as a list in config.json.</summary>
public class MonitorSettings
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>"mqtt", "http_json" or "command".</summary>
    public string Kind { get; set; } = "mqtt";

    /// <summary>How often to poll (http_json/command) or evaluate rules (mqtt).</summary>
    public int IntervalSeconds { get; set; } = 60;

    // ---- mqtt ----
    public string Host { get; set; } = "";
    public int Port { get; set; } = 1883;
    public string Username { get; set; } = "";
    public string? PasswordProtected { get; set; }

    [JsonIgnore]
    public string? Password { get => Secret.Unprotect(PasswordProtected); set => PasswordProtected = Secret.Protect(value); }

    // ---- http_json ----
    public string Url { get; set; } = "";

    // ---- command ----
    /// <summary>PowerShell that prints JSON or name=value lines.</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Values to read. mqtt: Source = topic (+ optional Path into a JSON payload). http_json: Source = JSON path like
    /// "battery.soc" or "data[0].temp". command: Source = key or JSON path. For command, an empty list takes every value printed.
    /// </summary>
    public List<MonitorValue> Values { get; set; } = new();

    /// <summary>Alert rules, e.g. "battery_soc &lt; 30", "temp &gt; 38.5 for 5m", "pv_power == 0 between 10:00-15:00".</summary>
    public List<MonitorRule> Rules { get; set; } = new();

    /// <summary>Alert when no fresh data arrives for this many minutes (0 = never).</summary>
    public int OfflineAlertMinutes { get; set; } = 15;

    /// <summary>SolarAssistant (e.g. Luxpower inverter + LiFePO4) over MQTT. Enable MQTT in SolarAssistant's settings first.</summary>
    public static MonitorSettings SolarAssistantPreset(string host, int port = 1883, string username = "", string password = "")
    {
        var m = new MonitorSettings
        {
            Name = "Solar",
            Kind = "mqtt",
            Host = host,
            Port = port,
            Username = username,
            IntervalSeconds = 60,
            Values =
            {
                new() { Name = "battery_soc", Source = "solar_assistant/total/battery_state_of_charge/state", Unit = "%" },
                new() { Name = "pv_power", Source = "solar_assistant/inverter_1/pv_power/state", Unit = "W" },
                new() { Name = "load_power", Source = "solar_assistant/inverter_1/load_power/state", Unit = "W" },
                new() { Name = "battery_power", Source = "solar_assistant/total/battery_power/state", Unit = "W" },
                new() { Name = "grid_voltage", Source = "solar_assistant/inverter_1/grid_voltage/state", Unit = "V" },
            },
            Rules =
            {
                new() { When = "battery_soc < 25", Message = "Battery low - consider cutting heavy loads.", CooldownMinutes = 60 },
                new() { When = "battery_soc < 15", Message = "Battery critically low - switch off non-essential loads now.", Urgent = true, CooldownMinutes = 30 },
                new() { When = "pv_power == 0 between 10:00-15:00 for 15m", Message = "No solar production in daytime - check PV breaker/isolator or inverter faults.", CooldownMinutes = 120 },
                new() { When = "grid_voltage < 190 for 2m", Message = "Grid voltage low or grid off (load shedding?).", CooldownMinutes = 180 },
                new() { When = "grid_voltage > 255 for 2m", Message = "Grid voltage abnormally high.", CooldownMinutes = 120 },
            }
        };
        if (!string.IsNullOrEmpty(password)) m.Password = password;
        return m;
    }
}

public class MonitorValue
{
    public string Name { get; set; } = "";
    /// <summary>mqtt topic, or JSON path / key for http_json and command.</summary>
    public string Source { get; set; } = "";
    /// <summary>mqtt only: optional JSON path inside the payload.</summary>
    public string Path { get; set; } = "";
    public string Unit { get; set; } = "";
}

public class MonitorRule
{
    /// <summary>"value op number [between HH:mm-HH:mm] [for Nm]". Ops: &lt; &lt;= &gt; &gt;= == !=.</summary>
    public string When { get; set; } = "";
    /// <summary>Optional text added to the alert.</summary>
    public string Message { get; set; } = "";
    public bool Urgent { get; set; } = false;
    /// <summary>Minimum minutes between repeat alerts for this rule while it stays true.</summary>
    public int CooldownMinutes { get; set; } = 60;
    /// <summary>Send a "back to normal" message when the condition clears.</summary>
    public bool NotifyRecovery { get; set; } = true;
}
