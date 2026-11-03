using Tmds.DBus.Protocol;

namespace Roamwise.Linux.Platform;

/// <summary>Writes a method call's arguments.</summary>
public delegate void BodyWriter(ref MessageWriter writer);

/// <summary>
/// A thin layer over one D-Bus connection: property reads and method calls by name, the way the
/// NetworkManager, wpa_supplicant, Secret Service and notification APIs are used here.
/// </summary>
public sealed class Bus : IDisposable
{
    private readonly Connection _connection;

    private Bus(Connection connection) => _connection = connection;

    /// <summary>ROAMWISE_SYSTEM_BUS overrides the system bus address (for testing against mock services).</summary>
    public static async Task<Bus> SystemAsync() =>
        await OpenAsync(Environment.GetEnvironmentVariable("ROAMWISE_SYSTEM_BUS") is { Length: > 0 } a ? a : Address.System!);

    public static async Task<Bus> SessionAsync() => await OpenAsync(Address.Session!);

    private static async Task<Bus> OpenAsync(string address)
    {
        var connection = new Connection(address);
        await connection.ConnectAsync();
        return new Bus(connection);
    }

    public Connection Connection => _connection;

    public Task<VariantValue> GetAsync(string dest, string path, string iface, string property)
    {
        using var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(dest, path, "org.freedesktop.DBus.Properties", "Get", "ss");
        w.WriteString(iface);
        w.WriteString(property);
        return _connection.CallMethodAsync(w.CreateMessage(), static (m, _) => m.GetBodyReader().ReadVariantValue());
    }

    public Task<Dictionary<string, VariantValue>> GetAllAsync(string dest, string path, string iface)
    {
        using var w = _connection.GetMessageWriter();
        w.WriteMethodCallHeader(dest, path, "org.freedesktop.DBus.Properties", "GetAll", "s");
        w.WriteString(iface);
        return _connection.CallMethodAsync(w.CreateMessage(), static (m, _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue());
    }

    public Task CallAsync(string dest, string path, string iface, string member, string? signature = null, BodyWriter? body = null) =>
        _connection.CallMethodAsync(Build(dest, path, iface, member, signature, body));

    public Task<T> CallAsync<T>(string dest, string path, string iface, string member, string? signature, BodyWriter? body, MessageValueReader<T> reader) =>
        _connection.CallMethodAsync(Build(dest, path, iface, member, signature, body), reader);

    private MessageBuffer Build(string dest, string path, string iface, string member, string? signature, BodyWriter? body)
    {
        var w = _connection.GetMessageWriter();
        try
        {
            w.WriteMethodCallHeader(dest, path, iface, member, signature);
            body?.Invoke(ref w);
            return w.CreateMessage();
        }
        finally { w.Dispose(); }
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Typed reads from a property dictionary, tolerant of missing keys (older services lack newer properties).</summary>
public static class Props
{
    public static string Str(this Dictionary<string, VariantValue> p, string key) =>
        p.TryGetValue(key, out var v) ? Unwrap(v) is { Type: VariantValueType.String or VariantValueType.ObjectPath } s ? s.Type == VariantValueType.String ? s.GetString() : s.GetObjectPathAsString() : "" : "";

    public static string Path(this Dictionary<string, VariantValue> p, string key) => p.Str(key);

    public static long Num(this Dictionary<string, VariantValue> p, string key, long fallback = 0) =>
        p.TryGetValue(key, out var v) ? Num(v, fallback) : fallback;

    public static long Num(VariantValue v, long fallback = 0)
    {
        v = Unwrap(v);
        return v.Type switch
        {
            VariantValueType.Byte => v.GetByte(),
            VariantValueType.Int16 => v.GetInt16(),
            VariantValueType.UInt16 => v.GetUInt16(),
            VariantValueType.Int32 => v.GetInt32(),
            VariantValueType.UInt32 => v.GetUInt32(),
            VariantValueType.Int64 => v.GetInt64(),
            VariantValueType.UInt64 => (long)v.GetUInt64(),
            VariantValueType.Bool => v.GetBool() ? 1 : 0,
            _ => fallback,
        };
    }

    public static bool Bool(this Dictionary<string, VariantValue> p, string key) => p.Num(key) != 0;

    public static byte[] Bytes(this Dictionary<string, VariantValue> p, string key) =>
        p.TryGetValue(key, out var v) ? Bytes(v) : [];

    public static byte[] Bytes(VariantValue v)
    {
        v = Unwrap(v);
        return v.Type == VariantValueType.Array && v.ItemType == VariantValueType.Byte ? v.GetArray<byte>() : [];
    }

    /// <summary>An array of object paths ("ao").</summary>
    public static List<string> Paths(this Dictionary<string, VariantValue> p, string key) =>
        p.TryGetValue(key, out var v) ? Paths(v) : [];

    public static List<string> Paths(VariantValue v)
    {
        v = Unwrap(v);
        var list = new List<string>();
        if (v.Type != VariantValueType.Array) return list;
        for (int i = 0; i < v.Count; i++)
        {
            var item = v.GetItem(i);
            list.Add(item.Type == VariantValueType.ObjectPath ? item.GetObjectPathAsString() : item.Type == VariantValueType.String ? item.GetString() : "");
        }
        return list;
    }

    public static VariantValue Unwrap(VariantValue v)
    {
        while (v.Type == VariantValueType.Variant) v = v.GetVariantValue();
        return v;
    }
}
