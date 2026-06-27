using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using System;
using System.Threading.Tasks;

namespace SlangClient
{
    public class SlangServerMessageTarget
    {
        internal readonly static SlangServerMessageTarget Instance = new SlangServerMessageTarget();

        // slangd pulls its settings via a "workspace/configuration" request right after
        // initialize (before any document opens). The VS language-client framework does not
        // answer this for us, so without a handler StreamJsonRpc replies with
        //   error -32601 "No method by the name 'workspace/configuration' is found."
        // and slangd never learns slang.additionalSearchPaths / slang.predefinedMacros from
        // slangdconfig.json (includes like "csg/device_records.h" then fail to resolve).
        // We answer it here, mirroring the VS Code client.
        [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
        public object[] OnWorkspaceConfiguration(JToken args)
        {
            SlangLanguageClient client = SlangLanguageClient.Instance;
            return client != null ? client.ProvideConfiguration(args) : new object[0];
        }
    }
}
