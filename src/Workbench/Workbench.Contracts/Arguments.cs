namespace Workbench.Contracts;

public sealed class Arguments
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public Arguments(string[] arguments, string[] options, string[] flags)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            var name = arguments[index];
            string value;
            if (flags.Contains(name))
                value = "true";
            else if (options.Contains(name) && index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--"))
                value = arguments[++index];
            else
                throw new ArgumentException($"Unknown option or missing value: {name}");
            if (!values.TryAdd(name, value))
                throw new ArgumentException($"Duplicate option: {name}");
        }
    }

    public string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"Required option: {name}");
    public string? Optional(string name) => values.GetValueOrDefault(name);
    public bool Flag(string name) => values.ContainsKey(name);

    public int TimeoutSeconds()
    {
        var text = Optional("--timeout-seconds") ?? "1800";
        if (!int.TryParse(text, out var seconds) || seconds is < 1 or > 86400)
            throw new ArgumentException("--timeout-seconds must be between 1 and 86400.");
        return seconds;
    }
}
