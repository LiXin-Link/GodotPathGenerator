#if TOOLS
#define TOOL_DEBUG

using System;
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using static Godot.GD;

[Tool]
public class GodotPathGenerator : EditorPlugin
{
    private const string PluginName = "GodotPathGenerator";

    private const string ClassNameRegex = "(?<=[//|\\.])(@*[a-zA-Z0-9]+)(?=\\.cs$)";

    private const string DirPath = "res://script/gpg";

    private EditorInterface _editor;

    /// <summary>
    /// Generated node path
    /// key: script path
    /// value: class name
    /// </summary>
    private readonly Dictionary<string, string> _generatedFilePath = new Dictionary<string, string>();

    /// <summary>
    /// Generated resource path
    /// </summary>
    private readonly HashSet<string> _generatedResPath = new HashSet<string>();

    /// <summary>
    /// types will be generated in Res.cs
    /// </summary>
    private readonly HashSet<string> _resGenerateType = new HashSet<string>
    {
        ".tscn",
    };

    private readonly Directory _dir = new Directory();

    private (string, DateTime) _lastChange = ("", DateTime.Now);

    public override void _EnterTree()
    {
        _editor = GetEditorInterface();
        EditorFileSystem fileSystem = _editor.GetResourceFilesystem();
        FileSystemDock fileSystemDock = _editor.GetFileSystemDock();

        fileSystem.Connect("filesystem_changed", this, nameof(OnFilesystemChanged));

        fileSystemDock.Connect("files_moved", this, nameof(OnFilesMoved));
        fileSystemDock.Connect("file_removed", this, nameof(OnFileRemoved));
        fileSystemDock.Connect("folder_removed", this, nameof(OnFolderRemoved));

        Print($"{PluginName}: Start Plugin");
    }

    private void OnFilesystemChanged()
    {
        CreateEditingSceneNodePath();

#if TOOL_DEBUG
        Print("OnFilesystemChanged");
#endif
    }

    #region create node path

    /// <summary>
    /// Create the path of the scene you are currently editing
    /// </summary>
    private void CreateEditingSceneNodePath()
    {
        Node root = _editor.GetEditedSceneRoot();
        if (root == null) return;

        if (root.Name == _lastChange.Item1 && (DateTime.Now - _lastChange.Item2).Milliseconds < 100)
        {
            return;
        }

        var reference = root.GetScript();

        if (reference is CSharpScript script
            && _dir.FileExists(script.ResourcePath)
            && _dir.FileExists(root.Filename))
        {
            var resourcePath = script.ResourcePath;
            var matchCollection = Regex.Matches(resourcePath, ClassNameRegex);

            if (matchCollection.Count > 0)
            {
                var nameMatch = matchCollection[0];
                var pathList = new List<string>();

                TraversalChildren("", root, pathList);
                var generated = StartGeneratePath(nameMatch.ToString(), pathList);

                if (generated)
                {
                    if (_generatedFilePath.ContainsKey(resourcePath))
                    {
                        _generatedFilePath[resourcePath] = nameMatch.ToString();
                    }
                    else
                    {
                        _generatedFilePath.Add(resourcePath, nameMatch.ToString());
                    }

                    if (ShouldGenerate(root.Filename))
                    {
                        _generatedResPath.Add(root.Filename);
                        GenerateResPath();
                    }

                    Print($"{PluginName}: generated node path");
                }
            }
        }

        _lastChange.Item1 = root.Name;
        _lastChange.Item2 = DateTime.Now;
    }

    private void TraversalChildren(string prefix, Node start, List<string> pathList)
    {
        var path = prefix + "/" + start.Name;
        pathList.Add(path);

        var children = start.GetChildren();
        if (children == null || children.Count <= 0) return;

        foreach (var child in children)
        {
            if (child is Node node)
            {
                TraversalChildren(path, node, pathList);
            }
        }
    }

    private bool StartGeneratePath(string className, List<string> pathList)
    {
        string fileName = $"{className}Path.cs";
        string filePath = DirPath + "/" + fileName;

        var file = new File();
        try
        {
            if (!_dir.DirExists(DirPath))
            {
                if (_dir.MakeDirRecursive(DirPath) != Error.Ok)
                {
                    PrintErr($"{PluginName}: can't create '{DirPath}' dir");
                    return false;
                }
            }

            if (_dir.Open(DirPath) == Error.Ok)
            {
                if (file.Open(filePath, File.ModeFlags.Write) != Error.Ok)
                {
                    PrintErr($"{PluginName}: can't create file '{filePath}'");
                    return false;
                }

                file.StoreString("/// <summary>");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("/// Don't modify this file, let plugin update it");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("/// Created by GodotPathGenerator");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("/// </summary>");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("public static partial class GPG");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("{");
                file.StoreString(System.Environment.NewLine);

                WriteOneClass(file, className, pathList);

                file.StoreString("}");
                file.Flush();

                file.Close();
                return true;
            }
            else
            {
                PrintErr($"{PluginName}: can't open '{DirPath}' dir");
                return false;
            }
        }
        finally
        {
            if (file.IsOpen())
            {
                file.Close();
            }
        }
    }

