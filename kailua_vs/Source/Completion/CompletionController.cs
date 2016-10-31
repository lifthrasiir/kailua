﻿using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace Kailua
{
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("kailua")]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    internal sealed class VsTextViewCreationListener : IVsTextViewCreationListener
    {
        [Import]
        IVsEditorAdaptersFactoryService AdaptersFactory = null;

        [Import]
        ICompletionBroker CompletionBroker = null;

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            IWpfTextView view = AdaptersFactory.GetWpfTextView(textViewAdapter);
            Trace.Assert(view != null);

            CommandFilter filter = new CommandFilter(view, CompletionBroker);

            IOleCommandTarget next;
            textViewAdapter.AddCommandFilter(filter, out next);
            filter.Next = next;
        }
    }

    internal sealed class CommandFilter : IOleCommandTarget
    {
        ICompletionSession currentSession;

        public CommandFilter(IWpfTextView textView, ICompletionBroker broker)
        {
            this.currentSession = null;

            this.TextView = textView;
            this.Broker = broker;
        }

        public IWpfTextView TextView { get; private set; }
        public ICompletionBroker Broker { get; private set; }
        public IOleCommandTarget Next { get; set; }

        private char getTypeChar(IntPtr pvaIn)
        {
            return (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            bool handled = false;
            int hresult = VSConstants.S_OK;

            // handle the existing session
            if (pguidCmdGroup == VSConstants.VSStd2K)
            {
                switch ((VSConstants.VSStd2KCmdID)nCmdID)
                {
                    case VSConstants.VSStd2KCmdID.AUTOCOMPLETE:
                    case VSConstants.VSStd2KCmdID.COMPLETEWORD:
                        handled = this.StartSession();
                        break;
                    case VSConstants.VSStd2KCmdID.RETURN:
                        handled = this.Complete(false);
                        break;
                    case VSConstants.VSStd2KCmdID.TAB:
                        handled = this.Complete(true);
                        break;
                    case VSConstants.VSStd2KCmdID.CANCEL:
                        handled = this.Cancel();
                        break;
                }
            }

            if (!handled)
            {
                hresult = this.Next.Exec(pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }

            // start the new session if needed
            if (ErrorHandler.Succeeded(hresult) && pguidCmdGroup == VSConstants.VSStd2K)
            {
                switch ((VSConstants.VSStd2KCmdID)nCmdID)
                {
                    case VSConstants.VSStd2KCmdID.TYPECHAR:
                        char ch = this.getTypeChar(pvaIn);
                        if (char.IsLetterOrDigit(ch))
                        {
                            if (this.currentSession == null)
                            {
                                this.StartSession();
                            }
                            else
                            {
                                this.Filter();
                            }
                        }
                        else
                        {
                            this.Cancel();
                        }
                        break;
                    case VSConstants.VSStd2KCmdID.BACKSPACE:
                    case VSConstants.VSStd2KCmdID.DELETE:
                        this.Filter();
                        break;
                }
            }

            return hresult;
        }

        internal void Filter()
        {
            if (this.currentSession == null)
            {
                return;
            }

            this.currentSession.SelectedCompletionSet.SelectBestMatch();
            this.currentSession.SelectedCompletionSet.Recalculate();
        }

        internal bool Cancel()
        {
            if (this.currentSession == null)
            {
                return false;
            }

            this.currentSession.Dismiss();
            return true;
        }

        internal bool Complete(bool force)
        {
            if (this.currentSession == null)
            {
                return false;
            }

            if (!this.currentSession.SelectedCompletionSet.SelectionStatus.IsSelected && !force)
            {
                this.currentSession.Dismiss();
                return false;
            }
            else
            {
                this.currentSession.Commit();
                return true;
            }
        }

        internal bool StartSession()
        {
            if (this.currentSession != null)
            {
                return false;
            }

            SnapshotPoint caret = this.TextView.Caret.Position.BufferPosition;
            ITextSnapshot snapshot = caret.Snapshot;

            if (!this.Broker.IsCompletionActive(this.TextView))
            {
                var triggerPoint = snapshot.CreateTrackingPoint(caret, PointTrackingMode.Positive);
                this.currentSession = this.Broker.CreateCompletionSession(this.TextView, triggerPoint, true);
            }
            else
            {
                this.currentSession = this.Broker.GetSessions(this.TextView)[0];
            }
            this.currentSession.Dismissed += (sender, args) => this.currentSession = null;

            this.currentSession.Start();
            return true;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup == VSConstants.VSStd2K)
            {
                switch ((VSConstants.VSStd2KCmdID)prgCmds[0].cmdID)
                {
                    case VSConstants.VSStd2KCmdID.AUTOCOMPLETE:
                    case VSConstants.VSStd2KCmdID.COMPLETEWORD:
                        prgCmds[0].cmdf = (uint)OLECMDF.OLECMDF_ENABLED | (uint)OLECMDF.OLECMDF_SUPPORTED;
                        return VSConstants.S_OK;
                }
            }
            return this.Next.QueryStatus(pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }
    }
}
