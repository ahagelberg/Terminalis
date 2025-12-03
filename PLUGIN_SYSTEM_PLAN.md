# Plugin System Project Plan

## 1. Overview

This document outlines the plan for implementing a comprehensive plugin system for TabbySSH that allows third-party developers to extend functionality through C# plugins. The system will support multiple plugin types including terminal replacements, input/output transformers, and protocol handlers.

### Goals
- Clean, intuitive API for plugin developers
- Easy plugin installation (single DLL in plugins folder)
- Per-session plugin configuration (primary use case - most plugins are session-specific)
- Global plugin configuration (optional capability for future use)
- JSON-based configuration storage
- Support for multiple plugin types and combinations
- Secure plugin isolation

---

## 2. Architecture Design

### 2.1 Core Components

```
┌─────────────────────────────────────────────────────────────┐
│                    Main Application                         │
├─────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐    │
│  │ Plugin       │  │ Session      │  │ Connection   │    │
│  │ Manager      │  │ Manager      │  │ Manager      │    │
│  └──────────────┘  └──────────────┘  └──────────────┘    │
└─────────────────────────────────────────────────────────────┘
         │                    │                    │
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────────────────────────────────────────────────────┐
│                    Plugin System                             │
├─────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐    │
│  │ Plugin       │  │ Plugin       │  │ Plugin       │    │
│  │ Loader       │  │ Registry     │  │ Context      │    │
│  └──────────────┘  └──────────────┘  └──────────────┘    │
└─────────────────────────────────────────────────────────────┘
         │                    │                    │
         │                    │                    │
         ▼                    ▼                    ▼
┌─────────────────────────────────────────────────────────────┐
│                    Plugin Interfaces                        │
├─────────────────────────────────────────────────────────────┤
│  IPlugin, ITerminalReplacement, IInputTransformer,         │
│  IOutputTransformer, IProtocolHandler, IConfigProvider      │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 Data Flow with Plugins

**Standard Flow (No Plugins):**
```
Server → ITerminalConnection → TerminalEmulator → User
User → TerminalEmulator → ITerminalConnection → Server
```

**With Input Transformer Plugin:**
```
User → TerminalEmulator → [Input Transformer Plugin] → ITerminalConnection → Server
```

**With Output Transformer Plugin:**
```
Server → ITerminalConnection → [Output Transformer Plugin] → TerminalEmulator → User
```

**With Terminal Replacement Plugin (Session-Specific):**
```
Server → ITerminalConnection → [Terminal Replacement Plugin] → Custom UI
User → Custom UI → [Terminal Replacement Plugin] → ITerminalConnection → Server
```
**Note:** Only the session configured with the terminal replacement plugin uses this flow. Other sessions continue to use the standard TerminalEmulator flow.

**With Protocol Handler Plugin:**
```
User → TerminalEmulator → [Protocol Handler Plugin] → Custom Protocol → Server
```

### 2.3 Plugin Discovery and Loading

- Plugins stored in `{AppData}/TabbySSH/Plugins/` directory
- Each plugin is a single DLL file
- Plugin metadata stored in `plugin.json` (optional, can be embedded in DLL)
- Plugins loaded on application startup
- Lazy loading: plugins only instantiated when needed
- Plugin dependencies handled via standard .NET assembly resolution

---

## 3. API Design

### 3.1 Core Interfaces

#### IPlugin (Base Interface)
```csharp
namespace TabbySSH.Plugins;

public interface IPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    string Description { get; }
    string Author { get; }
    
    PluginCapabilities Capabilities { get; }
    
    Task Initialize(IPluginContext context);
    Task Shutdown();
    
    bool IsEnabled { get; set; }
}
```

#### IPluginContext
```csharp
namespace TabbySSH.Plugins;

public interface IPluginContext
{
    ILogger Logger { get; }
    IConfigurationManager Configuration { get; }
    IApplicationInfo Application { get; }
    