    private void WriteOneClass(File file, string className, List<string> pathList)
    {
        file.StoreString($"    public static class {className}Path");
        file.StoreString(System.Environment.NewLine);
        file.StoreString("    {");
        file.StoreString(System.Environment.NewLine);

        foreach (var path in pathList)
        {
            var fieldName = path.Replace("/", "_");

            file.StoreString($"        public const string {fieldName} = \"/root{path}\";");
            file.StoreString(System.Environment.NewLine);
        }

        file.StoreString("    }");
        file.StoreString(System.Environment.NewLine);
    }

    #endregion

    #region file move and remove

    public void OnFileRemoved(string filePath)
    {
        if (_generatedFilePath.ContainsKey(filePath))
        {
            var className = _generatedFilePath[filePath];
            RemovePathFileByClassName(className);

            _generatedFilePath.Remove(filePath);
        }

        if (_generatedResPath.Contains(filePath))
        {
            _generatedResPath.Remove(filePath);
            GenerateResPath();
        }
        
#if TOOL_DEBUG
        Print($"OnFileRemoved; filePath: {filePath}");
#endif
    }

    public void OnFilesMoved(string oldFile, string newFile)
    {
        if (_generatedFilePath.ContainsKey(oldFile))
        {
            var oldClassNameMatchCollection = Regex.Matches(oldFile, ClassNameRegex);
            var newClassNameMatchCollection = Regex.Matches(newFile, ClassNameRegex);

            if (oldClassNameMatchCollection.Count > 0 && newClassNameMatchCollection.Count > 0)
            {
                var oldClassNameMatch = oldClassNameMatchCollection[0];
                var newClassNameMatch = newClassNameMatchCollection[0];
                if (oldClassNameMatch == newClassNameMatch)
                {
                    return;
                }

                string oldPath = $"{DirPath}/{oldClassNameMatch}Path.cs";
                string newPath = $"{DirPath}/{newClassNameMatch}Path.cs";

                _dir.Rename(oldPath, newPath);
            }
        }
        
        if (_generatedResPath.Contains(oldFile) && !_generatedResPath.Contains(newFile))
        {
            _generatedResPath.Remove(oldFile);
            _generatedResPath.Add(newFile);
            GenerateResPath();
        }

#if TOOL_DEBUG
        Print($"OnFilesMoved; oldFile: {oldFile}, newFile: {newFile}");
#endif
    }

    public void OnFolderRemoved(string folder)
    {
        var classNameList = _generatedFilePath.Keys
            .Where(path => path.StartsWith(folder))
            .ToList();
        foreach (var scriptPath in classNameList)
        {
            RemovePathFileByClassName(_generatedFilePath[scriptPath]);
            _generatedFilePath.Remove(scriptPath);
        }

        if (_generatedResPath.RemoveWhere(path => path.StartsWith(folder)) > 0)
        {
            GenerateResPath();
        }
        

#if TOOL_DEBUG
        Print($"OnFolderRemoved; folder: {folder}");
#endif
    }

    private void RemovePathFileByClassName(string className)
    {
        string classPath = $"{DirPath}/{className}Path.cs";

        if (_dir.FileExists(classPath))
        {
            if (_dir.Remove(classPath) == Error.Ok)
            {
#if TOOL_DEBUG
                Print($"{PluginName}: remove a node path file");
#endif
            }
        }
    }

    #endregion


    private bool ShouldGenerate(string resPath)
    {
        if (!_resGenerateType.Any(resPath.EndsWith))
        {
            return false;
        }

        return !_generatedResPath.Contains(resPath);
    }

    private bool GenerateResPath()
    {
        string fileName = "Res.cs";
        string filePath = DirPath + "/" + fileName;

        var file = new File();
        try
        {
            if (!_dir.DirExists(DirPath))
            {
                if (_dir.MakeDirRecursive(DirPath) != Error.Ok)
                {
                    PrintErr($"{PluginName}: can't create '{DirPath}' dir");
                    return false;
                }
            }

            if (_dir.Open(DirPath) == Error.Ok)
            {
                if (file.Open(filePath, File.ModeFlags.Write) != Error.Ok)
                {
                    PrintErr($"{PluginName}: can't create file '{filePath}'");
                    return false;
                }

                file.StoreString("/// <summary>");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("/// Don't modify this file, let plugin update it");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("/// Created by GodotPathGenerator");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("/// </summary>");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("public static partial class GPG");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("{");
                file.StoreString(System.Environment.NewLine);

                file.StoreString("    public static class Res");
                file.StoreString(System.Environment.NewLine);
                file.StoreString("    {");
                file.StoreString(System.Environment.NewLine);

                foreach (var path in _generatedResPath)
                {
                    var fieldName = path.Replace("res:/", "")
                        .Replace(".", "_")
                        .Replace("/", "_");

                    file.StoreString($"        public const string {fieldName} = \"{path}\";");
                    file.StoreString(System.Environment.NewLine);
                }

                file.StoreString("    }");
                file.StoreString(System.Environment.NewLine);

                file.StoreString("}");
                file.Flush();

                file.Close();
                return true;
            }
            else
            {
                PrintErr($"{PluginName}: can't open '{DirPath}' dir");
                return false;
            }
        }
        finally
        {
            if (file.IsOpen())
            {
                file.Close();
            }
        }
    }
}
#endif