using System.Collections.ObjectModel;
using System.IO;
using Newtonsoft.Json.Linq;
using Terminalis.Models;

namespace Terminalis.Services;

public class SessionManager
{
    private const string GROUPS_FILE_NAME = "groups.json";

    private readonly ConfigurationManager _configManager;
    private readonly ObservableCollection<SessionConfiguration> _sessions;
    private readonly ObservableCollection<SessionGroup> _groups;

    public SessionManager(ConfigurationManager configManager)
    {
        _configManager = configManager;
        _sessions = new ObservableCollection<SessionConfiguration>();
        _groups = new ObservableCollection<SessionGroup>();
        LoadSessions();
        LoadGroups();
    }

    public ObservableCollection<SessionConfiguration> Sessions => _sessions;
    public ObservableCollection<SessionGroup> Groups => _groups;

    public void AddSession(SessionConfiguration session)
    {
        if (session.Order == 0)
        {
            var sessionsInGroup = _sessions.Where(s => s.Group == session.Group).ToList();
            session.Order = sessionsInGroup.Count > 0 ? sessionsInGroup.Max(s => s.Order) + 1 : 0;
        }
        _sessions.Add(session);
        SaveSessions();
    }

    public void UpdateSession(SessionConfiguration session)
    {
        var existing = _sessions.FirstOrDefault(s => s.Id == session.Id);
        if (existing != null)
        {
            var index = _sessions.IndexOf(existing);
            _sessions[index] = session;
            SaveSessions();
        }
    }

    public void DeleteSession(string sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session != null)
        {
            _sessions.Remove(session);
            SaveSessions();
        }
    }

    public SessionConfiguration? GetSession(string sessionId)
    {
        return _sessions.FirstOrDefault(s => s.Id == sessionId);
    }

    public void AddGroup(SessionGroup group)
    {
        if (group.Order == 0)
        {
            var groupsInParent = _groups.Where(g => g.ParentGroup == group.ParentGroup).ToList();
            group.Order = groupsInParent.Count > 0 ? groupsInParent.Max(g => g.Order) + 1 : 0;
        }
        _groups.Add(group);
        SaveGroups();
    }

    public void UpdateGroup(SessionGroup group)
    {
        var existing = _groups.FirstOrDefault(g => g.Id == group.Id);
        if (existing != null)
        {
            var index = _groups.IndexOf(existing);
            _groups[index] = group;
            SaveGroups();
        }
    }

    public void DeleteGroup(string groupId)
    {
        var group = _groups.FirstOrDefault(g => g.Id == groupId);
        if (group != null)
        {
            var sessionsInGroup = _sessions.Where(s => s.Group == groupId).ToList();
            foreach (var session in sessionsInGroup)
            {
                session.Group = null;
            }
            _groups.Remove(group);
            SaveGroups();
            SaveSessions();
        }
    }

    public SessionGroup? GetGroup(string groupId)
    {
        return _groups.FirstOrDefault(g => g.Id == groupId);
    }

    private void LoadSessions()
    {
        try
        {
            if (File.Exists(_configManager.SessionsFilePath))
            {
                var json = File.ReadAllText(_configManager.SessionsFilePath);
                var sessionsArray = JArray.Parse(json);
                bool needsMigration = false;
                
                foreach (var sessionJson in sessionsArray)
                {
                    if (sessionJson["Folder"] != null && sessionJson["Group"] == null)
                    {
                        sessionJson["Group"] = sessionJson["Folder"];
                        sessionJson["Folder"] = null;
                        needsMigration = true;
                    }
                }
                
                if (needsMigration)
                {
                    File.WriteAllText(_configManager.SessionsFilePath, sessionsArray.ToString());
                }
                
                var sessions = _configManager.LoadConfiguration<List<SessionConfiguration>>(_configManager.SessionsFilePath);
                if (sessions != null)
                {
                    foreach (var session in sessions)
                    {
                        if (session != null)
                        {
                            _sessions.Add(session);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load sessions: {ex.Message}");
        }
    }

    private void SaveSessions()
    {
        var sessionsList = _sessions.ToList();
        _configManager.SaveConfiguration(sessionsList, _configManager.SessionsFilePath);
    }

    private void LoadGroups()
    {
        try
        {
            var groupsPath = Path.Combine(_configManager.AppDataPath, GROUPS_FILE_NAME);
            var groups = _configManager.LoadConfiguration<List<SessionGroup>>(groupsPath);
            if (groups != null)
            {
                foreach (var group in groups)
                {
                    if (group != null)
                    {
                        _groups.Add(group);
                    }
                }
            }
            else
            {
                var oldFoldersPath = Path.Combine(_configManager.AppDataPath, "folders.json");
                if (File.Exists(oldFoldersPath))
                {
                    try
                    {
                        var json = File.ReadAllText(oldFoldersPath);
                        var oldFoldersArray = JArray.Parse(json);
                        foreach (var oldFolder in oldFoldersArray)
                        {
                            var group = new SessionGroup
                            {
                                Id = oldFolder["Id"]?.ToString() ?? Guid.NewGuid().ToString(),
                                Name = oldFolder["Name"]?.ToString() ?? string.Empty,
                                ParentGroup = oldFolder["ParentFolder"]?.ToString()
                            };
                            _groups.Add(group);
                        }
                        SaveGroups();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to migrate old folders: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load groups: {ex.Message}");
        }
    }

    private void SaveGroups()
    {
        var groupsPath = Path.Combine(_configManager.AppDataPath, GROUPS_FILE_NAME);
        var groupsList = _groups.ToList();
        _configManager.SaveConfiguration(groupsList, groupsPath);
    }
}

