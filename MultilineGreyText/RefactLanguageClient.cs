﻿using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Utilities;
using StreamJsonRpc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace RefactAI{

    //the lsp client for refact
    //any means the lsp should start up for any file extension
    [ContentType("any")]
    [Export(typeof(ILanguageClient))]
    [RunOnContext(RunningContext.RunOnHost)]

    public class RefactLanguageClient : ILanguageClient, ILanguageClientCustomMessage2{
        private Connection c;

        //lsp instance
        internal static RefactLanguageClient Instance{
            get;
            set;
        }

        //rpc for sending requests to the lsp
        internal JsonRpc Rpc{
            get;
            set;
        }

        //checks if lsp has started to load used to detect presence of lsp
        public bool loaded = false;

        //StartAsync used to start the lsp
        public event AsyncEventHandler<EventArgs> StartAsync;

        //StopAsync used to stop the lsp
        public event AsyncEventHandler<EventArgs> StopAsync;

        //name of lsp
        public string Name => "Refact Language Extension";

        //intialization options
        public object InitializationOptions => null;

        //files to watch 
        public IEnumerable<string> FilesToWatch => null;

        //middle layer used to intercep messages to/from lsp
        public object MiddleLayer => RefactMiddleLayer.Instance;

        //custom message target
        public object CustomMessageTarget => null;

        //show notification on initialize failed setting
        public bool ShowNotificationOnInitializeFailed => true;

        //files lsp is aware of 
        internal HashSet<String> files;

        //constructor
        public RefactLanguageClient(){
            Instance = this;
            files = new HashSet<string>();
        }

        //gets/sets lsp configuration sections
        public IEnumerable<string> ConfigurationSections{
            get{
                yield return "";
            }
        }

        //sends file to lsp and adds it to known file set        
        public async void AddFile(String filePath, String text){

            //wait for the rpc 
            while (Rpc == null) await Task.Delay(1);

            //dont send the file to the lsp if the lsp already knows about it
            if (ContainsFile(filePath)){
                return;
            }

            //add file to known file set
            files.Add(filePath);

            //message to send to lsp
            var openParam = new DidOpenTextDocumentParams{
                TextDocument = new TextDocumentItem{
                    Uri = new Uri(filePath),
                    LanguageId = filePath.Substring(filePath.LastIndexOf(".") + 1),
                    Version = 0,
                    Text = text
                }
            };

            //send message to lsp catch any communication errors
            try{
                await Rpc.NotifyWithParameterObjectAsync("textDocument/didChange", openParam);
            }catch (Exception e){
                Debug.Write("InvokeTextDocumentDidChangeAsync Server Exception " + e.ToString());
            }
        }

        //does lsp know about the file?
        public bool ContainsFile(String file){
            return files.Contains(file);
        }

        //activates the lsp using stdin/stdout to communicate with it
        public async Task<Connection> ActivateAsync(CancellationToken token){
            ProcessStartInfo info = new ProcessStartInfo();

            info.FileName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Resources", @"refact-lsp.exe");

            info.Arguments = GetArgs();
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;

            //tells the lsp not to show the window
            //turning this off can be useful for debugging
            info.CreateNoWindow = true;

            //starts the lsp process
            Process process = new Process();
            process.StartInfo = info;

            if (process.Start()){
                //returns the connection for future use
                this.c = new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
                return c;
            }

            return null;
        }

        //get command line args for the lsp
        String GetArgs(){
            String args = "";
            if (General.Instance.TelemetryBasic){
                args += "--basic-telemetry ";
            }

            if (General.Instance.TelemetryCodeSnippets){
                args += "--snippet-telemetry ";
            }

            args += "--address-url " + (String.IsNullOrWhiteSpace(General.Instance.AddressURL) ? "Refact" : General.Instance.AddressURL) + " ";
            args += "--api-key " + (String.IsNullOrWhiteSpace(General.Instance.APIKey) ? "ZZZWWW" : General.Instance.APIKey) + " ";

            return args + "--http-port 8001 --lsp-stdin-stdout 1";
        }

        //used to start loading lsp
        public async Task OnLoadedAsync(){
            if (StartAsync != null){
                loaded = true;
                await StartAsync.InvokeAsync(this, EventArgs.Empty);
            }
        }

        //stops the lsp
        public async Task StopServerAsync(){
            if (StopAsync != null){
                await StopAsync.InvokeAsync(this, EventArgs.Empty);
            }
        }

        //returns the completed task when the lsp has finished loading
        public Task OnServerInitializedAsync(){
            return Task.CompletedTask;
        }

        //used to set up custom messages 
        public Task AttachForCustomMessageAsync(JsonRpc rpc){
            this.Rpc = rpc;
            return Task.CompletedTask;
        }

        //server initialize failed
        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState){
            string message = "Oh no! Refact Language Client failed to activate, now we can't test LSP! :(";
            string exception = initializationState.InitializationException?.ToString() ?? string.Empty;
            message = $"{message}\n {exception}";

            var failureContext = new InitializationFailureContext(){
                FailureMessage = message,
            };

            return Task.FromResult(failureContext);
        }

        //manually sends change message to lsp
        public async void InvokeTextDocumentDidChangeAsync(Uri fileURI, int version, TextDocumentContentChangeEvent[] contentChanges){
            if (Rpc != null && ContainsFile(fileURI.ToString())){
                var changesParam = new DidChangeTextDocumentParams{
                    ContentChanges = contentChanges,
                    TextDocument = new VersionedTextDocumentIdentifier{
                        Version = version,
                        Uri = fileURI,
                    }
                };

                try{
                    await Rpc.NotifyWithParameterObjectAsync("textDocument/didChange", changesParam);
                }catch(Exception e){
                    Debug.Write("InvokeTextDocumentDidChangeAsync Server Exception " + e.ToString());
                }
            }
        }

        public async Task<string> RefactCompletion(PropertyCollection props, String fileUri, int lineN, int character, bool multiline){
            //Make sure lsp has finished loading
            if(this.Rpc == null){
                return null;
            }

            //catching server errors
            try{
                //args to send for refact/getCompletions
                var argObj2 = new{
                    text_document_position = new {
                        textDocument = new { uri = fileUri },
                        position = new { line = lineN, character = character },
                    },
                    parameters = new { max_new_tokens = 50, temperature = 0.2f },
                    multiline = multiline,
                    textDocument = new { uri = fileUri },
                    position = new{ line = lineN, character = character }
                };
                
                var res = await this.Rpc.InvokeWithParameterObjectAsync<JToken>("refact/getCompletions", argObj2);

                //process results
                List<String> suggestions = new List<String>();
                foreach (var s in res["choices"]){
                    suggestions.Add(s["code_completion"].ToString());
                }

                return suggestions[0];
            }catch (Exception e){
                Debug.Write("Error " + e.ToString());
                return null;
            }
        }

        //ilanguage client middle layer
        internal class RefactMiddleLayer : ILanguageClientMiddleLayer{
            internal readonly static RefactMiddleLayer Instance = new RefactMiddleLayer();

            //returns true if the method should be handled by the middle layer
            public bool CanHandle(string methodName){
                return true;
            }

            //intercepts new files and adds them to the knonw file set
            public Task HandleNotificationAsync(string methodName, JToken methodParam, Func<JToken, Task> sendNotification){
                Task t = sendNotification(methodParam);
                if (methodName == "textDocument/didOpen"){
                    RefactLanguageClient.Instance.files.Add(methodParam["textDocument"]["uri"].ToString());
                }
                return t;
            }

            //intercepts requests for completions sent to the lsp
            //returns an empty list to avoid showing default completions
            public async Task<JToken> HandleRequestAsync(string methodName, JToken methodParam, Func<JToken, Task<JToken>> sendRequest){
                var result = await sendRequest(methodParam);
                if(methodName == "textDocument/completion"){
                    return JToken.Parse("[]");
                }else{
                    return result;
                }
            }
        }
    }
}