    Task<string> ReadResource(string resourceName);
    Task SaveResource(string resourceName, string content);
}
```

#### PluginCapabilities (Flags Enum)
```csharp
[Flags]
public enum PluginCapabilities
{
    None = 0,
    TerminalReplacement = 1 << 0,
    InputTransformation = 1 << 1,
    OutputTransformation = 1 << 2,
    ProtocolHandler = 1 << 3,
    ConfigProvider = 1 << 4,
    GlobalConfig = 1 << 5 // Optional, for future global plugins
}
```

**Note:** Most plugins operate on a per-session basis. The `GlobalConfig` capability exists for future use cases but is not the primary design focus.

### 3.2 Terminal Replacement Plugin

**Note:** Terminal replacement plugins are configured per-session. When a session is configured to use a terminal replacement plugin, only that session's tab will use the plugin UI instead of the standard `TerminalEmulator`. Other sessions continue to use the standard terminal.

```csharp
namespace TabbySSH.Plugins;

public interface ITerminalReplacement : IPlugin
{
    UIElement CreateTerminalView(ITerminalConnection connection, IPluginContext context);
    
    Task OnConnectionEstablished(ITerminalConnection connection);
    Task OnConnectionClosed(ITerminalConnection connection);
    
    Task OnDataReceived(string data, bool isRaw);
    Task<string> OnUserInput(string input);
}
```
<｜tool▁calls▁begin｜><｜tool▁call▁begin｜>
read_file

### 3.3 Input Transformer Plugin

```csharp
namespace TabbySSH.Plugins;

public interface IInputTransformer : IPlugin
{
    Task<string> TransformInput(string input, ITerminalConnection connection);
    
    int Priority { get; } // Lower = earlier in chain
}
```

### 3.4 Output Transformer Plugin

```csharp
namespace TabbySSH.Plugins;

public interface IOutputTransformer : IPlugin
{
    Task<string> TransformOutput(string output, bool isRaw, ITerminalConnection connection);
    
    int Priority { get; } // Lower = earlier in chain
}
```

### 3.5 Protocol Handler Plugin

```csharp
namespace TabbySSH.Plugins;

public interface IProtocolHandler : IPlugin
{
    string ProtocolName { get; }
    bool CanHandle(SessionConfiguration config);
    
    Task<ITerminalConnection> CreateConnection(SessionConfiguration config);
}
```

### 3.6 Configuration Provider Plugin

**Note:** Most plugins operate on a per-session basis. The `IConfigProvider` interface allows plugins to add configuration sections to the session configuration dialog. Global configuration support exists for future use cases, but the primary focus is on per-session plugin configuration.

```csharp
namespace TabbySSH.Plugins;

public interface IConfigProvider : IPlugin
{
    IEnumerable<ConfigSection> GetSessionConfigSections();
    IEnumerable<ConfigSection> GetGlobalConfigSections(); // Optional, for future global plugins
    
    Task LoadConfig(JsonObject config, bool isGlobal);
    Task<JsonObject> SaveConfig(bool isGlobal);
}
```

### 3.7 Helper Interfaces

#### ITerminalConnection (Extended for Plugins)
```csharp
// Existing interface extended with plugin-friendly methods
public interface ITerminalConnection : IDisposable
{
    // ... existing members ...
    
    // New for plugins:
    Task<string> ReadRawData(TimeSpan timeout);
    Task WriteRawData(byte[] data);
    event EventHandler<RawDataEventArgs>? RawDataReceived;
}
```

#### ILogger
```csharp
namespace TabbySSH.Plugins;

public interface ILogger
{
    void LogDebug(string message);
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
}
```

#### IConfigurationManager
```csharp
namespace TabbySSH.Plugins;

