using System.Reflection;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;


[assembly: CommandClass(typeof(DynamicLoad.Main))]

namespace DynamicLoad;

public sealed class Main {
    private readonly static Dictionary<string, Action> CommandMethods = new();
    private static string? _targetFilePath;
    private static bool _resolverRegistered;

    [CommandMethod("ATDLL")]
    public void SelectDll() {
        using var dialog = new OpenFileDialog();

        dialog.Filter = "DLL files (*.dll)|*.dll";
        dialog.FilterIndex = 1;
        dialog.Multiselect = false;

        if (dialog.ShowDialog() != DialogResult.OK)
            return;

        _targetFilePath = dialog.FileName;
        EnsureAssemblyResolver();
    }

    [CommandMethod("ATLOAD")]
    public void Load() {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;

        if (string.IsNullOrWhiteSpace(_targetFilePath) || !File.Exists(_targetFilePath)) {
            editor.WriteMessage("\nDLL 路径无效，请先执行 ATDLL。");

            return;
        }

        try {
            CommandMethods.Clear();

            var bytes = File.ReadAllBytes(_targetFilePath);

            var assembly = Assembly.Load(bytes);

            LoadCommands(assembly);

            editor.WriteMessage($"\n动态加载成功，共发现 {CommandMethods.Count} 个命令。");
        } catch (Exception ex) {
            editor.WriteMessage($"\n动态加载失败：\n{ex}");
        }
    }

    [CommandMethod("ATRUN")]
    public void Run() {
        var editor = Application.DocumentManager.MdiActiveDocument.Editor;

        if (CommandMethods.Count == 0) {
            editor.WriteMessage("\n没有可执行命令，请先执行 ATLOAD。");

            return;
        }

        var options = new PromptKeywordOptions("\n请选择要执行的命令") {
                                                                  AllowNone = true
                                                              };

        var index = 1;

        foreach (var command in CommandMethods) {
            options.Keywords.Add(command.Key, index.ToString(), $"{command.Key} ({index})");

            index++;
        }

        var result = editor.GetKeywords(options);

        if (result.Status != PromptStatus.OK)
            return;

        if (CommandMethods.TryGetValue(result.StringResult, out var action)) {
            try {
                action();
            } catch (Exception ex) {
                editor.WriteMessage($"\n命令执行失败：\n{ex}");
            }
        }
    }

    private static void LoadCommands(Assembly assembly) {
        var commandClasses = assembly.GetCustomAttributes<CommandClassAttribute>();

        foreach (var commandClass in commandClasses) {
            var type = commandClass.Type;

            object? instance = null;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Where(m => m.GetCustomAttribute<CommandMethodAttribute>() != null);

            foreach (var method in methods) {
                if (method.GetParameters().Length != 0)
                    continue;

                var attribute = method.GetCustomAttribute<CommandMethodAttribute>()!;

                var commandName = attribute.GlobalName;

                if (!method.IsStatic)
                    instance ??= Activator.CreateInstance(type);

                CommandMethods[commandName] = () => method.Invoke(method.IsStatic ? null : instance, null);
            }
        }
    }

    private static void EnsureAssemblyResolver() {
        if (_resolverRegistered)
            return;

        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

        _resolverRegistered = true;
    }

    private static Assembly? ResolveAssembly(object sender, ResolveEventArgs args) {
        if (string.IsNullOrWhiteSpace(_targetFilePath))
            return null;

        var directory = Path.GetDirectoryName(_targetFilePath);

        if (directory == null) return null;

        var name = new AssemblyName(args.Name).Name;

        var path = Path.Combine(directory, name + ".dll");

        return !File.Exists(path) ? null : Assembly.Load(File.ReadAllBytes(path));
    }
}
