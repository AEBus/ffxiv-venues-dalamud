using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace FFXIVVenues.Dalamud;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    public void Save(IDalamudPluginInterface pluginInterface) =>
        pluginInterface.SavePluginConfig(this);
}
