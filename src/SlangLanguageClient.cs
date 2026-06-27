using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace SlangClient
{
    [ContentType("slang")]
    [Export(typeof(ILanguageClient))]
    public class SlangLanguageClient : ILanguageClient, ILanguageClientCustomMessage2
    {
        [Import]
        ITextDocumentFactoryService DocumentFactoryService { get; set; }
        [Import]
        ITextEditorFactoryService textEditorFactoryService { get; set; }

        FileSystemWatcher ConfigFileWatcher { get; set; }

        private JoinableTaskFactory joinableTaskFactory;

        public delegate void DiagnosticsReadyEvent(string path);

        public event DiagnosticsReadyEvent DiagnoticsReady;

        public Connection _connection = null;
        public JsonRpc _rpc = null;
        public static SlangLanguageClient Instance = null;

        public string Name => "Slang Language Client Extension";

        public IEnumerable<string> ConfigurationSections
        {
            get
            {
                yield return "slang.predefinedMacros";
                yield return "slang.additionalSearchPaths";
                yield return "slang.enableCommitCharactersInAutoCompletion";
            }
        }

        public object InitializationOptions => null;

        public IEnumerable<string> FilesToWatch => null;

        bool ILanguageClient.ShowNotificationOnInitializeFailed => true;

        object ILanguageClientCustomMessage2.MiddleLayer => SlangMiddleLayer.Instance;

        object ILanguageClientCustomMessage2.CustomMessageTarget => SlangServerMessageTarget.Instance;

        public event AsyncEventHandler<EventArgs> StartAsync;
        public event AsyncEventHandler<EventArgs> StopAsync;

        public object WorkspaceOptions = null;
        public string ClangFormatLocation = null;

        // Completed once slangd's workspace/configuration pull has been answered with the
        // discovered search paths. The didOpen middle-layer awaits this so slangd has the
        // include search paths before it first compiles the document — otherwise it compiles
        // with no search path and caches an unresolved #include that later edits never re-fix.
        private TaskCompletionSource<bool> _configPullAnswered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForConfigPullAsync(int timeoutMs)
        {
            return Task.WhenAny(_configPullAnswered.Task, Task.Delay(timeoutMs));
        }

        public ConcurrentDictionary<string, PublishDiagnosticParams> diagnostics = new ConcurrentDictionary<string, PublishDiagnosticParams>(Environment.ProcessorCount * 2, 32);
        public ConcurrentDictionary<string, ITextView> textViews = new ConcurrentDictionary<string, ITextView>(Environment.ProcessorCount * 2, 32);
        System.Diagnostics.Process process;

        private async Task<string> FindClangFormatAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            IVsShell shellService = (IVsShell)Package.GetGlobalService(typeof(SVsShell));
            if (shellService != null)
            {
                object installDirObj;
                shellService.GetProperty((int)__VSSPROPID.VSSPROPID_InstallDirectory, out installDirObj);

                if (installDirObj != null)
                {
                    var clangformatDir = Path.GetFullPath(Path.Combine(installDirObj.ToString(), @"../../VC/Tools/Llvm/x64/bin/clang-format.exe"));
                    if (File.Exists(clangformatDir))
                    {
                        return clangformatDir;
                    }
                }
            }
            return null;
        }

        public async Task<Connection> ActivateAsync(CancellationToken token)
        {
            joinableTaskFactory = ThreadHelper.JoinableTaskFactory;
            textEditorFactoryService.TextViewCreated += OnTextViewCreated;
            Instance = this;
            await Task.Yield();

            ClangFormatLocation = await FindClangFormatAsync();

            ConfigFileWatcher = null;
            _configPullAnswered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            if (SlangWorkspace.Instance != null)
            {
                await SlangWorkspace.Instance.DoSettingsAsync();
            }

            // Discover slangdconfig.json now, before slangd starts. slangd pulls its
            // settings (workspace/configuration) immediately after initialize and compiles
            // the first opened document right after; if WorkspaceOptions isn't populated yet
            // the pull is answered empty and the include search paths never reach the first
            // compile. Pre-populating here lets ProvideConfiguration answer instantly.
            EnsureConfigurationDiscovered();

            string languageServerPath = Path.GetDirectoryName(typeof(SlangLanguageClient).Assembly.Location);
            languageServerPath = Path.Combine(languageServerPath, "SlangServer", "slangd.exe");
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = languageServerPath;
            info.Arguments = "-vs";
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;

            process = new System.Diagnostics.Process();
            process.StartInfo = info;
            if (process.Start())
            {
#if DEBUG
                _connection = new Connection(new DebugStream(process.StandardOutput.BaseStream), new DebugStream(process.StandardInput.BaseStream));
#else
                _connection = new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
#endif
                return _connection;
            }
            return null;
        }

        public string UriToLocalPath(Uri uri)
        {
            if (uri.IsFile && uri.IsLoopback)
            {
                string localPath = uri.LocalPath;
                if (uri.IsAbsoluteUri)
                {
                    localPath = localPath.TrimStart('/');
                }
                return localPath;
            }
            return null;
        }

        public string GetNewConfigurationPath(Uri fileUri)
        {
            if (fileUri.IsFile)
            {
                string configFilePath = FindConfigFile(UriToLocalPath(fileUri));
                if (configFilePath != null)
                {
                    string currentWatcher = ConfigFileWatcher == null ? "" : Path.Combine(ConfigFileWatcher.Path, ConfigFileWatcher.Filter);
                    if (configFilePath != currentWatcher)
                    {
                        return configFilePath;
                    }
                }
            }
            return null;
        }

        public void NotifyClangFormatLocation()
        {
            // Merge the clang-format location into the existing configuration instead of
            // replacing it. This runs on every server (re)initialize; overwriting here
            // would wipe slang.additionalSearchPaths / slang.predefinedMacros that were
            // already discovered from slangdconfig.json.
            if (ClangFormatLocation != null)
            {
                JObject configObj = WorkspaceOptions as JObject ?? new JObject();
                configObj["slang.format.clangFormatLocation"] = ClangFormatLocation;
                WorkspaceOptions = configObj;
                NotifyConfigChange();
            }
        }

        public void UpdateConfiguration(Uri fileUri)
        {
            string configFilePath = GetNewConfigurationPath(fileUri);
            if (configFilePath != null)
            {
                ConfigFileWatcher = new FileSystemWatcher(Path.GetDirectoryName(configFilePath), Path.GetFileName(configFilePath));
                ConfigFileWatcher.Changed += ConfigFileWatcher_Changed;
                ConfigFileWatcher.EnableRaisingEvents = true;
                ReadAndNotifyConfigurationChange();
            }
        }

        private void ReadAndNotifyConfigurationChange()
        {
            try
            {
                dynamic config = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(Path.Combine(ConfigFileWatcher.Path, ConfigFileWatcher.Filter)));
                if (config == null)
                {
                    return;
                }

                // Convert relative paths in searchPath to absolute paths.
                dynamic searchPath = config?["slang.additionalSearchPaths"];
                if (searchPath != null && searchPath.Count != 0)
                {
                    for (int i = 0; i < searchPath.Count; i++)
                    {
                        try
                        {
                            var pathStr = searchPath[i].ToString();
                            if (!Path.IsPathRooted(pathStr))
                            {
                                pathStr = Path.GetFullPath(Path.Combine(ConfigFileWatcher.Path, pathStr));
                                searchPath[i] = pathStr;
                            }
                        }
                        catch (Exception)
                        { }
                    }
                    config["slang.additionalSearchPaths"] = searchPath;
                }

                dynamic clangFormatLocation = config?["slang.format.clangFormatLocation"];
                if (clangFormatLocation == null && ClangFormatLocation != null)
                {
                    // Use clang-format that comes with visual studio.
                    config["slang.format.clangFormatLocation"] = ClangFormatLocation;
                }
                WorkspaceOptions = config;

                // Send the config to language server.
                NotifyConfigChange();
            }
            catch (Exception)
            {
                WorkspaceOptions = null;
            }
        }

        private void NotifyConfigChange()
        {
            // _rpc is not attached yet during eager discovery in ActivateAsync; the pull
            // handler delivers the config in that case, so skip the (impossible) push.
            if (_rpc == null)
            {
                return;
            }
            DidChangeConfigurationParams configParams = new DidChangeConfigurationParams();
            configParams.Settings = WorkspaceOptions;
            Task task = Task.Run(async () => await _rpc.NotifyAsync(Methods.WorkspaceDidChangeConfigurationName, configParams));
        }

        // Answers slangd's "workspace/configuration" pull (see SlangServerMessageTarget).
        // slangd requests this once right after initialize — before any textDocument/didOpen —
        // and it is the ONLY channel through which it reads slang.additionalSearchPaths /
        // slang.predefinedMacros. Without this the server got a -32601 error and never learned
        // the include search paths from slangdconfig.json. Each requested item carries a
        // "section" (e.g. "slang.additionalSearchPaths"); we return the matching value from the
        // discovered config (search paths already resolved to absolute), or null.
        public object[] ProvideConfiguration(JToken args)
        {
            EnsureConfigurationDiscovered();

            JArray items = args?["items"] as JArray;
            int count = items?.Count ?? 0;
            object[] result = new object[count];

            JObject options = WorkspaceOptions as JObject;
            for (int i = 0; i < count; i++)
            {
                string section = items[i]?["section"]?.ToString();
                result[i] = (options != null && section != null) ? options[section] : null;
            }

            // Unblock didOpen once the search paths have actually been handed to slangd.
            if (options != null && options["slang.additionalSearchPaths"] != null)
            {
                _configPullAnswered.TrySetResult(true);
            }
            return result;
        }

        // The config pull arrives before the first didOpen, so WorkspaceOptions may still be
        // empty. Anchor discovery on any already-open .slang document (the one that activated
        // the language client) and read its slangdconfig.json right now.
        private void EnsureConfigurationDiscovered()
        {
            JObject options = WorkspaceOptions as JObject;
            if (options != null && options["slang.additionalSearchPaths"] != null)
                return;

            IEnumerable<string> openDocs = textViews.Keys;
            if (SlangWorkspace.Instance != null)
                openDocs = openDocs.Concat(SlangWorkspace.Instance.m_TextViewsDictionary.Keys);

            foreach (string key in openDocs.Distinct())
            {
                try
                {
                    Uri uri;
                    if (!Uri.TryCreate(key, UriKind.Absolute, out uri) || !uri.IsFile)
                        continue;

                    string cfg = FindConfigFile(UriToLocalPath(uri));
                    if (cfg == null)
                        continue;

                    string current = ConfigFileWatcher == null ? "" :
                        Path.Combine(ConfigFileWatcher.Path, ConfigFileWatcher.Filter);
                    if (current != cfg)
                    {
                        ConfigFileWatcher = new FileSystemWatcher(Path.GetDirectoryName(cfg), Path.GetFileName(cfg));
                        ConfigFileWatcher.Changed += ConfigFileWatcher_Changed;
                        ConfigFileWatcher.EnableRaisingEvents = true;
                    }
                    ReadAndNotifyConfigurationChange();

                    options = WorkspaceOptions as JObject;
                    if (options != null && options["slang.additionalSearchPaths"] != null)
                        return;
                }
                catch { }
            }
        }

        private void ConfigFileWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            ReadAndNotifyConfigurationChange();
        }

        private string FindConfigFile(string path)
        {
            string currentPath = Path.GetDirectoryName(path);
            string filePath = Path.Combine(currentPath, "slangdconfig.json");
            if (File.Exists(filePath))
            {
                return filePath;
            }
            if (currentPath == Path.GetPathRoot(currentPath))
            {
                return null;
            }
            return FindConfigFile(currentPath);
        }

        public ITextBuffer GetTextBufferFromPath(string path)
        {
            foreach (var x in textViews)
            {
                if (x.Key == path)
                {
                    return x.Value.TextBuffer;
                }
            }
            return null;
        }

        public string GetPathFromView(ITextView view)
        {
            ITextDocument textDocument = null;
            if (DocumentFactoryService.TryGetTextDocument(view.TextBuffer, out textDocument))
            {
                Uri fileUri;
                if (Uri.TryCreate(textDocument.FilePath, UriKind.Absolute, out fileUri))
                {
                    return Uri.UnescapeDataString(fileUri.AbsoluteUri);
                }
            }
            return null;
        }

        public void RaiseDiagnosticsReady(string path)
        {
            DiagnoticsReady?.Invoke(path);
        }


        private void OnTextViewCreated(object sender, TextViewCreatedEventArgs args)
        {
            ITextDocument textDocument = null;
            if (DocumentFactoryService.TryGetTextDocument(args.TextView.TextBuffer, out textDocument))
            {
                Uri fileUri;
                if (Uri.TryCreate(textDocument.FilePath, UriKind.Absolute, out fileUri))
                {
                    textViews.TryAdd(Uri.UnescapeDataString(fileUri.AbsoluteUri), args.TextView);

                    args.TextView.Closed += TextView_Closed;
                }
            }
        }

        private void TextView_Closed(object sender, EventArgs e)
        {
            ITextView view = (ITextView)sender;
            view.Closed -= TextView_Closed;
            var item = textViews.FirstOrDefault(kvp => kvp.Value == view);
            if ( item.Value != null)
            { 
                ITextView removeView;
                textViews.TryRemove(item.Key, out removeView);
            }
        }

        private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            return;
        }

        public async Task OnLoadedAsync()
        {
            await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public Task OnServerInitializeFailedAsync(Exception e)
        {
            return Task.CompletedTask;
        }

        public Task OnServerInitializedAsync()
        {
            NotifyClangFormatLocation();
            return Task.CompletedTask;
        }

        Task<InitializationFailureContext> ILanguageClient.OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState)
        {
            throw new NotImplementedException();
        }

        Task ILanguageClientCustomMessage2.AttachForCustomMessageAsync(JsonRpc rpc)
        {
            _rpc = rpc;
            return Task.CompletedTask;
        }
    }
}
