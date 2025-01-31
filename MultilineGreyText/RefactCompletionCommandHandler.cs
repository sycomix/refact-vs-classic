﻿using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System.Text.RegularExpressions;

namespace RefactAI{

    internal class RefactCompletionCommandHandler : IOleCommandTarget{
      
        //LanguageClientMetadata is needed to manually load LSP
        private class LanguageClientMetadata : ILanguageClientMetadata{
            public LanguageClientMetadata(string[] contentTypes, string clientName = null){
                this.ContentTypes = contentTypes;
                this.ClientName = clientName;
            }

            public string ClientName { get; }

            public IEnumerable<string> ContentTypes { get; }
        }

        private IOleCommandTarget m_nextCommandHandler;
        private ITextView m_textView;
        private ICompletionSession m_session;
        private IVsTextView textViewAdapter;
        private ITextDocument doc;

        private RefactCompletionHandlerProvider m_provider;
        private RefactLanguageClient client = null;
        private String filePath;
        private Uri fileURI;
        private int version = 0;

        private bool hasCompletionUpdated = false;

        //The command Handler processes keyboard input.
        internal RefactCompletionCommandHandler(IVsTextView textViewAdapter, ITextView textView, RefactCompletionHandlerProvider provider){
            this.m_textView = textView;
            this.m_provider = provider;
            this.textViewAdapter = textViewAdapter;
           
            var topBuffer = textView.BufferGraph.TopBuffer;
            var projectionBuffer = topBuffer as IProjectionBufferBase;
            var typeName = topBuffer.GetType();
            ITextBuffer textBuffer = projectionBuffer != null ? projectionBuffer.SourceBuffers[0] : topBuffer;
            provider.documentFactory.TryGetTextDocument(textBuffer, out doc);
            this.fileURI = new Uri(doc.FilePath);
            this.filePath = this.fileURI.ToString();
           
            LoadLsp(this.filePath, doc);

            //add the command to the command chain
            textViewAdapter.AddCommandFilter(this, out m_nextCommandHandler);            
        }

        //Starts the refactlsp manually
        //Needed mostly for C/C++ 
        //some other languages don't start the refactlsp consistently but c/c++ appears to never start the lsp
        void LoadLsp(String file, ITextDocument doc){
            IComponentModel componentModel = (IComponentModel)m_provider.ServiceProvider.GetService(typeof(SComponentModel));
            ILanguageClientBroker clientBroker = componentModel.GetService<ILanguageClientBroker>();
            this.client = componentModel.GetExtensions<ILanguageClient>().ToList().Where((c) => c is RefactLanguageClient).FirstOrDefault() as RefactLanguageClient; ;
            if (!client.loaded){
                Task.Run(() => clientBroker.LoadAsync(new LanguageClientMetadata(new string[] { CodeRemoteContentDefinition.CodeRemoteBaseTypeName }), client));
            }
        }

        //Adds file to LSP
        void ConnectFileToLSP(){
            if (!client.ContainsFile(filePath)){
                client.AddFile(filePath, doc.TextBuffer.CurrentSnapshot.GetText());

                //listen for changes
                ((ITextBuffer2)doc.TextBuffer).ChangedHighPriority += ChangeEvent;
            }
        }

        private MultilineGreyTextTagger GetTagger(){
            var key = typeof(MultilineGreyTextTagger);
            var props = m_textView.TextBuffer.Properties;
            if (props.ContainsProperty(key)){
                return props.GetProperty<MultilineGreyTextTagger>(key);
            }else{
                return null;
            }
        }

        //Send changes to LSP
        private void ChangeEvent(object sender, TextContentChangedEventArgs args){
            version++;
            
            //converts the changelist to be readable by LSP
            TextDocumentContentChangeEvent[] contentChanges = args.Changes.Reverse().Select<ITextChange, TextDocumentContentChangeEvent>(change => {
                int startLine, startColumn;
                textViewAdapter.GetLineAndColumn(change.OldSpan.Start, out startLine, out startColumn);
                int endLine, endColumn;
                textViewAdapter.GetLineAndColumn(change.OldSpan.End, out endLine, out endColumn);
                
                return new TextDocumentContentChangeEvent{
                    Text = change.NewText,
                    Range = new Range{
                        Start = new Position(startLine, startColumn),
                        End = new Position(endLine, endColumn)
                    },
                    RangeLength = change.OldSpan.Length
                };
            }).ToArray();

            //sends changes to LSP
            if (contentChanges.Length > 0){
                contentChanges[0].Text = m_textView.TextBuffer.CurrentSnapshot.GetText();
                this.client.InvokeTextDocumentDidChangeAsync(fileURI, version, contentChanges);
            }
        }

