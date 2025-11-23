# TabbySSH

TabbySSH is a multi-tab SSH terminal client for Windows built with WPF and .NET. It provides a clean interface for managing multiple SSH connections with improved usability over traditional SSH clients like PuTTY.

## What It Does

TabbySSH allows you to connect to remote servers via SSH and manage multiple terminal sessions in a single window using tabs. Each session can be configured with custom settings, colors, and connection options. Sessions can be organized into groups and saved for quick access.

## Features

### Core Functionality
- **Multi-tab Interface**: Manage multiple SSH sessions in a single window with tabbed interface
- **Session Management**: Organize sessions into groups with drag-and-drop support
- **Persistent Sessions**: Save and restore session configurations
- **Tab Navigation**: 
  - `Ctrl+T` - New tab
  - `Ctrl+W` - Close tab
  - `Ctrl+Tab` - Next tab (cycles through tabs in predictable order)
  - `Ctrl+Shift+Tab` - Previous tab (cycles through tabs in predictable order)

### Authentication
- Password authentication
- Private key authentication (PEM, KEY, PPK formats)
- Passphrase support for encrypted private keys

### Terminal Features
- **VT100 Terminal Emulation**: Full ANSI color support and terminal control sequences
- **Customizable Appearance**: 
  - Per-session custom foreground and background colors
  - Per-session accent colors for quick visual identification of different servers
  - Customizable font family and size
  - Light and dark themes
- **Line Ending Support**: Configurable line endings (Unix LF / Windows CRLF)
- **Bell Notifications**: Flash, sound, or none
- **Scrollback**: Extensive scrollback buffer for terminal history

### Connection Options
- **Port Forwarding**: 
  - Local port forwarding
  - Remote port forwarding
  - Multiple forwarding rules per session
- **X11 Forwarding**: Enable/disable X11 forwarding
- **Compression**: Enable/disable SSH compression
- **Keep-Alive**: Configurable keep-alive interval
- **Connection Timeout**: Configurable connection timeout
- **Terminal Resize Methods**: SSH, ANSI, STTY, XTERM, or None

### User Interface
- **Modern Theme System**: 
  - Light theme
  - Dark theme (similar to VS Code/Cursor)
  - JSON-based theme configuration
- **Custom Window Chrome**: Clean, modern title bar with custom controls
- **Session Organization**: Group sessions into folders for better organization
- **Context Menus**: Right-click support for session management
- **Window State Persistence**: Remembers window size and position

## Why TabbySSH Was Created

TabbySSH was created to solve usability annoyances present in PuTTY and multi-tab wrappers for PuTTY:

### Alt+Tab Not Working as Expected
PuTTY and all tested wrappers require double-tap on Tab when using Alt+Tab, making window switching frustrating. TabbySSH handles window focus correctly so Alt+Tab works on the first press.

### Disconnected Terminal Dialog Boxes
When a terminal disconnects, PuTTY shows a pop-up dialog box that must be clicked to close, interrupting workflow. TabbySSH handles disconnections gracefully without blocking modal dialogs.
This pop-up is even more annoying in wrapper programs blocking reconnecting until it's been closed, and it can't be closed with the keyboard as it isn't focused by default.

### Inconsistent Tab Navigation
Ctrl+Tab and Ctrl+Shift+Tab don't work consistently in PuTTY wrappers, making it difficult to switch between tabs in a predictable order. TabbySSH provides reliable tab navigation that cycles through tabs consistently.

### Non-Optimal Default Settings
PuTTY's default settings are not optimal for modern use and can be quirky to configure. TabbySSH uses sensible defaults.

### No Visual Differentiation Between Servers
It is awkward or impossible to set custom colors for individual servers/sessions in PuTTY wrappers, making it difficult to quickly identify which server you're connected to when managing multiple sessions. TabbySSH allows per-session custom colors (foreground, background, and accent) for instant visual identification.

## Requirements

- Windows 10 or later
- .NET 8.0 Runtime

## Building

```bash
dotnet build
```

## License

[Add your license information here]