public interface IConfigurationManager
{
    Task<JsonObject> GetSessionConfig(string sessionId);
    Task SaveSessionConfig(string sessionId, JsonObject config);
    Task<JsonObject> GetGlobalConfig(); // Optional, for future global plugins
    Task SaveGlobalConfig(JsonObject config); // Optional, for future global plugins
}
```

**Note:** Most plugin configurations are session-specific. Plugins access configuration through the session context. Global configuration methods are available for future use cases but are not the primary focus.

---

## 4. Plugin Types and Use Cases

**Important:** Most plugins operate on a per-session basis. Each session can have its own set of plugins configured. Global plugins are supported by the API for future use cases, but the primary design focus is on per-session plugins.

### 4.1 Terminal Replacement Plugins
**Purpose:** Replace the standard terminal emulator with custom UI

**Scope:** Per-session (each session can have its own terminal replacement)

**Example Use Cases:**
- System monitoring dashboard
- Database query interface
- File transfer interface
- Custom command builder

**Implementation:**
- Plugin is configured per-session in session configuration
- When a session uses a terminal replacement plugin, only that session's tab uses the plugin UI
- Other sessions continue to use the standard `TerminalEmulator`
- Plugin returns a WPF `UIElement` that replaces `TerminalEmulator` for that specific session
- Plugin receives raw or cleaned server output
- Plugin can send input to server
- Plugin manages its own UI state

### 4.2 Input Transformer Plugins
**Purpose:** Modify user input before sending to server

**Scope:** Per-session (each session can have its own input transformers)

**Example Use Cases:**
- Spell checking and correction
- Client-side macros (expand shortcuts)
- Input validation
- Command aliasing
- Auto-completion

**Implementation:**
- Chain of transformers (priority-based) per session
- Each transformer receives input, can modify or pass through
- Can block input entirely
- Works with both text and raw byte input
- Each session maintains its own transformer chain

### 4.3 Output Transformer Plugins
**Purpose:** Modify server output before displaying

**Scope:** Per-session (each session can have its own output transformers)

**Example Use Cases:**
- Syntax highlighting
- Log filtering
- Output formatting
- Anonymization
- Color enhancement

**Implementation:**
- Chain of transformers (priority-based) per session
- Can work with raw ANSI codes or cleaned text
- Can modify, filter, or replace output
- Can add annotations or metadata
- Each session maintains its own transformer chain

### 4.4 Protocol Handler Plugins
**Purpose:** Support protocols other than SSH

**Scope:** Per-session (each session specifies its protocol)

**Example Use Cases:**
- Telnet support
- Serial port connections
- Custom proprietary protocols
- WebSocket-based terminals

**Implementation:**
- Plugin implements `ITerminalConnection` interface
- Plugin registered with protocol name
- Session configuration specifies protocol type
- Plugin handles all connection logic for that session

### 4.5 Configuration Provider Plugins
**Purpose:** Add custom configuration options

**Scope:** Per-session (primary), Global (optional for future use)

**Example Use Cases:**
- Plugin-specific settings
- Advanced protocol options
- Custom authentication methods
- Integration with external services

**Implementation:**
- Plugin provides XAML or code-defined UI sections
- Sections added to ConnectionDialog (per-session) or SettingsDialog (global, future)
- Configuration stored in JSON (session or global)
- Plugin loads/saves its own config sections
- Primary focus is on per-session configuration

---

## 5. Implementation Phases

### Phase 1: Core Plugin Infrastructure
**Duration:** 2-3 weeks

**Tasks:**
1. Create plugin interfaces (`IPlugin`, `IPluginContext`, etc.)
2. Implement plugin loader and registry
3. Create plugin discovery mechanism (scan plugins folder)
4. Implement plugin context and lifecycle management
5. Add plugin metadata system (JSON + attributes)
6. Create plugin manager service
7. Add error handling and plugin isolation
8. Create plugin API assembly (separate DLL for plugins to reference)

**Deliverables:**
- `TabbySSH.Plugins.dll` - Plugin API assembly
- `PluginManager` class
- `PluginLoader` class
- `PluginRegistry` class
- Plugin discovery and loading working
- Basic plugin can be loaded and initialized

### Phase 2: Configuration System
**Duration:** 1-2 weeks

**Tasks:**
1. Extend `SshSessionConfiguration` to support plugin configs
2. Implement JSON-based configuration storage
3. Create `IConfigProvider` interface and implementation
4. Add plugin config sections to `ConnectionDialog`
5. Add plugin config sections to global settings dialog
6. Implement config persistence (save/load)
7. Add config validation

**Deliverables:**
- Plugin configs stored in session JSON
- Plugin configs stored in global settings JSON
- UI for plugin configuration in dialogs
- Config loading/saving working

### Phase 3: Input/Output Transformation
**Duration:** 2 weeks

**Tasks:**
1. Implement `IInputTransformer` interface
2. Implement `IOutputTransformer` interface
3. Create transformation pipeline in `TerminalEmulator`
4. Add priority-based chaining
5. Integrate with existing data flow
6. Add support for raw vs cleaned data
7. Create example transformer plugins

**Deliverables:**
- Input transformation working
- Output transformation working
- Example plugins (macro, spellcheck, etc.)
- Documentation for transformer plugins

### Phase 4: Terminal Replacement
**Duration:** 2-3 weeks

**Tasks:**
1. Implement `ITerminalReplacement` interface
2. Modify `TerminalTabItem` to support plugin UI
3. Create plugin UI hosting mechanism
4. Implement data flow for replacement plugins
5. Add support for raw/cleaned data modes
6. Handle connection lifecycle events
7. Create example replacement plugin

**Deliverables:**
- Terminal replacement working
- Example replacement plugin (monitoring dashboard)
- Documentation for replacement plugins

### Phase 5: Protocol Handlers
**Duration:** 2 weeks

**Tasks:**
1. Implement `IProtocolHandler` interface
2. Extend `SessionConfiguration` for protocol selection
3. Modify connection creation to use protocol handlers
4. Update `ConnectionDialog` for protocol selection
5. Create example protocol handler (Telnet)
6. Ensure protocol handlers work with all plugin types

**Deliverables:**
- Protocol handler system working
- Example protocol handler
- Documentation for protocol handlers

### Phase 6: Polish and Documentation
**Duration:** 1-2 weeks

**Tasks:**
1. Create comprehensive plugin developer documentation
2. Create plugin templates and examples
3. Add plugin validation and error messages
4. Improve plugin error handling
5. Add plugin debugging support
6. Create plugin development guide
7. Add plugin API reference documentation
8. Create sample plugins for all types

**Deliverables:**
- Complete documentation
- Plugin templates
- Sample plugins
- Developer guide

---

## 6. Technical Details

### 6.1 Plugin Assembly Structure

```
PluginAssembly.dll
├── Plugin Class (implements IPlugin)
├── Plugin Metadata (attributes or plugin.json)
├── Dependencies (bundled or referenced)
└── Resources (optional)
```

### 6.2 Plugin Metadata

**Option 1: Attributes (Preferred)**
```csharp
[Plugin(
    Id = "com.example.macro",
    Name = "Macro Plugin",
    Version = "1.0.0",
    Description = "Client-side macro expansion",
    Author = "Example Author"
)]
public class MacroPlugin : IPlugin, IInputTransformer
{
    // ...
}
```

**Option 2: plugin.json**
```json
{
  "id": "com.example.macro",
  "name": "Macro Plugin",
  "version": "1.0.0",
  "description": "Client-side macro expansion",
  "author": "Example Author",
  "capabilities": ["InputTransformation"],
  "entryPoint": "Example.MacroPlugin, Example.MacroPlugin"
}
```

### 6.3 Plugin Loading Process

1. **Discovery:** Scan `Plugins/` directory for `.dll` files
2. **Validation:** Check for `IPlugin` implementation
3. **Metadata Extraction:** Read plugin metadata
4. **Dependency Resolution:** Load plugin dependencies
5. **Instantiation:** Create plugin instance (lazy)
6. **Initialization:** Call `Initialize()`
7. **Registration:** Register with appropriate managers

### 6.4 Plugin Isolation

- Each plugin loaded in separate `AssemblyLoadContext` (optional, for .NET 6+)
- Plugin exceptions caught and logged, don't crash application
- Plugin timeouts for long-running operations
- Resource limits (memory, CPU) - future enhancement

### 6.5 Configuration Storage

**Session Configuration (JSON):**
```json
{
  "name": "My Server",
  "host": "example.com",
  "plugins": {
    "terminalReplacement": "com.example.monitor",
    "enabled": ["com.example.macro", "com.example.highlight"],
    "configs": {
      "com.example.monitor": {
        "refreshInterval": 1000,
        "showCpu": true,
        "showMemory": true
      },
      "com.example.macro": {
        "macros": {
          "ll": "ls -lah",
          "..": "cd .."
        }
      },
      "com.example.highlight": {
        "patterns": ["ERROR", "WARNING"]
      }
    }
  }
}
```

**Note:** The `terminalReplacement` field specifies which plugin (if any) should replace the standard terminal emulator for this session. If not specified or set to `null`, the session uses the standard `TerminalEmulator`. Other plugins (input/output transformers) work alongside the terminal replacement or standard terminal.

**Global Configuration (JSON):**
```json
{
  "theme": "dark",
  "plugins": {
    "enabled": ["com.example.global"],
    "configs": {
      "com.example.global": {
        "setting": "value"
      }
    }
  }
}
```

**Note:** Global plugin configuration is supported but not the primary use case. Most plugins are configured per-session. Global configuration exists for future use cases where plugins might need application-wide settings.

### 6.6 Plugin API Assembly

Create separate `TabbySSH.Plugins.dll` that contains:
- All plugin interfaces
- Helper classes and utilities
- Plugin base classes (optional)
- Documentation

Plugins reference this assembly, not the main application.

---

## 7. UI Integration

### 7.1 Connection Dialog Integration

**Terminal Replacement Selection:**
- Dropdown or list to select terminal replacement plugin for this session
- Options: "Standard Terminal" (default) or any available terminal replacement plugin
- Only shows plugins that implement `ITerminalReplacement`
- Selection saved to session configuration
- When a terminal replacement is selected, that session will use the plugin UI instead of the standard terminal

**Plugin Config Sections:**
- Plugins with `IConfigProvider` can add sections
- Sections appear in collapsible `GroupBox` controls
- Each plugin gets its own section
- Config saved to session configuration

**Protocol Selection:**
- Dropdown for protocol type
- Only shows protocols from loaded plugins
- Default: SSH (built-in)

### 7.2 Global Settings Integration

**Note:** Global plugin configuration is supported by the API but is not the primary use case. Most plugins operate on a per-session basis. Global configuration exists for future use cases.

**Plugin Config Sections:**
- Similar to session config
- Global plugins can add sections (future use)
- Config saved to global settings

**Plugin Management:**
- List of installed plugins
- Enable/disable plugins
- Plugin information display
- Plugin configuration (primarily per-session, global support for future)

### 7.3 Terminal Tab Integration

**Terminal Replacement (Session-Specific):**
- Terminal replacement plugins are configured per-session in session configuration
- When creating/connecting a session, `TerminalTabItem` checks if the session has a terminal replacement plugin configured
- If found, replaces `TerminalEmulator` with plugin UI for that specific session tab only
- Other session tabs continue to use the standard `TerminalEmulator`
- Plugin UI receives connection events for its specific session
- Each session can have its own terminal replacement plugin, or use the standard terminal

**Plugin Indicators:**
- Visual indicator when plugins are active
- Tooltip showing active plugins
- Plugin status in tab context menu

---

## 8. Security Considerations

### 8.1 Plugin Validation
- Verify plugin signature (optional, future)
- Check plugin metadata validity
- Validate plugin capabilities
- Check for malicious code patterns (static analysis, future)

### 8.2 Sandboxing (Future)
- Run plugins in restricted AppDomain (legacy .NET)
- Use AssemblyLoadContext isolation (.NET 6+)
- Limit plugin permissions
- Resource quotas

### 8.3 Input Validation
- Validate all plugin inputs
- Sanitize plugin outputs
- Prevent plugin from accessing sensitive data
- Limit plugin access to file system

### 8.4 Error Handling
- Plugins cannot crash the application
- All plugin exceptions caught and logged
- Failed plugins disabled automatically
- User notification for plugin errors

---

## 9. File Structure

### 9.1 Application Structure
```
TabbySSH/
├── Plugins/
│   ├── TabbySSH.Plugins.dll (Plugin API)
│   └── [Plugin DLLs go here]
├── Services/
│   ├── Plugins/
│   │   ├── PluginManager.cs
│   │   ├── PluginLoader.cs
│   │   ├── PluginRegistry.cs
│   │   └── PluginContext.cs
│   └── ...
├── Models/
│   ├── PluginConfiguration.cs
│   └── ...
└── ...
```

### 9.2 Plugin Structure
```
Plugins/
├── Example.MacroPlugin.dll
├── Example.HighlightPlugin.dll
├── Example.TelnetProtocol.dll
└── ...
```

### 9.3 Configuration Files
```
{AppData}/TabbySSH/
├── sessions.json (includes plugin configs)
├── settings.json (includes global plugin configs)
└── plugins/
    └── [plugin-specific data]