        //required by interface just boiler plate
        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText){
            return m_nextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        public bool IsInline(int lineN){
            var text = m_textView.TextSnapshot.GetLineFromLineNumber(lineN).GetText();
            return !String.IsNullOrWhiteSpace(text);
        }

        //gets recommendations from LSP
        public void GetLSPCompletions(){
           if (!General.Instance.PauseCompletion){
                SnapshotPoint? caretPoint = m_textView.Caret.Position.Point.GetPoint(textBuffer => (!textBuffer.ContentType.IsOfType("projection")), PositionAffinity.Predecessor);

                if (caretPoint.HasValue){
                    int lineN;
                    int characterN;
                    int res = textViewAdapter.GetCaretPos(out lineN, out characterN);

                    if (res == VSConstants.S_OK && RefactLanguageClient.Instance != null){
                        //Make sure caret is at the end of a line
                        String untrimLine = m_textView.TextBuffer.CurrentSnapshot.GetLineFromLineNumber(lineN).GetText();
                        if(characterN < untrimLine.Length){
                            String afterCaret = untrimLine.Substring(characterN);
                            String escapedSymbols = Regex.Escape(":(){ },.\"\';");

                            String pattern ="[\\s\\t\\n\\r" + escapedSymbols + "]*";
                            Match m = Regex.Match(afterCaret, pattern, RegexOptions.IgnoreCase);
                            if(!m.Success)
                                return;
                        }

                        if (!client.ContainsFile(filePath)){
                            ConnectFileToLSP();
                        }

                        hasCompletionUpdated = false;
                        bool multiline = !IsInline(lineN);
                        var refactRes = client.RefactCompletion(m_textView.TextBuffer.Properties, filePath, lineN, multiline ? 0 : characterN, multiline);
                        ShowRefactSuggestion(refactRes, new Tuple<int, int>(lineN, characterN));
                    }
                }
            }
        }

        //sends lsp reccomendations to grey text tagger to be dispalyed 
        public async void ShowRefactSuggestion(Task<string> res, Object position){
            var p = position as Tuple<int, int>;
            int lineN = p.Item1;
            int characterN = p.Item2;

            String s = await res;
            if (res != null){
                //the caret must be in a non-projection location 
                SnapshotPoint? caretPoint = m_textView.Caret.Position.Point.GetPoint(textBuffer => (!textBuffer.ContentType.IsOfType("projection")), PositionAffinity.Predecessor);
                if (!caretPoint.HasValue){
                    return;
                }

                int newLineN;
                int newCharacterN;
                int resCaretPos = textViewAdapter.GetCaretPos(out newLineN, out newCharacterN);

                //double checks the cursor is still on the line the recommendation is for
                if(resCaretPos != VSConstants.S_OK || (lineN != newLineN) || (characterN != newCharacterN)){
                    return;
                }

                var tagger = GetTagger();
                if(tagger != null && s != null){
                    tagger.SetSuggestion(s, IsInline(lineN), characterN);
                }
            }
        }

        //Used to detect when the user interacts with the intellisense popup
        void CheckSuggestionUpdate(uint nCmdID){
            switch (nCmdID){
                case ((uint)VSConstants.VSStd2KCmdID.UP):
                case ((uint)VSConstants.VSStd2KCmdID.DOWN):
                case ((uint)VSConstants.VSStd2KCmdID.PAGEUP):
                case ((uint)VSConstants.VSStd2KCmdID.PAGEDN):
                    if (m_provider.CompletionBroker.IsCompletionActive(m_textView)){
                        hasCompletionUpdated = true;
                    }

                    break;
                case ((uint)VSConstants.VSStd2KCmdID.TAB):
                case ((uint)VSConstants.VSStd2KCmdID.RETURN):
                    hasCompletionUpdated = false;
                    break;
            }
        }

        //Key input handler
        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut){
            //let the other handlers handle automation functions
            if (VsShellUtilities.IsInAutomationFunction(m_provider.ServiceProvider)){
                return m_nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }

            //check for a commit character
            if (!hasCompletionUpdated && nCmdID == (uint)VSConstants.VSStd2KCmdID.TAB){

                var tagger = GetTagger();

                if (tagger != null){
                    if (tagger.IsSuggestionActive() && tagger.CompleteText()){                        
                        ClearCompletionSessions();
                        return VSConstants.S_OK;
                    }else{
                        tagger.ClearSuggestion();
                    }
                }

            }else if(nCmdID == (uint)VSConstants.VSStd2KCmdID.RETURN || nCmdID == (uint)VSConstants.VSStd2KCmdID.CANCEL){
                var tagger = GetTagger();
                if (tagger != null){
                    tagger.ClearSuggestion();
                }
            }

            CheckSuggestionUpdate(nCmdID);
            //make a copy of this so we can look at it after forwarding some commands
            uint commandID = nCmdID;
            char typedChar = char.MinValue;

            //make sure the input is a char before getting it
            if (pguidCmdGroup == VSConstants.VSStd2K && nCmdID == (uint)VSConstants.VSStd2KCmdID.TYPECHAR){
                typedChar = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
            }

            //pass along the command so the char is added to the buffer
            int retVal = m_nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            bool handled = false;

            //gets lsp completions on added character or deletions
            if (!typedChar.Equals(char.MinValue) || commandID == (uint)VSConstants.VSStd2KCmdID.RETURN){
                GetLSPCompletions();
                handled = true;
            }else if (commandID == (uint)VSConstants.VSStd2KCmdID.BACKSPACE || commandID == (uint)VSConstants.VSStd2KCmdID.DELETE){
                GetLSPCompletions();
                handled = true;
            }

            if (handled) return VSConstants.S_OK;
            return retVal;
        }

        //clears the intellisense popup window
        void ClearCompletionSessions(){
            m_provider.CompletionBroker.DismissAllSessions(m_textView);
        }

    }
}
