using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GitHub.Settings
{
    public interface IPackageSettings : INotifyPropertyChanged
    {
        void Save();
        bool CollectMetrics { get; set; }
        bool EditorComments { get; set; }
        bool ForkButton { get; set; }
        UIState UIState { get; set; }
        bool HideTeamExplorerWelcomeMessage { get; set; }
        bool EnableTraceLogging { get; set; }
    }
}