```

---

## 10. Example Plugins

### 10.1 Macro Plugin (Input Transformer)
- Expands shortcuts like `ll` → `ls -lah`
- Configurable macro definitions
- Session-specific configuration

### 10.2 Syntax Highlighting Plugin (Output Transformer)
- Highlights keywords in server output
- Configurable patterns and colors
- Works with cleaned text (no ANSI codes)

### 10.3 System Monitor Plugin (Terminal Replacement)
- Replaces terminal with system monitoring dashboard
- Shows CPU, memory, disk usage
- Updates in real-time from server

### 10.4 Telnet Protocol Plugin (Protocol Handler)
- Implements Telnet protocol
- Works with existing terminal emulator
- Session configuration for Telnet-specific options

---

## 11. Testing Strategy

### 11.1 Unit Tests
- Plugin loading and discovery
- Plugin lifecycle management
- Configuration loading/saving
- Transformation pipelines

### 11.2 Integration Tests
- Plugin with real connections
- Multiple plugins working together
- Plugin error handling
- Configuration persistence

### 11.3 Manual Testing
- Example plugins in real scenarios
- Plugin development workflow
- User experience with plugins

---

## 12. Migration and Backward Compatibility

### 12.1 Existing Sessions
- Existing sessions work without plugins
- Plugin configs optional in session JSON
- Graceful handling of missing plugins

### 12.2 Plugin Updates
- Plugin versioning support
- Plugin update mechanism (future)
- Backward compatibility for plugin configs

---

## 13. Future Enhancements

### 13.1 Plugin Marketplace
- Centralized plugin repository
- Plugin installation from UI
- Plugin updates and notifications

### 13.2 Advanced Features
- Plugin dependencies
- Plugin communication/interaction
- Plugin scripting (Lua/Python, future)
- Visual plugin builder (future)

### 13.3 Performance
- Plugin performance monitoring
- Plugin resource usage tracking
- Plugin optimization tools

---

## 14. Success Criteria

### 14.1 Functional Requirements
- ✅ Plugins can be installed by copying DLL to plugins folder
- ✅ Plugins can replace terminal UI
- ✅ Plugins can transform input/output
- ✅ Plugins can add protocol handlers
- ✅ Plugins can add configuration options
- ✅ Plugin configs persist across sessions
- ✅ Multiple plugins can work together

### 14.2 Non-Functional Requirements
- ✅ Plugin API is clean and intuitive
- ✅ Plugin errors don't crash application
- ✅ Plugin loading is fast (< 1 second for 10 plugins)
- ✅ Plugins have minimal performance impact
- ✅ Comprehensive documentation exists
- ✅ Example plugins available

---

## 15. Timeline Estimate

**Total Duration:** 10-14 weeks

- Phase 1: 2-3 weeks
- Phase 2: 1-2 weeks
- Phase 3: 2 weeks
- Phase 4: 2-3 weeks
- Phase 5: 2 weeks
- Phase 6: 1-2 weeks

**Note:** Timeline assumes single developer working full-time. Adjust based on team size and availability.

---

## 16. Risks and Mitigation

### 16.1 Technical Risks
- **Risk:** Plugin API too complex or too simple
  - **Mitigation:** Early prototyping and feedback
- **Risk:** Performance issues with plugin chain
  - **Mitigation:** Benchmarking and optimization
- **Risk:** Plugin compatibility issues
  - **Mitigation:** Versioning and compatibility testing

### 16.2 Security Risks
- **Risk:** Malicious plugins
  - **Mitigation:** Code signing, sandboxing (future)
- **Risk:** Plugin data leaks
  - **Mitigation:** Input validation, access controls

### 16.3 Maintenance Risks
- **Risk:** API changes break plugins
  - **Mitigation:** Versioning, deprecation policy
- **Risk:** Plugin ecosystem doesn't develop
  - **Mitigation:** Good documentation, example plugins

---

## 17. Next Steps

1. Review and approve this plan
2. Create plugin API assembly project
3. Begin Phase 1 implementation
4. Set up plugin development environment
5. Create initial plugin templates

---

**Document Version:** 1.0  
**Last Updated:** 2025-01-XX  
**Author:** TabbySSH Development Team

