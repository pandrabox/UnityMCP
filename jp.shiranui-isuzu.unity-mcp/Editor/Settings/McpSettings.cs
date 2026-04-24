using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Stores and manages Unity MCP settings.
    /// </summary>
    [FilePath("UserSettings/UnityMcpSettings.asset", FilePathAttribute.Location.PreferencesFolder)]
    public sealed class McpSettings : ScriptableSingleton<McpSettings>
    {
        /// <summary>
        /// Gets or sets the path to the client installation.
        /// </summary>
        [SerializeField]
        public string clientInstallationPath = string.Empty;

        /// <summary>
        /// Gets or sets the HTTP port for the Unity HTTP server.
        /// </summary>
        [SerializeField]
        public int httpPort = 27182;

        /// <summary>
        /// Gets or sets whether to auto-start the server when Unity starts.
        /// </summary>
        [SerializeField]
        public bool autoStartOnLaunch = true;

        /// <summary>
        /// Gets or sets whether to persist the bound port across assembly reloads via SessionState.
        /// </summary>
        [SerializeField]
        public bool portPersistenceEnabled = true;

        /// <summary>
        /// Gets or sets the maximum retry duration (ms) for TS/CLI clients to retry after a reload.
        /// This value is advisory; the TS server reads it from /health.
        /// </summary>
        [SerializeField]
        public int reloadRetryMaxMs = 15000;

        /// <summary>
        /// Gets or sets whether to store detailed logs.
        /// </summary>
        [SerializeField]
        public bool detailedLogs = true;

        /// <summary>
        /// Gets or sets whether to use UDP broadcast for discovery.
        /// </summary>
        [SerializeField]
        public bool useUdpBroadcast = true;

        /// <summary>
        /// Gets or sets the UDP broadcast port.
        /// </summary>
        [SerializeField]
        public int udpBroadcastPort = 27183;

        /// <summary>
        /// Gets or sets the broadcast interval in seconds.
        /// </summary>
        [SerializeField]
        public int broadcastIntervalSeconds = 30;

        /// <summary>
        /// Gets or sets the dictionary of command handlers and their enabled states.
        /// </summary>
        [SerializeField]
        public Dictionary<string, bool> handlerEnabledStates = new Dictionary<string, bool>();

        /// <summary>
        /// Gets or sets the dictionary of resource handlers and their enabled states.
        /// </summary>
        [SerializeField]
        public Dictionary<string, bool> resourceHandlerEnabledStates = new Dictionary<string, bool>();

        // ── Legacy compatibility properties ──
        // These allow old code references to still compile during migration.

        /// <summary>
        /// Legacy: returns "127.0.0.1". HTTP server always binds to localhost.
        /// </summary>
        public string host => "127.0.0.1";

        /// <summary>
        /// Legacy: maps to httpPort.
        /// </summary>
        public int port
        {
            get => this.httpPort;
            set => this.httpPort = value;
        }

        /// <summary>
        /// Legacy: maps to useUdpBroadcast.
        /// </summary>
        public bool useUdpDiscovery
        {
            get => this.useUdpBroadcast;
            set => this.useUdpBroadcast = value;
        }

        /// <summary>
        /// Legacy: maps to udpBroadcastPort.
        /// </summary>
        public int udpDiscoveryPort
        {
            get => this.udpBroadcastPort;
            set => this.udpBroadcastPort = value;
        }

        /// <summary>
        /// Saves the settings to disk.
        /// </summary>
        public void Save()
        {
            this.Save(true);
        }

        public void UpdateHandlerEnabledState(string commandPrefix, bool enabled)
        {
            this.handlerEnabledStates[commandPrefix] = enabled;
            this.Save();
        }

        public bool GetHandlerEnabledState(string commandPrefix)
        {
            return this.handlerEnabledStates.TryGetValue(commandPrefix, out var enabled) ? enabled : true;
        }

        public Dictionary<string, bool> GetAllHandlerEnabledStates()
        {
            return new Dictionary<string, bool>(this.handlerEnabledStates);
        }

        public void UpdateResourceHandlerEnabledState(string resourceName, bool enabled)
        {
            this.resourceHandlerEnabledStates[resourceName] = enabled;
            this.Save();
        }

        public bool GetResourceHandlerEnabledState(string resourceName)
        {
            return this.resourceHandlerEnabledStates.TryGetValue(resourceName, out var enabled) ? enabled : true;
        }

        public Dictionary<string, bool> GetAllResourceHandlerEnabledStates()
        {
            return new Dictionary<string, bool>(this.resourceHandlerEnabledStates);
        }
    }
}
