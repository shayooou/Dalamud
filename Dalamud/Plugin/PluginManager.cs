using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Configuration;
using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Plugin
{
    public class PluginManager {
        private readonly Dalamud dalamud;
        private readonly string pluginDirectory;

        private readonly PluginConfigurations pluginConfigs;

        private readonly Type interfaceType = typeof(IDalamudPlugin);

        public enum PluginLoadState {
            Unknown,
            Disabled,
            NotApplicable,
            InitFailed,
            Loaded
        }

        public class LoadedPlugin {
            public IDalamudPlugin PluginInstance { get; set; }
            public PluginDefinition Definition { get; set; }
            public DalamudPluginInterface PluginInterface { get; set; }
            public PluginLoadState LoadState { get; set; }
        }

        public readonly List<LoadedPlugin> Plugins = new List<LoadedPlugin>();

        public PluginManager(Dalamud dalamud, string pluginDirectory) {
            this.dalamud = dalamud;
            this.pluginDirectory = pluginDirectory;

            if (dalamud.Configuration.InstalledPlugins == null) {
                dalamud.Configuration.InstalledPlugins = new List<DalamudConfiguration.ConfigPlugin>();
                this.dalamud.Configuration.Save();
            }

            this.pluginConfigs = new PluginConfigurations(Path.Combine(Path.GetDirectoryName(dalamud.StartInfo.ConfigurationPath), "pluginConfigs"));

            // Try to load missing assemblies from the local directory of the requesting assembly
            // This would usually be implicit when using Assembly.Load(), but Assembly.LoadFile() doesn't do it...
            // This handler should only be invoked on things that fail regular lookups, but it *is* global to this appdomain
            AppDomain.CurrentDomain.AssemblyResolve += (object source, ResolveEventArgs e) =>
            {
                Log.Debug($"[PLUGINM] Resolving missing assembly {e.Name}");
                // This looks weird but I'm pretty sure it's actually correct.  Pretty sure.  Probably.
                var assemblyPath = Path.Combine(Path.GetDirectoryName(e.RequestingAssembly.Location), new AssemblyName(e.Name).Name + ".dll");
                if (!File.Exists(assemblyPath))
                {
                    Log.Error($"[PLUGINM] Assembly not found at {assemblyPath}");
                    return null;
                }
                return Assembly.LoadFrom(assemblyPath);
            };
        }

        public void UnloadPlugins() {
            if (this.Plugins == null)
                return;

            foreach (var loadedPlugin in this.Plugins) {
                loadedPlugin.PluginInstance.Dispose();
            }

            this.Plugins.Clear();
        }

        public LoadedPlugin GetLoadedPlugin(string internalName) =>
            this.Plugins.FirstOrDefault(x => x.Definition.InternalName == internalName);

        public void LoadPlugins() {
            LoadInstalledPlugins();
        }

        public bool DisposePlugin(string internalName, bool removeFromState = false) {
            var thisPlugin = this.Plugins.Where(x => x.Definition != null)
                                 .FirstOrDefault(x => x.Definition.InternalName == internalName);

            if (thisPlugin?.PluginInstance != null) {
                thisPlugin.PluginInstance.Dispose();
                if (!removeFromState) {
                    thisPlugin.LoadState = PluginLoadState.Disabled;
                } else {
                    this.Plugins.Remove(thisPlugin);
                }

                return true;
            }

            return false;
        }

        public bool LoadPluginFromAssembly(FileInfo dllFile, PluginDefinition definition) {
            Log.Information("[PLUGINM] Loading assembly at {0}", dllFile);

            // Assembly.Load() by name here will not load multiple versions with the same name, in the case of updates
            var pluginAssembly = Assembly.LoadFile(dllFile.FullName);

            Log.Information("[PLUGINM] Loading types for {0}", pluginAssembly.FullName);
            var types = pluginAssembly.GetTypes();
            foreach (var type in types)
            {
                if (type.IsInterface || type.IsAbstract)
                {
                    continue;
                }

                if (type.GetInterface(interfaceType.FullName) != null)
                {
                    var plugin = (IDalamudPlugin)Activator.CreateInstance(type);

                    try
                    {
                        var dalamudInterface = new DalamudPluginInterface(this.dalamud, type.Assembly.GetName().Name, this.pluginConfigs);
                        plugin.Initialize(dalamudInterface);
                        Log.Information("[PLUGINM] Loaded plugin: {0}", plugin.Name);

                        this.Plugins.Add(new LoadedPlugin
                        {
                            PluginInstance = plugin,
                            PluginInterface = dalamudInterface,
                            Definition = definition,
                            LoadState = PluginLoadState.Loaded
                        });

                        return true;
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "[PLUGINM] Failed to initialize plugin.");

                        this.Plugins.Add(new LoadedPlugin
                        {
                            Definition = definition,
                            LoadState = PluginLoadState.InitFailed
                        });
                    }
                }
            }

            Log.Information("[PLUGINM] Plugin DLL {0} has no plugin interface.", dllFile.FullName);

            return false;
        }

        private void LoadInstalledPlugins() {
            foreach (var installedPlugin in this.dalamud.Configuration.InstalledPlugins) {
                var pluginFile = new FileInfo(Path.Combine(this.pluginDirectory, installedPlugin.InternalName,
                                                           $"{installedPlugin.InternalName}.dll"));

                if (!pluginFile.Exists) {
                    Log.Error("[PLUGINM] InstalledPlugin {0} did not have a DLL but was enabled.");
                    continue;
                }

                var pluginDef = GetLocalDefinition(installedPlugin.InternalName);
                // load the definition if it exists, even for raw/developer plugins
                if (pluginDef != null)
                {
                    if (pluginDef.ApplicableVersion != this.dalamud.StartInfo.GameVersion && pluginDef.ApplicableVersion != "any")
                    {
                        this.Plugins.Add(new LoadedPlugin
                        {
                            Definition = pluginDef,
                            LoadState = PluginLoadState.NotApplicable
                        });

                        Log.Information("[PLUGINM] Plugin {0} has not applicable version.", pluginFile.FullName);
                        continue;
                    }
                }
                else
                {
                    Log.Information("[PLUGINM] Plugin DLL {0} has no definition.", pluginFile.FullName);
                    continue;
                }

                if (!installedPlugin.IsEnabled) {
                    this.Plugins.Add(new LoadedPlugin
                    {
                        Definition = pluginDef,
                        LoadState = PluginLoadState.Disabled
                    });
                    Log.Information("[PLUGINM] Plugin {0} was disabled.", pluginFile.FullName);
                    continue;
                }

                LoadPluginFromAssembly(pluginFile, pluginDef);
            }
        }

        public PluginDefinition GetLocalDefinition(string internalName) {
            // read the plugin def if present - again, fail before actually trying to load the dll if there is a problem
            var defJsonFile = new FileInfo(Path.Combine(this.pluginDirectory, internalName,
                                                        $"{internalName}.json"));

            Log.Debug("[PLUGINM] Loading definition for plugin {0}", defJsonFile.FullName);

            // load the definition if it exists, even for raw/developer plugins
            if (defJsonFile.Exists) {
                
                return JsonConvert.DeserializeObject<PluginDefinition>(
                    File.ReadAllText(defJsonFile.FullName));
            }

            return null;
        }
    }
}
