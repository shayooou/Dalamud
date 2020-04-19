using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Plugin
{
    public class PluginRepository
    {
        private const string PluginRepoBaseUrl = "https://goaaats.github.io/DalamudPlugins/";

        private readonly Dalamud dalamud;
        private string pluginDirectory;
        public ReadOnlyCollection<PluginDefinition> PluginMaster;

        public enum InitializationState {
            Unknown,
            InProgress,
            Success,
            Fail
        }

        public InitializationState State { get; private set; }

        public PluginRepository(Dalamud dalamud, string pluginDirectory, string gameVersion)
        {
            this.dalamud = dalamud;
            this.pluginDirectory = pluginDirectory;

            State = InitializationState.InProgress;
            Task.Run(CachePluginMaster).ContinueWith(t => {
                if (t.IsFaulted)
                    State = InitializationState.Fail;
            });
        }

        private void CachePluginMaster()
        {
            try
            {
                using var client = new WebClient();

                var data = client.DownloadString(PluginRepoBaseUrl + "pluginmaster.json");

                this.PluginMaster = JsonConvert.DeserializeObject<ReadOnlyCollection<PluginDefinition>>(data);

                State = InitializationState.Success;
            }
            catch {
                State = InitializationState.Fail;
            }
        }

        public bool DisablePlugin(string internalName) {
            this.dalamud.Configuration.InstalledPlugins.First(x => x.InternalName == internalName).IsEnabled = false;
            this.dalamud.Configuration.Save();

            return this.dalamud.PluginManager.DisposePlugin(internalName);
        }

        public bool InstallPlugin(string internalName, bool forceReinstall = false) {
            try
            {
                var outputDir = new DirectoryInfo(Path.Combine(this.pluginDirectory, internalName));
                var dllFile = new FileInfo(Path.Combine(outputDir.FullName, $"{internalName}.dll"));

                if (dllFile.Exists && !forceReinstall) {
                    AddOrEnableToConfig(internalName);
                    Log.Information("[PLUGINR] Plugin was local.");
                    return this.dalamud.PluginManager.LoadPluginFromAssembly(dllFile, this.dalamud.PluginManager.GetLocalDefinition(internalName));
                }

                if (outputDir.Exists)
                    outputDir.Delete(true);

                outputDir.Create();

                var path = Path.GetTempFileName();
                Log.Information("[PLUGINR] Downloading plugin to {0}", path);
                using var client = new WebClient();
                client.DownloadFile(PluginRepoBaseUrl + $"/plugins/{internalName}/latest.zip", path);

                Log.Information("[PLUGINR] Extracting to {0}", outputDir);

                ZipFile.ExtractToDirectory(path, outputDir.FullName);

                AddOrEnableToConfig(internalName);

                Log.Information("[PLUGINR] Plugin installed remotely.");

                return this.dalamud.PluginManager.LoadPluginFromAssembly(dllFile, this.PluginMaster.First(x => x.InternalName == internalName));
            }
            catch (Exception e)
            {
                Log.Error(e, "[PLUGINR] Plugin download failed hard.");
                return false;
            }
        }

        private void AddOrEnableToConfig(string internalName) {
            if (this.dalamud.Configuration.InstalledPlugins.All(x => x.InternalName != internalName))
            {
                this.dalamud.Configuration.InstalledPlugins.Add(new DalamudConfiguration.ConfigPlugin
                {
                    InternalName = internalName,
                    IsEnabled = true
                });
            }
            else
            {
                this.dalamud.Configuration.InstalledPlugins.First(x => x.InternalName == internalName).IsEnabled = true;
            }

            this.dalamud.Configuration.Save();
        }

        public (bool Success, int UpdatedCount) UpdatePlugins(bool dryRun = false)
        {
            Log.Information("[PLUGINR] Starting plugin update... dry:{0}", dryRun);

            var updatedCount = 0;
            var hasError = false;

            try
            {
                var pluginsDirectory = new DirectoryInfo(this.pluginDirectory);
                foreach (var installed in pluginsDirectory.GetDirectories())
                {
                    var localInfoFile = new FileInfo(Path.Combine(installed.FullName, $"{installed.Name}.json"));

                    if (!localInfoFile.Exists)
                    {
                        Log.Information("[PLUGINR] Has no definition: {0}", localInfoFile.FullName);
                        continue;
                    }

                    var info = JsonConvert.DeserializeObject<PluginDefinition>(File.ReadAllText(localInfoFile.FullName));

                    var remoteInfo = this.PluginMaster.FirstOrDefault(x => x.Name == info.Name);

                    if (remoteInfo == null)
                    {
                        Log.Information("[PLUGINR] Is not in pluginmaster: {0}", info.Name);
                        continue;
                    }

                    if (remoteInfo.AssemblyVersion != info.AssemblyVersion)
                    {
                        Log.Information("[PLUGINR] Eligible for update: {0}", remoteInfo.InternalName);

                        // DisablePlugin() below immediately creates a .disabled file anyway, but will fail
                        // with an exception if we try to do it twice in row like this

                        if (!dryRun)
                        {
                            // Try to dispose plugin if it is loaded
                            try
                            {
                                if (!this.dalamud.PluginManager.DisposePlugin(info.InternalName, true))
                                    Log.Information("[PLUGINR] Plugin was not loaded.");
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "[PLUGINR] Plugin dispose failed.");
                                hasError = true;
                            }

                            Log.Information("[PLUGINR] Plugin disposed.");

                            var installSuccess = InstallPlugin(remoteInfo.InternalName, true);

                            if (installSuccess)
                            {
                                updatedCount++;
                            }
                            else
                            {
                                Log.Error("[PLUGINR] InstallPlugin failed.");
                                hasError = true;
                            }
                        }
                        else {
                            updatedCount++;
                        }
                    }
                    else
                    {
                        Log.Information("[PLUGINR] Up to date: {0}", remoteInfo.InternalName);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "[PLUGINR] Plugin update failed hard.");
                hasError = true;
            }

            Log.Information("[PLUGINR] Plugin update OK.");

            return (!hasError, updatedCount);
        }
    }
}
