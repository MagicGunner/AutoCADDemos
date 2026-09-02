using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(DynamicLoad.Main))]

namespace DynamicLoad;

public sealed class Main {
    private readonly static Dictionary<string, Action> CommandMethods = new(StringComparer.OrdinalIgnoreCase);

    private static string? _targetFilePath;

    private static bool _resolverRegistered;

    [CommandMethod("ATDLL")]
    public void SelectDll() {
        using var dialog = new OpenFileDialog();
        dialog.Filter = "DLL files (*.dll)|*.dll";
        dialog.FilterIndex = 1;
        dialog.Multiselect = false;
        if (dialog.ShowDialog() != DialogResult.OK) return;
        _targetFilePath = dialog.FileName;
        EnsureAssemblyResolver();
        GetEditor().WriteMessage($"\n目标 DLL：{_targetFilePath}");
    }

    [CommandMethod("ATLOAD")]
    public void Load() {
        var editor = GetEditor();

        if (string.IsNullOrWhiteSpace(_targetFilePath) || !File.Exists(_targetFilePath)) {
            editor.WriteMessage("\nDLL 路径无效，请先执行 ATDLL。");

            return;
        }

        try {
            CommandMethods.Clear();

            var assemblyBytes = File.ReadAllBytes(_targetFilePath);

            var pdbPath = Path.ChangeExtension(_targetFilePath, ".pdb");

            Assembly assembly;

        #if DEBUG
            if (File.Exists(pdbPath)) {
                assembly = Assembly.Load(assemblyBytes, File.ReadAllBytes(pdbPath));
            } else {
                assembly = Assembly.Load(assemblyBytes);
            }
        #else
            assembly =
                Assembly.Load(assemblyBytes);
        #endif

            LoadCommands(assembly, editor);

            editor.WriteMessage($"\n动态加载成功：{assembly.GetName().Name}");

            editor.WriteMessage($"\n共发现 {CommandMethods.Count} 个命令。");
        } catch (ReflectionTypeLoadException ex) {
            editor.WriteMessage("\n扫描程序集类型失败：");

            foreach (var loaderException in ex.LoaderExceptions) {
                if (loaderException != null) {
                    editor.WriteMessage($"\n{loaderException.Message}");
                }
            }
        } catch (Exception ex) {
            editor.WriteMessage($"\n动态加载失败：\n{GetActualException(ex)}");
        }
    }

    [CommandMethod("ATRUN")]
    public void Run() {
        var editor = GetEditor();

        if (CommandMethods.Count == 0) {
            editor.WriteMessage("\n没有可执行命令，请先执行 ATLOAD。");

            return;
        }

        var options = new PromptKeywordOptions("\n请选择要执行的命令") {
                                                                  AllowNone = true
                                                              };

        var index = 1;

        foreach (var command in CommandMethods) {
            var keyword = $"C{index}";

            options.Keywords.Add(keyword, keyword, $"{command.Key} ({index})");

            index++;
        }

        var result = editor.GetKeywords(options);

        if (result.Status != PromptStatus.OK)
            return;

        if (!result.StringResult.StartsWith("C", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        if (!int.TryParse(result.StringResult.Substring(1), out var selectedIndex)) {
            return;
        }

        var selectedCommand = CommandMethods.ElementAtOrDefault(selectedIndex - 1);

        if (string.IsNullOrWhiteSpace(selectedCommand.Key)) {
            return;
        }

        try {
            selectedCommand.Value();
        } catch (Exception ex) {
            editor.WriteMessage($"\n命令执行失败：\n{GetActualException(ex)}");
        }
    }

    private static void LoadCommands(Assembly assembly, Editor editor) {
        var commandCount = 0;

        foreach (var type in GetLoadableTypes(assembly)) {
            if (type == null)
                continue;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                              .Where(method => method.GetCustomAttribute<CommandMethodAttribute>() != null);

            object? instance = null;

            foreach (var method in methods) {
                var attribute = method.GetCustomAttribute<CommandMethodAttribute>();

                if (attribute == null)
                    continue;

                if (method.GetParameters().Length != 0) {
                    editor.WriteMessage($"\n跳过命令 {attribute.GlobalName}：" + "方法带有参数。");

                    continue;
                }

                if (!method.IsStatic) {
                    instance ??= Activator.CreateInstance(type);

                    if (instance == null) {
                        editor.WriteMessage($"\n无法创建命令类：{type.FullName}");

                        continue;
                    }
                }

                var commandName = string.IsNullOrWhiteSpace(attribute.GlobalName) ? method.Name : attribute.GlobalName;

                var target = method.IsStatic ? null : instance;

                var capturedMethod = method;
                var capturedTarget = target;

                CommandMethods[commandName] = () => {
                                                  try {
                                                      capturedMethod.Invoke(capturedTarget, null);
                                                  } catch (TargetInvocationException ex) {
                                                      throw ex.InnerException ?? ex;
                                                  }
                                              };

                commandCount++;

                editor.WriteMessage($"\n发现命令：" + $"{commandName} " + $"[{type.FullName}.{method.Name}]");
            }
        }

        if (commandCount == 0) {
            editor.WriteMessage("\n未发现任何 [CommandMethod]。");
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly) {
        try {
            return assembly.GetTypes();
        } catch (ReflectionTypeLoadException ex) {
            return ex.Types.Where(type => type != null)!;
        }
    }

    private static void EnsureAssemblyResolver() {
        if (_resolverRegistered)
            return;

        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

        _resolverRegistered = true;
    }

    private static Assembly? ResolveAssembly(object? sender, ResolveEventArgs args) {
        var requestedName = new AssemblyName(args.Name);

        // 先从 AutoCAD 当前 AppDomain 已加载程序集里找。
        // 特别重要：不要重新加载 Autodesk.AutoCAD.*。
        var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                                      .FirstOrDefault(assembly => {
                                                          var name = assembly.GetName();

                                                          return string.Equals(name.Name, requestedName.Name, StringComparison.OrdinalIgnoreCase);
                                                      });

        if (loadedAssembly != null)
            return loadedAssembly;

        if (string.IsNullOrWhiteSpace(_targetFilePath)) {
            return null;
        }

        var directory = Path.GetDirectoryName(_targetFilePath);

        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var assemblyName = requestedName.Name;

        if (string.IsNullOrWhiteSpace(assemblyName)) {
            return null;
        }

        var dllPath = Path.Combine(directory, assemblyName + ".dll");

        if (!File.Exists(dllPath))
            return null;

        try {
            return Assembly.Load(File.ReadAllBytes(dllPath));
        } catch {
            return null;
        }
    }

    private static Editor GetEditor() => Application.DocumentManager.MdiActiveDocument.Editor;

    private static Exception GetActualException(Exception exception) {
        while (exception is TargetInvocationException && exception.InnerException != null) {
            exception = exception.InnerException;
        }

        return exception;
    }
}
