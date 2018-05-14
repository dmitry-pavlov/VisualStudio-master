﻿using System;
using System.Threading.Tasks;
using GitHub.Models;
using Octokit;
using ReactiveUI;
using IConnection = GitHub.Models.IConnection;

namespace GitHub.ViewModels.Dialog
{
    /// <summary>
    /// View model for selecting the account to fork a repository to.
    /// </summary>
    public interface IForkRepositoryExecuteViewModel : IDialogContentViewModel
    {
        IRepositoryModel SourceRepository { get; }

        IRepositoryModel DestinationRepository { get; }

        IAccount DestinationAccount { get; }
      
        /// <summary>
        /// Gets a command that is executed when the user clicks the "Fork" button.
        /// </summary>
        IReactiveCommand<Repository> CreateFork { get; }

        bool ResetMasterTracking { get; set; }

        bool AddUpstream { get; set; }

        bool UpdateOrigin { get; set; }

        bool CanAddUpstream { get; }

        bool CanResetMasterTracking { get; }

        string Error { get; }

        /// <summary>
        /// Initializes the view model.
        /// </summary>
        /// <param name="sourceRepository">The repository to fork.</param>
        /// <param name="destinationAccount">The account to fork to.</param>
        /// <param name="connection">The connection to use.</param>
        Task InitializeAsync(
            ILocalRepositoryModel sourceRepository, 
            IAccount destinationAccount, 
            IConnection connection);
    }
}
