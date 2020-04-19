using System;
using Dalamud.DiscordBot;

namespace Dalamud {
    [Serializable]
    public sealed class DalamudStartInfo
    {
        public string WorkingDirectory;
        public string ConfigurationPath; 

        public string PluginDirectory;
        public ClientLanguage Language;

        public string GameVersion;
    }

    public enum ClientLanguage
    {
        Japanese,
        English,
        German,
        French
    }
}
