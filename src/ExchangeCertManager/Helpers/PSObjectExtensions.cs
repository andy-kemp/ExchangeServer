using System.Collections;
using System.Management.Automation;

namespace ExchangeCertManager.Helpers;

/// <summary>
/// Reads properties from deserialised Exchange objects. Everything that comes back over remoting is a
/// property bag, so values are read by name and converted defensively.
/// </summary>
public static class PSObjectExtensions
{
    public static object? Raw(this PSObject obj, string name)
    {
        var value = obj.Properties[name]?.Value;
        return value is PSObject pso ? pso.BaseObject : value;
    }

    public static string Str(this PSObject obj, string name)
    {
        var value = obj.Raw(name);
        if (value is null) return string.Empty;
        if (value is string s) return s;
        if (value is IEnumerable e) return string.Join(", ", e.Cast<object?>().Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)));
        return value.ToString() ?? string.Empty;
    }

    public static List<string> List(this PSObject obj, string name)
    {
        var value = obj.Raw(name);
        return value switch
        {
            null => new List<string>(),
            string s => s.Length == 0 ? new List<string>() : new List<string> { s },
            IEnumerable e => e.Cast<object?>()
                .Select(x => (x is PSObject p ? p.BaseObject : x)?.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList(),
            _ => new List<string> { value.ToString()! },
        };
    }

    public static bool Bool(this PSObject obj, string name)
    {
        var value = obj.Raw(name);
        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var r) && r,
            _ => false,
        };
    }

    public static DateTime? Date(this PSObject obj, string name)
    {
        var value = obj.Raw(name);
        return value switch
        {
            DateTime d => d,
            string s when DateTime.TryParse(s, out var d) => d,
            _ => null,
        };
    }

    public static byte[]? Bytes(this PSObject obj, string name)
    {
        var value = obj.Raw(name);
        return value switch
        {
            byte[] b => b,
            IEnumerable e => e.Cast<object>().Select(Convert.ToByte).ToArray(),
            _ => null,
        };
    }

    /// <summary>Hostname from an Exchange URL property (Uri or string), or null.</summary>
    public static string? Host(this PSObject obj, string name)
    {
        var text = obj.Str(name);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)) return uri.Host.ToLowerInvariant();
        return text.Trim().ToLowerInvariant();
    }
}
