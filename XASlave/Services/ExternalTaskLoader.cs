using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XASlave.Data;

namespace XASlave.Services;

public sealed class ExternalTaskLoader : IDisposable
{
    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private readonly string tasksFolder;
    private readonly List<ITaskPanel> loadedTasks = new();
    private readonly List<AssemblyLoadContext> loadContexts = new();
    private readonly List<string> tempFiles = new();

    public IReadOnlyList<ITaskPanel> Tasks => loadedTasks;

    public ExternalTaskLoader(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;

        var configDir = pluginInterface.GetPluginConfigDirectory();
        tasksFolder = Path.Combine(configDir, "tasks");

        LoadAll();
    }

    private void LoadAll()
    {
        if (!Directory.Exists(tasksFolder))
            return;

        var dllFiles = Directory.GetFiles(tasksFolder, "*.dll");
        if (dllFiles.Length == 0)
            return;

        foreach (var dllPath in dllFiles)
        {
            try
            {
                LoadDll(dllPath);
            }
            catch (Exception ex)
            {
                log.Error($"[XASlave] {Path.GetFileName(dllPath)}: {ex.Message}");
            }
        }

        if (loadedTasks.Count > 0)
            log.Information($"[XASlave] Loaded {loadedTasks.Count} task panel(s).");
    }

    private void LoadDll(string dllPath)
    {
        var fileName = Path.GetFileName(dllPath);

        // Copy to temp file so the original is never locked.
        var tempPath = Path.Combine(Path.GetTempPath(), $"XASlave_{Guid.NewGuid():N}_{fileName}");
        File.Copy(dllPath, tempPath, true);
        tempFiles.Add(tempPath);

        var alc = new AssemblyLoadContext($"XASlaveTask_{fileName}", isCollectible: true);
        loadContexts.Add(alc);

        alc.Resolving += (ctx, name) =>
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == name.Name)
                    return asm;
            }

            var depPath = Path.Combine(Path.GetDirectoryName(dllPath)!, $"{name.Name}.dll");
            if (File.Exists(depPath))
            {
                var depTemp = Path.Combine(Path.GetTempPath(), $"XASlave_{Guid.NewGuid():N}_{name.Name}.dll");
                File.Copy(depPath, depTemp, true);
                tempFiles.Add(depTemp);
                return ctx.LoadFromAssemblyPath(depTemp);
            }

            return null;
        };

        var assembly = alc.LoadFromAssemblyPath(tempPath);

        var iface = typeof(ITaskPanel);
        var taskTypes = new List<Type>();
        foreach (var t in assembly.GetTypes())
        {
            if (t.IsInterface || t.IsAbstract) continue;
            if (iface.IsAssignableFrom(t))
            {
                taskTypes.Add(t);
            }
            else if (t.GetInterfaces().Any(i => i.FullName == iface.FullName))
            {
                taskTypes.Add(t);
                log.Debug($"[XASlave] {fileName}: {t.Name} matched by name fallback.");
            }
        }

        if (taskTypes.Count == 0)
        {
            log.Debug($"[XASlave] {fileName}: no ITaskPanel types.");
            return;
        }

        foreach (var taskType in taskTypes)
        {
            try
            {
                var obj = Activator.CreateInstance(taskType)!;
                ITaskPanel instance;
                if (obj is ITaskPanel direct)
                {
                    instance = direct;
                }
                else
                {
                    instance = new ReflectionTaskPanel(obj);
                }

                if (loadedTasks.Any(t => t.Name == instance.Name))
                    continue;

                instance.Initialize();
                loadedTasks.Add(instance);
            }
            catch (Exception ex)
            {
                log.Error($"[XASlave] {taskType.FullName}: {ex.Message}");
            }
        }
    }

    private sealed class ReflectionTaskPanel : ITaskPanel
    {
        private readonly dynamic target;

        public ReflectionTaskPanel(object obj)
        {
            target = obj;
        }

        public string Name => target.Name;
        public string Label => target.Label;
        public void Draw() => target.Draw();
        public void Initialize() => target.Initialize();
        public void Dispose() => target.Dispose();
    }

    public void Dispose()
    {
        foreach (var task in loadedTasks)
        {
            try { task.Dispose(); }
            catch { /* best-effort */ }
        }
        loadedTasks.Clear();

        foreach (var alc in loadContexts)
        {
            try { alc.Unload(); }
            catch { /* best-effort */ }
        }
        loadContexts.Clear();

        foreach (var tf in tempFiles)
        {
            try { if (File.Exists(tf)) File.Delete(tf); }
            catch { /* best-effort */ }
        }
        tempFiles.Clear();
    }
}
