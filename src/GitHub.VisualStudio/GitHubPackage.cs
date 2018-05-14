﻿using System;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel.Design;
using System.ComponentModel.Composition;
using System.Runtime.InteropServices;
using GitHub.Api;
using GitHub.Commands;
using GitHub.Info;
using GitHub.Exports;
using GitHub.Logging;
using GitHub.Services;
using GitHub.Settings;
using GitHub.Services.Vssdk.Commands;
using GitHub.ViewModels.GitHubPane;
using GitHub.VisualStudio.Settings;
using GitHub.VisualStudio.UI;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Serilog;
using Task = System.Threading.Tasks.Task;

namespace GitHub.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("#110", "#112", AssemblyVersionInformation.Version, IconResourceID = 400)]
    [Guid(Guids.guidGitHubPkgString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(Guids.UIContext_Git, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(OptionsPage), "GitHub for Visual Studio", "General", 0, 0, supportsAutomation: true)]
    public class GitHubPackage : AsyncPackage
    {
        static readonly ILogger log = LogManager.ForContext<GitHubPackage>();

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            LogVersionInformation();
            await base.InitializeAsync(cancellationToken, progress);

            await InitializeLoggingAsync();
            await GetServiceAsync(typeof(IUsageTracker));

            // Avoid delays when there is ongoing UI activity.
            // See: https://github.com/github/VisualStudio/issues/1537
            await JoinableTaskFactory.RunAsync(VsTaskRunContext.UIThreadNormalPriority, InitializeMenus);
        }

        async Task InitializeLoggingAsync()
        {
            var packageSettings = await GetServiceAsync(typeof(IPackageSettings)) as IPackageSettings;
            LogManager.EnableTraceLogging(packageSettings?.EnableTraceLogging ?? false);
            if (packageSettings != null)
            {
                packageSettings.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(packageSettings.EnableTraceLogging))
                    {
                        LogManager.EnableTraceLogging(packageSettings.EnableTraceLogging);
                    }
                };
            }
        }

        void LogVersionInformation()
        {
            var packageVersion = ApplicationInfo.GetPackageVersion(this);
            var hostVersionInfo = ApplicationInfo.GetHostVersionInfo();
            log.Information("Initializing GitHub Extension v{PackageVersion} in {$FileDescription} ({$ProductVersion})",
                packageVersion, hostVersionInfo.FileDescription, hostVersionInfo.ProductVersion);
        }

        async Task InitializeMenus()
        {
            var componentModel = (IComponentModel)(await GetServiceAsync(typeof(SComponentModel)));
            var exports = componentModel.DefaultExportProvider;
            var commands = new IVsCommandBase[]
            {
                exports.GetExportedValue<IAddConnectionCommand>(),
                exports.GetExportedValue<IBlameLinkCommand>(),
                exports.GetExportedValue<ICopyLinkCommand>(),
                exports.GetExportedValue<ICreateGistCommand>(),
                exports.GetExportedValue<IOpenLinkCommand>(),
                exports.GetExportedValue<IOpenPullRequestsCommand>(),
                exports.GetExportedValue<IShowCurrentPullRequestCommand>(),
                exports.GetExportedValue<IShowGitHubPaneCommand>(),
                exports.GetExportedValue<ISyncSubmodulesCommand>()
            };

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var menuService = (IMenuCommandService)(await GetServiceAsync(typeof(IMenuCommandService)));
            menuService.AddCommands(commands);
        }

        async Task EnsurePackageLoaded(Guid packageGuid)
        {
            var shell = await GetServiceAsync(typeof(SVsShell)) as IVsShell;
            if (shell != null)
            {
                IVsPackage vsPackage;
                ErrorHandler.ThrowOnFailure(shell.LoadPackage(ref packageGuid, out vsPackage));
            }
        }
    }

    [PartCreationPolicy(CreationPolicy.Shared)]
    public class ServiceProviderExports
    {
        // Only export services for the Visual Studio process (they don't work in Expression Blend).
        const string ProcessName = "devenv";

        readonly IServiceProvider serviceProvider;

        [ImportingConstructor]
        public ServiceProviderExports([Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        [ExportForProcess(typeof(ILoginManager), ProcessName)]
        public ILoginManager LoginManager => GetService<ILoginManager>();

        [ExportForProcess(typeof(IGitHubServiceProvider), ProcessName)]
        public IGitHubServiceProvider GitHubServiceProvider => GetService<IGitHubServiceProvider>();

        [ExportForProcess(typeof(IUsageTracker), ProcessName)]
        public IUsageTracker UsageTracker => GetService<IUsageTracker>();

        [ExportForProcess(typeof(IVSGitExt), ProcessName)]
        public IVSGitExt VSGitExt => GetService<IVSGitExt>();

        [ExportForProcess(typeof(IPackageSettings), ProcessName)]
        public IPackageSettings PackageSettings => GetService<IPackageSettings>();

        T GetService<T>() => (T)serviceProvider.GetService(typeof(T));
    }

    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideService(typeof(ILoginManager), IsAsyncQueryable = true)]
    [ProvideService(typeof(IGitHubServiceProvider), IsAsyncQueryable = true)]
    [ProvideService(typeof(IUsageTracker), IsAsyncQueryable = true)]
    [ProvideService(typeof(IPackageSettings), IsAsyncQueryable = true)]
    [ProvideService(typeof(IUsageService), IsAsyncQueryable = true)]
    [ProvideService(typeof(IVSGitExt), IsAsyncQueryable = true)]
    [ProvideService(typeof(IGitHubToolWindowManager))]
    [Guid(ServiceProviderPackageId)]
    public sealed class ServiceProviderPackage : AsyncPackage, IServiceProviderPackage, IGitHubToolWindowManager
    {
        public const string ServiceProviderPackageId = "D5CE1488-DEDE-426D-9E5B-BFCCFBE33E53";
        static readonly ILogger log = LogManager.ForContext<ServiceProviderPackage>();

        protected override Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            AddService(typeof(IGitHubServiceProvider), CreateService, true);
            AddService(typeof(IVSGitExt), CreateService, true);
            AddService(typeof(IUsageTracker), CreateService, true);
            AddService(typeof(IUsageService), CreateService, true);
            AddService(typeof(ILoginManager), CreateService, true);
            AddService(typeof(IGitHubToolWindowManager), CreateService, true);
            AddService(typeof(IPackageSettings), CreateService, true);
            return Task.CompletedTask;
        }

        public async Task<IGitHubPaneViewModel> ShowGitHubPane()
        {
            var pane = ShowToolWindow(new Guid(GitHubPane.GitHubPaneGuid));
            if (pane == null)
                return null;
            var frame = pane.Frame as IVsWindowFrame;
            if (frame != null)
            {
                ErrorHandler.Failed(frame.Show());
            }

            var gitHubPane = (GitHubPane)pane;
            return await gitHubPane.GetViewModelAsync();
        }

        static ToolWindowPane ShowToolWindow(Guid windowGuid)
        {
            IVsWindowFrame frame;
            if (ErrorHandler.Failed(Services.UIShell.FindToolWindow((uint)__VSCREATETOOLWIN.CTW_fForceCreate,
                ref windowGuid, out frame)))
            {
                log.Error("Unable to find or create GitHubPane '{Guid}'", UI.GitHubPane.GitHubPaneGuid);
                return null;
            }
            if (ErrorHandler.Failed(frame.Show()))
            {
                log.Error("Unable to show GitHubPane '{Guid}'", UI.GitHubPane.GitHubPaneGuid);
                return null;
            }

            object docView = null;
            if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out docView)))
            {
                log.Error("Unable to grab instance of GitHubPane '{Guid}'", UI.GitHubPane.GitHubPaneGuid);
                return null;
            }
            return docView as GitHubPane;
        }

        async Task<object> CreateService(IAsyncServiceContainer container, CancellationToken cancellationToken, Type serviceType)
        {
            if (serviceType == null)
                return null;

            if (container != this)
                return null;

            if (serviceType == typeof(IGitHubServiceProvider))
            {
                //var sp = await GetServiceAsync(typeof(SVsServiceProvider)) as IServiceProvider;
                var result = new GitHubServiceProvider(this, this);
                await result.Initialize();
                return result;
            }
            else if (serviceType == typeof(ILoginManager))
            {
                // These services are got through MEF and we will take a performance hit if ILoginManager is requested during 
                // InitializeAsync. TODO: We can probably make LoginManager a normal MEF component rather than a service.
                var serviceProvider = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                var keychain = serviceProvider.GetService<IKeychain>();
                var oauthListener = serviceProvider.GetService<IOAuthCallbackListener>();

                // HACK: We need to make sure this is run on the main thread. We really
                // shouldn't be injecting a view model concern into LoginManager - this
                // needs to be refactored. See #1398.
                var lazy2Fa = new Lazy<ITwoFactorChallengeHandler>(() =>
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        return serviceProvider.GetService<ITwoFactorChallengeHandler>();
                    }));

                return new LoginManager(
                    keychain,
                    lazy2Fa,
                    oauthListener,
                    ApiClientConfiguration.ClientId,
                    ApiClientConfiguration.ClientSecret,
                    ApiClientConfiguration.RequiredScopes,
                    ApiClientConfiguration.AuthorizationNote,
                    ApiClientConfiguration.MachineFingerprint);
            }
            else if (serviceType == typeof(IUsageService))
            {
                var sp = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                var environment = new Rothko.Environment();
                return new UsageService(sp, environment);
            }
            else if (serviceType == typeof(IUsageTracker))
            {
                var usageService = await GetServiceAsync(typeof(IUsageService)) as IUsageService;
                var serviceProvider = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                var settings = await GetServiceAsync(typeof(IPackageSettings)) as IPackageSettings;
                return new UsageTracker(serviceProvider, usageService, settings);
            }
            else if (serviceType == typeof(IVSGitExt))
            {
                var vsVersion = ApplicationInfo.GetHostVersionInfo().FileMajorPart;
                return new VSGitExtFactory(vsVersion, this).Create();
            }
            else if (serviceType == typeof(IGitHubToolWindowManager))
            {
                return this;
            }
            else if (serviceType == typeof(IPackageSettings))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var sp = new ServiceProvider(Services.Dte as Microsoft.VisualStudio.OLE.Interop.IServiceProvider);
                return new PackageSettings(sp);
            }
            // go the mef route
            else
            {
                var sp = await GetServiceAsync(typeof(IGitHubServiceProvider)) as IGitHubServiceProvider;
                return sp.TryGetService(serviceType);
            }
        }
    }
}
