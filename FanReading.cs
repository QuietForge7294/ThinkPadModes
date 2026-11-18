using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Automation;
internal sealed class FanReading
{
    private static readonly Regex Channel = new Regex(
        @"(?:CPU\s*Fan|Vitesse\s+du\s+ventilateur\s+de\s+l['’]UC)[^\r\n]*?[\(\[]\s*(?<name>Principal|Primary|Main|Secondaire|Secondary)\s*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Speed = new Regex(
        @"(?<![\d.,-])(?<speed>[0-9]{1,2}(?:[ \u00a0\u202f][0-9]{3})|[0-9]{1,5})\s*(?:RPM|tr/min)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal readonly int? Main;
    internal readonly int? Secondary;
    internal readonly DateTime Timestamp;
    internal FanReading(int? main, int? secondary)
    {
        Main = main;
        Secondary = secondary;
        Timestamp = DateTime.Now;
    }
    internal bool Any { get { return Main.HasValue || Secondary.HasValue; } }
    internal static FanReading Parse(string text)
    {
        int? main = null;
        int? secondary = null;
        var channels = Channel.Matches(text);
        for (int i = 0; i < channels.Count; i++)
        {
            Match channel = channels[i];
            int start = channel.Index + channel.Length;
            int limit = i + 1 < channels.Count ? channels[i + 1].Index : text.Length;
            string tail = text.Substring(start, Math.Min(limit - start, 60));
            Match value = Speed.Match(tail);
            if (!value.Success || !Regex.IsMatch(tail.Substring(0, value.Index), @"^[\s:=·–—]*$"))
                continue;
            int rpm = int.Parse(Regex.Replace(value.Groups["speed"].Value, @"\s", ""), CultureInfo.InvariantCulture);
            string name = channel.Groups["name"].Value.ToLowerInvariant();
            if (name == "secondaire" || name == "secondary")
                secondary = rpm;
            else
                main = rpm;
        }
        return new FanReading(main, secondary);
    }
    internal static FanReading ReadLenovo()
    {
        var processes = Process.GetProcessesByName("NextGenLenovoDiagnostics");
        try
        {
            foreach (Process process in processes)
            {
                IntPtr window = process.MainWindowHandle;
                if (window == IntPtr.Zero)
                    continue;
                var root = AutomationElement.FromHandle(window);
                if (root == null)
                    continue;
                var properties = new CacheRequest();
                properties.Add(AutomationElement.NameProperty);
                properties.Add(AutomationElement.IsOffscreenProperty);
                AutomationElementCollection elements;
                using (properties.Activate())
                    elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                var text = new System.Text.StringBuilder();
                foreach (AutomationElement element in elements)
                {
                    if (!element.Cached.IsOffscreen && !string.IsNullOrWhiteSpace(element.Cached.Name))
                        text.AppendLine(element.Cached.Name);
                }
                FanReading reading = Parse(text.ToString());
                if (reading.Any)
                    return reading;
            }
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
        return new FanReading(null, null);
    }
}
