using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnityMCP.Editor.Handlers
{
    internal static class PlayModeControl
    {
        public static JObject Control(JObject parameters)
        {
            var action = parameters["action"]?.ToString();
            if (string.IsNullOrEmpty(action))
            {
                return new JObject { ["error"] = "action parameter is required" };
            }

            switch (action)
            {
                case "status":
                    return GetStatus();

                case "play":
                    if (EditorApplication.isPlaying)
                    {
                        var status = GetStatus();
                        status["message"] = "Already in play mode";
                        return status;
                    }
                    EditorApplication.delayCall += () => { EditorApplication.isPlaying = true; };
                    return new JObject
                    {
                        ["deferred"] = true,
                        ["action"] = "play",
                        ["message"] = "Play mode will start on next frame. Connection may be interrupted during domain reload."
                    };

                case "stop":
                    if (!EditorApplication.isPlaying)
                    {
                        var status = GetStatus();
                        status["message"] = "Not in play mode";
                        return status;
                    }
                    EditorApplication.delayCall += () => { EditorApplication.isPlaying = false; };
                    return new JObject
                    {
                        ["deferred"] = true,
                        ["action"] = "stop",
                        ["message"] = "Play mode will stop on next frame. Connection may be interrupted during domain reload."
                    };

                case "pause":
                    if (!EditorApplication.isPlaying)
                    {
                        return new JObject { ["error"] = "Cannot pause outside of play mode" };
                    }
                    EditorApplication.isPaused = true;
                    return GetStatus();

                case "unpause":
                    if (!EditorApplication.isPlaying)
                    {
                        return new JObject { ["error"] = "Cannot unpause outside of play mode" };
                    }
                    EditorApplication.isPaused = false;
                    return GetStatus();

                case "step":
                    if (!EditorApplication.isPlaying)
                    {
                        return new JObject { ["error"] = "Cannot step outside of play mode" };
                    }
                    if (!EditorApplication.isPaused)
                    {
                        EditorApplication.isPaused = true;
                    }
                    EditorApplication.Step();
                    return GetStatus();

                default:
                    return new JObject { ["error"] = $"Unknown action: {action}" };
            }
        }

        private static JObject GetStatus()
        {
            return new JObject
            {
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling
            };
        }
    }
}
