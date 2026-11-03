using System.Net.Sockets;
using System.Text;
using Roamwise.Core;
using Tmds.DBus.Protocol;

namespace Roamwise.Linux.Platform;

/// <summary>
/// Controller keys go in the desktop keyring (GNOME Keyring on Ubuntu and Mint) through the Secret Service API;
/// settings.json only holds "keyring:&lt;id&gt;". Without a keyring, they fall back to a file only you can read.
/// </summary>
public sealed class KeyringSecretStore : ISecretStore
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string DefaultCollection = "/org/freedesktop/secrets/aliases/default";
    private const string NoPrompt = "/";

    private bool _usedFile;

    public string Description => _usedFile
        ? "Stored in a file only your account can read (no desktop keyring was found). Roamwise only reads device names."
        : "Stored in your desktop keyring. Roamwise only reads device names.";

    private static string FileFolder => Path.Combine(SettingsStore.DataFolder, "secrets");

    public async Task<string> SaveAsync(string secret)
    {
        string id = Guid.NewGuid().ToString("N");
        try
        {
            using var bus = await Bus.SessionAsync();
            string session = await OpenSessionAsync(bus);
            var (_, prompt) = await bus.CallAsync(Service, DefaultCollection, "org.freedesktop.Secret.Collection", "CreateItem", "a{sv}(oayays)b",
                (ref MessageWriter w) =>
                {
                    var props = w.WriteDictionaryStart();
                    w.WriteDictionaryEntryStart();
                    w.WriteString("org.freedesktop.Secret.Item.Label");
                    w.WriteVariantString("Roamwise controller key");
                    w.WriteDictionaryEntryStart();
                    w.WriteString("org.freedesktop.Secret.Item.Attributes");
                    w.WriteVariant(new Dict<string, string> { ["application"] = "roamwise", ["roamwise-id"] = id }.AsVariantValue());
                    w.WriteDictionaryEnd(props);
                    WriteSecret(ref w, session, secret);
                    w.WriteBool(true);
                },
                static (m, _) =>
                {
                    var r = m.GetBodyReader();
                    return (r.ReadObjectPathAsString(), r.ReadObjectPathAsString());
                });
            // A locked keyring would need an unlock prompt; use the file instead of popping one up unasked.
            if (prompt == NoPrompt) return "keyring:" + id;
        }
        catch (Exception ex) when (IsUnavailable(ex)) { }

        _usedFile = true;
        Directory.CreateDirectory(FileFolder);
        string path = Path.Combine(FileFolder, id);
        await File.WriteAllTextAsync(path, "");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await File.WriteAllTextAsync(path, secret);
        return "file:" + id;
    }

    public async Task<string?> ReadAsync(string stored)
    {
        if (stored.StartsWith("file:"))
        {
            string path = Path.Combine(FileFolder, stored[5..]);
            return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        }
        if (!stored.StartsWith("keyring:")) return null;
        try
        {
            using var bus = await Bus.SessionAsync();
            var items = await FindAsync(bus, stored[8..]);
            if (items.Count == 0) return null;
            string session = await OpenSessionAsync(bus);
            var secrets = await bus.CallAsync(Service, ServicePath, "org.freedesktop.Secret.Service", "GetSecrets", "aoo",
                (ref MessageWriter w) =>
                {
                    w.WriteArray(items.Select(i => new ObjectPath(i)).ToArray());
                    w.WriteObjectPath(session);
                },
                static (m, _) =>
                {
                    // a{o(oayays)}: item → (session, parameters, value, content type)
                    var r = m.GetBodyReader();
                    var end = r.ReadDictionaryStart();
                    var values = new List<byte[]>();
                    while (r.HasNext(end))
                    {
                        r.ReadObjectPathAsString();
                        r.AlignStruct();
                        r.ReadObjectPathAsString();
                        r.ReadArrayOfByte();
                        values.Add(r.ReadArrayOfByte());
                        r.ReadString();
                    }
                    return values;
                });
            return secrets.Count > 0 ? Encoding.UTF8.GetString(secrets[0]) : null;
        }
        catch (Exception ex) when (IsUnavailable(ex)) { return null; }
    }

    public async Task ForgetAsync(string stored)
    {
        if (stored.StartsWith("file:"))
        {
            File.Delete(Path.Combine(FileFolder, stored[5..]));
            return;
        }
        if (!stored.StartsWith("keyring:")) return;
        try
        {
            using var bus = await Bus.SessionAsync();
            foreach (var item in await FindAsync(bus, stored[8..]))
                await bus.CallAsync(Service, item, "org.freedesktop.Secret.Item", "Delete", null, null, static (m, _) => m.GetBodyReader().ReadObjectPathAsString());
        }
        catch (Exception ex) when (IsUnavailable(ex)) { }
    }

    /// <summary>Unlocked items with our id; locked ones are skipped rather than prompting.</summary>
    private static Task<List<string>> FindAsync(Bus bus, string id) =>
        bus.CallAsync(Service, ServicePath, "org.freedesktop.Secret.Service", "SearchItems", "a{ss}",
            (ref MessageWriter w) =>
            {
                var dict = w.WriteDictionaryStart();
                foreach (var (k, v) in new[] { ("application", "roamwise"), ("roamwise-id", id) })
                {
                    w.WriteDictionaryEntryStart();
                    w.WriteString(k);
                    w.WriteString(v);
                }
                w.WriteDictionaryEnd(dict);
            },
            static (m, _) =>
            {
                var r = m.GetBodyReader();
                return r.ReadArrayOfObjectPath().Select(p => p.ToString()).ToList();
            });

    /// <summary>A "plain" session: the secret crosses the local session bus unencrypted, which only your own processes can see.</summary>
    private static Task<string> OpenSessionAsync(Bus bus) =>
        bus.CallAsync(Service, ServicePath, "org.freedesktop.Secret.Service", "OpenSession", "sv",
            (ref MessageWriter w) =>
            {
                w.WriteString("plain");
                w.WriteVariantString("");
            },
            static (m, _) =>
            {
                var r = m.GetBodyReader();
                r.ReadVariantValue();
                return r.ReadObjectPathAsString();
            });

    private static void WriteSecret(ref MessageWriter w, string session, string secret)
    {
        w.WriteStructureStart();
        w.WriteObjectPath(session);
        w.WriteArray(Array.Empty<byte>());
        w.WriteArray(Encoding.UTF8.GetBytes(secret));
        w.WriteString("text/plain; charset=utf8");
    }

    private static bool IsUnavailable(Exception ex) =>
        ex is DBusException or ConnectException or IOException or SocketException or DisconnectedException or ArgumentException or InvalidOperationException;
}
