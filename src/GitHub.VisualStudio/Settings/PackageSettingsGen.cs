﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


using GitHub.Settings;
using GitHub.Primitives;
using GitHub.VisualStudio.Helpers;

namespace GitHub.VisualStudio.Settings
{

    public partial class PackageSettings : NotificationAwareObject, IPackageSettings
    {

        bool collectMetrics;
        public bool CollectMetrics
        {
            get { return collectMetrics; }
            set { collectMetrics = value; this.RaisePropertyChange(); }
        }

        bool editorComments;
        public bool EditorComments
        {
            get { return editorComments; }
            set { editorComments = value; this.RaisePropertyChange(); }
        }

        bool forkButton;
        public bool ForkButton
        {
            get { return forkButton; }
            set { forkButton = value; this.RaisePropertyChange(); }
        }

        UIState uIState;
        public UIState UIState
        {
            get { return uIState; }
            set { uIState = value; this.RaisePropertyChange(); }
        }

        bool hideTeamExplorerWelcomeMessage;
        public bool HideTeamExplorerWelcomeMessage
        {
            get { return hideTeamExplorerWelcomeMessage; }
            set { hideTeamExplorerWelcomeMessage = value; this.RaisePropertyChange(); }
        }

        bool enableTraceLogging;
        public bool EnableTraceLogging
        {
            get { return enableTraceLogging; }
            set { enableTraceLogging = value; this.RaisePropertyChange(); }
        }


        void LoadSettings()
        {
            CollectMetrics = (bool)settingsStore.Read("CollectMetrics", true);
            EditorComments = (bool)settingsStore.Read("EditorComments", false);
            ForkButton = (bool)settingsStore.Read("ForkButton", false);
            UIState = SimpleJson.DeserializeObject<UIState>((string)settingsStore.Read("UIState", "{}"));
            HideTeamExplorerWelcomeMessage = (bool)settingsStore.Read("HideTeamExplorerWelcomeMessage", false);
            EnableTraceLogging = (bool)settingsStore.Read("EnableTraceLogging", false);
        }

        void SaveSettings()
        {
            settingsStore.Write("CollectMetrics", CollectMetrics);
            settingsStore.Write("EditorComments", EditorComments);
            settingsStore.Write("ForkButton", ForkButton);
            settingsStore.Write("UIState", SimpleJson.SerializeObject(UIState));
            settingsStore.Write("HideTeamExplorerWelcomeMessage", HideTeamExplorerWelcomeMessage);
            settingsStore.Write("EnableTraceLogging", EnableTraceLogging);
        }

    }
}
