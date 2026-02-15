using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Terminalis.Models;

namespace Terminalis.Services;

public class KnownHostsManager
{
    private const string KNOWN_HOSTS_FILE_NAME = "known_hosts.json";
    private readonly string _knownHostsFilePath;
    private Dictionary<string, KnownHostEntry>? _knownHosts;

    public KnownHostsManager(ConfigurationManager configManager)
    {
        _knownHostsFilePath = Path.Combine(configManager.AppDataPath, KNOWN_HOSTS_FILE_NAME);
        LoadKnownHosts();
    }

    private void LoadKnownHosts()
    {
        if (File.Exists(_knownHostsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_knownHostsFilePath);
                var entries = JsonConvert.DeserializeObject<List<KnownHostEntry>>(json);
                _knownHosts = entries?.ToDictionary(e => GetHostKey(e.Host, e.Port), e => e) ?? new Dictionary<string, KnownHostEntry>();
            }
            catch
            {
                _knownHosts = new Dictionary<string, KnownHostEntry>();
            }
        }
        else
        {
            _knownHosts = new Dictionary<string, KnownHostEntry>();
        }
    }

    private void SaveKnownHosts()
    {
        try
        {
            var entries = _knownHosts?.Values.ToList() ?? new List<KnownHostEntry>();
            var json = JsonConvert.SerializeObject(entries, Formatting.Indented);
            File.WriteAllText(_knownHostsFilePath, json);
        }
        catch
        {
        }
    }

    private static string GetHostKey(string host, int port)
    {
        return $"{host}:{port}";
    }

    public KnownHostEntry? GetKnownHost(string host, int port)
    {
        var key = GetHostKey(host, port);
        return _knownHosts?.TryGetValue(key, out var entry) == true ? entry : null;
    }

    public bool IsHostKnown(string host, int port, string fingerprint)
    {
        var entry = GetKnownHost(host, port);
        return entry != null && entry.Fingerprint == fingerprint;
    }

    public void AddKnownHost(string host, int port, string fingerprint, string keyAlgorithm)
    {
        if (_knownHosts == null)
        {
            _knownHosts = new Dictionary<string, KnownHostEntry>();
        }

        var key = GetHostKey(host, port);
        _knownHosts[key] = new KnownHostEntry
        {
            Host = host,
            Port = port,
            Fingerprint = fingerprint,
            KeyAlgorithm = keyAlgorithm
        };
        SaveKnownHosts();
    }

    public void RemoveKnownHost(string host, int port)
    {
        if (_knownHosts == null)
        {
            return;
        }

        var key = GetHostKey(host, port);
        if (_knownHosts.Remove(key))
        {
            SaveKnownHosts();
        }
    }
}

