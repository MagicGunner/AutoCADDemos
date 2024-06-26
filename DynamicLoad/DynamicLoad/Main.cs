using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(DynamicLoad.Main))]

namespace DynamicLoad;

public class Main {
    private Dictionary<string, Action> _commandMethods = new ();

    private string? _targetFilePath;

    public Main() {
        InitDllPath();
    }

    /// <summary>
    /// 获取当前二次开发生存的DLL完整路径
    /// </summary>
    [CommandMethod(nameof(InitDllPath))]
    public void InitDllPath() {
        var openFileDialog = new OpenFileDialog();
        openFileDialog.Filter = "DLL files (*.dll)|*.dll";
        openFileDialog.FilterIndex = 1;
        openFileDialog.Multiselect = false;

        if (openFileDialog.ShowDialog() != DialogResult.OK) return;
        // 获取所选文件的完整路径
        _targetFilePath = openFileDialog.FileName;
    }

    /// <summary>
    /// 动态加载DLL
    /// </summary>
    [CommandMethod(nameof(DLoad))]
    public void DLoad() {
        if (!File.Exists(_targetFilePath)) {
            Application.ShowAlertDialog("当前DLL路径为空或非法，请输入指令InitDllPath重新加载您的DLL");
            return;
        }
        _commandMethods = new Dictionary<string, Action>();
        var targetAssembly = Assembly.Load(File.ReadAllBytes(_targetFilePath));
        var customAttributes = targetAssembly.GetCustomAttributes(typeof(CommandClassAttribute), false);
        foreach (var customAttribute in customAttributes) {
            var commandClassAttr = customAttribute as CommandClassAttribute;
            var targetType = commandClassAttr!.Type;
            var targetObj = Activator.CreateInstance(targetType);
            var methodInfos = targetType.GetMethods().Where(methodInfo => methodInfo.DeclaringType == targetType);
            foreach (var methodInfo in methodInfos) {
                _commandMethods[targetType + "." + methodInfo.Name] = () => methodInfo.Invoke(targetObj, null);
            }
        }
    }

    [CommandMethod(nameof(Run))]
    public void Run() {
        if (_commandMethods.Count == 0) {
            Application.ShowAlertDialog("当前未加载任何可执行的函数，请先执行LoadDll加载您的DLL");
            return;
        }

        var action = _commandMethods.First().Value;
        action.Invoke();
        // var currentDoc = Application.DocumentManager.MdiActiveDocument;
        // var ed = currentDoc.Editor;
        // var pKeyOpts = new PromptKeywordOptions("\n请选择要执行的函数") { AllowNone = true };
        // var index = 1;
        // foreach (var commandMethod in _commandMethods) {
        //     pKeyOpts.Keywords.Add(commandMethod.Key, index.ToString(), commandMethod.Key + "("+ index +")");
        //     index++;
        // }
        //
        // var pKeyRes = ed.GetKeywords(pKeyOpts);
        // if (_commandMethods.TryGetValue(pKeyRes.StringResult, out var method)) {
        //     method.Invoke();
        // }
    }
}