﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Akavache;
using GitHub.Api;
using GitHub.Caches;
using GitHub.Collections;
using GitHub.Extensions;
using GitHub.Extensions.Reactive;
using GitHub.Logging;
using GitHub.Models;
using GitHub.Primitives;
using Octokit;
using Octokit.GraphQL;
using Serilog;
using static Octokit.GraphQL.Variable;

namespace GitHub.Services
{
    [Export(typeof(IModelService))]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class ModelService : IModelService
    {
        static readonly ILogger log = LogManager.ForContext<ModelService>();

        public const string PRPrefix = "pr";
        const string TempFilesDirectory = Info.ApplicationInfo.ApplicationName;
        const string CachedFilesDirectory = "CachedFiles";

        readonly IBlobCache hostCache;
        readonly IAvatarProvider avatarProvider;
        readonly Octokit.GraphQL.IConnection graphql;

        public ModelService(
            IApiClient apiClient,
            Octokit.GraphQL.IConnection graphql,
            IBlobCache hostCache,
            IAvatarProvider avatarProvider)
        {
            this.ApiClient = apiClient;
            this.graphql = graphql;
            this.hostCache = hostCache;
            this.avatarProvider = avatarProvider;
        }

        public IApiClient ApiClient { get; }

        public IObservable<IAccount> GetCurrentUser()
        {
            return GetUserFromCache().Select(Create);
        }

        public IObservable<IAccount> GetUser(string login)
        {
            return hostCache.GetAndRefreshObject("user|" + login,
                () => ApiClient.GetUser(login).Select(AccountCacheItem.Create), TimeSpan.FromMinutes(5), TimeSpan.FromDays(7))
                .Select(Create);
        }

        public IObservable<GitIgnoreItem> GetGitIgnoreTemplates()
        {
            return Observable.Defer(() =>
                hostCache.GetAndFetchLatestFromIndex(CacheIndex.GitIgnoresPrefix, () =>
                        GetGitIgnoreTemplatesFromApi(),
                        item => { },
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromDays(7))
                )
                .Select(Create)
                .Catch<GitIgnoreItem, Exception>(e =>
                {
                    log.Error(e, "Failed to retrieve GitIgnoreTemplates");
                    return Observable.Empty<GitIgnoreItem>();
                });
        }

        public IObservable<LicenseItem> GetLicenses()
        {
            return Observable.Defer(() =>
                hostCache.GetAndFetchLatestFromIndex(CacheIndex.LicensesPrefix, () =>
                        GetLicensesFromApi(),
                        item => { },
                        TimeSpan.FromMinutes(1),
                        TimeSpan.FromDays(7))
                )
                .Select(Create)
                .Catch<LicenseItem, Exception>(e =>
                {
                    log.Error(e, "Failed to retrieve licenses");
                    return Observable.Empty<LicenseItem>();
                });
        }

        public IObservable<IReadOnlyList<IAccount>> GetAccounts()
        {
            return Observable.Zip(
                GetUser(),
                GetUserOrganizations(),
                (user, orgs) => user.Concat(orgs))
            .ToReadOnlyList(Create);
        }

        public IObservable<IRemoteRepositoryModel> GetForks(IRepositoryModel repository)
        {
            return ApiClient.GetForks(repository.Owner, repository.Name)
                .Select(x => new RemoteRepositoryModel(x));
        }

        IObservable<LicenseCacheItem> GetLicensesFromApi()
        {
            return ApiClient.GetLicenses()
                .WhereNotNull()
                .Select(LicenseCacheItem.Create);
        }

        IObservable<GitIgnoreCacheItem> GetGitIgnoreTemplatesFromApi()
        {
            return ApiClient.GetGitIgnoreTemplates()
                .WhereNotNull()
                .Select(GitIgnoreCacheItem.Create);
        }

        IObservable<IEnumerable<AccountCacheItem>> GetUser()
        {
            return hostCache.GetAndRefreshObject("user",
                () => ApiClient.GetUser().Select(AccountCacheItem.Create), TimeSpan.FromMinutes(5), TimeSpan.FromDays(7))
                .TakeLast(1)
                .ToList();
        }

        IObservable<IEnumerable<AccountCacheItem>> GetUserOrganizations()
        {
            return GetUserFromCache().SelectMany(user =>
                hostCache.GetAndRefreshObject(user.Login + "|orgs",
                    () => ApiClient.GetOrganizations().Select(AccountCacheItem.Create).ToList(),
                    TimeSpan.FromMinutes(2), TimeSpan.FromDays(7)))
                // TODO: Akavache returns the cached version followed by the fresh version if > 2
                // minutes have expired from the last request. Here we make sure the latest value is
                // returned but it's a hack. We really need a better way to cache this stuff.
                .TakeLast(1)
                .Catch<IEnumerable<AccountCacheItem>, KeyNotFoundException>(
                    // This could in theory happen if we try to call this before the user is logged in.
                    e =>
                    {
                        log.Error(e, "Retrieve user organizations failed because user is not stored in the cache");
                        return Observable.Return(Enumerable.Empty<AccountCacheItem>());
                    })
                 .Catch<IEnumerable<AccountCacheItem>, Exception>(e =>
                 {
                     log.Error(e, "Retrieve user organizations failed");
                     return Observable.Return(Enumerable.Empty<AccountCacheItem>());
                 });
        }

        public IObservable<IReadOnlyList<IRemoteRepositoryModel>> GetRepositories()
        {
            return GetUserRepositories(RepositoryType.Owner)
                .TakeLast(1)
                .Concat(GetUserRepositories(RepositoryType.Member).TakeLast(1))
                .Concat(GetAllRepositoriesForAllOrganizations());
        }

        IObservable<AccountCacheItem> GetUserFromCache()
        {
            return Observable.Defer(() => hostCache.GetObject<AccountCacheItem>("user"));
        }

        /// <summary>
        /// Gets a collection of Pull Requests. If you want to refresh existing data, pass a collection in
        /// </summary>
        /// <param name="repo"></param>
        /// <param name="collection"></param>
        /// <returns></returns>
        public ITrackingCollection<IPullRequestModel> GetPullRequests(IRepositoryModel repo,
            ITrackingCollection<IPullRequestModel> collection)
        {
            // Since the api to list pull requests returns all the data for each pr, cache each pr in its own entry
            // and also cache an index that contains all the keys for each pr. This way we can fetch prs in bulk
            // but also individually without duplicating information. We store things in a custom observable collection
            // that checks whether an item is being updated (coming from the live stream after being retrieved from cache)
            // and replaces it instead of appending, so items get refreshed in-place as they come in.

            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}:{2}", CacheIndex.PRPrefix, repo.Owner, repo.Name));

            var source = Observable.Defer(() => keyobs
                .SelectMany(key =>
                    hostCache.GetAndFetchLatestFromIndex(key, () =>
                        ApiClient.GetPullRequestsForRepository(repo.CloneUrl.Owner, repo.CloneUrl.RepositoryName)
                                 .Select(PullRequestCacheItem.Create),
                        item =>
                        {
                            if (collection.Disposed) return;

                            // this could blow up due to the collection being disposed somewhere else
                            try { collection.RemoveItem(Create(item)); }
                            catch (ObjectDisposedException) { }
                        },
                        TimeSpan.Zero,
                        TimeSpan.FromDays(7))
                )
                .Select(Create)
            );

            collection.Listen(source);
            return collection;
        }

        public IObservable<IPullRequestModel> GetPullRequest(string owner, string name, int number)
        {
            return Observable.Defer(() =>
            {
                return hostCache.GetAndRefreshObject(PRPrefix + '|' + number, () =>
                        Observable.CombineLatest(
                            ApiClient.GetPullRequest(owner, name, number),
                            ApiClient.GetPullRequestFiles(owner, name, number).ToList(),
                            ApiClient.GetIssueComments(owner, name, number).ToList(),
                            GetPullRequestReviews(owner, name, number).ToObservable(),
                            GetPullRequestReviewComments(owner, name, number).ToObservable(),
                            (pr, files, comments, reviews, reviewComments) => new
                            {
                                PullRequest = pr,
                                Files = files,
                                Comments = comments,
                                Reviews = reviews,
                                ReviewComments = reviewComments
                            })
                            .Select(x => PullRequestCacheItem.Create(
                                x.PullRequest, 
                                (IReadOnlyList<PullRequestFile>)x.Files,
                                (IReadOnlyList<IssueComment>)x.Comments,
                                (IReadOnlyList<IPullRequestReviewModel>)x.Reviews,
                                (IReadOnlyList<IPullRequestReviewCommentModel>)x.ReviewComments)),
                        TimeSpan.Zero,
                        TimeSpan.FromDays(7))
                    .Select(Create);
            });
        }

        public IObservable<IRemoteRepositoryModel> GetRepository(string owner, string repo)
        {
            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}/{3}", CacheIndex.RepoPrefix, user.Login, owner, repo));

            return Observable.Defer(() => keyobs
                .SelectMany(key =>
                    hostCache.GetAndFetchLatest(
                        key,
                        () => ApiClient.GetRepository(owner, repo).Select(RepositoryCacheItem.Create))
                    .Select(Create)));
        }

        public ITrackingCollection<IRemoteRepositoryModel> GetRepositories(ITrackingCollection<IRemoteRepositoryModel> collection)
        {
            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}", CacheIndex.RepoPrefix, user.Login));

            var source = Observable.Defer(() => keyobs
                .SelectMany(key =>
                    hostCache.GetAndFetchLatestFromIndex(key, () =>
                        ApiClient.GetRepositories()
                                 .Select(RepositoryCacheItem.Create),
                        item =>
                        {
                            if (collection.Disposed) return;

                            // this could blow up due to the collection being disposed somewhere else
                            try { collection.RemoveItem(Create(item)); }
                            catch (ObjectDisposedException) { }
                        },
                        TimeSpan.FromMinutes(5),
                        TimeSpan.FromDays(1))
                )
                .Select(Create)
            );

            collection.Listen(source);
            return collection;
        }

        public IObservable<IPullRequestModel> CreatePullRequest(ILocalRepositoryModel sourceRepository, IRepositoryModel targetRepository,
            IBranch sourceBranch, IBranch targetBranch,
            string title, string body)
        {
            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}:{2}", CacheIndex.PRPrefix, targetRepository.Owner, targetRepository.Name));

            return Observable.Defer(() => keyobs
                .SelectMany(key =>
                    hostCache.PutAndUpdateIndex(key, () =>
                        ApiClient.CreatePullRequest(
                                new NewPullRequest(title,
                                                   string.Format(CultureInfo.InvariantCulture, "{0}:{1}", sourceRepository.Owner, sourceBranch.Name),
                                                   targetBranch.Name)
                                                   { Body = body },
                                targetRepository.Owner,
                                targetRepository.Name)
                            .Select(PullRequestCacheItem.Create)
                        ,
                        TimeSpan.FromMinutes(30))
                )
                .Select(Create)
            );
        }

        public IObservable<Unit> InvalidateAll()
        {
            return hostCache.InvalidateAll().ContinueAfter(() => hostCache.Vacuum());
        }

        public IObservable<string> GetFileContents(IRepositoryModel repo, string commitSha, string path, string fileSha)
        {
            return Observable.Defer(() => Task.Run(async () =>
            {
                // Store cached file contents a the temp directory so they can be deleted by disk cleanup etc.
                var tempDir = Path.Combine(Path.GetTempPath(), TempFilesDirectory, CachedFilesDirectory, fileSha.Substring(0, 2));
                var tempFile = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(path) + '@' + fileSha + Path.GetExtension(path));

                if (!File.Exists(tempFile))
                {
                    var contents = await ApiClient.GetFileContents(repo.Owner, repo.Name, commitSha, path);
                    Directory.CreateDirectory(tempDir);
                    File.WriteAllBytes(tempFile, Convert.FromBase64String(contents.EncodedContent));
                }

                return Observable.Return(tempFile);
            }));
        }

        IObservable<IReadOnlyList<IRemoteRepositoryModel>> GetUserRepositories(RepositoryType repositoryType)
        {
            return Observable.Defer(() => GetUserFromCache().SelectMany(user =>
                hostCache.GetAndRefreshObject(string.Format(CultureInfo.InvariantCulture, "{0}|{1}:repos", user.Login, repositoryType),
                    () => GetUserRepositoriesFromApi(repositoryType),
                        TimeSpan.FromMinutes(2),
                        TimeSpan.FromDays(7)))
                .ToReadOnlyList(Create))
                .Catch<IReadOnlyList<IRemoteRepositoryModel>, KeyNotFoundException>(
                    // This could in theory happen if we try to call this before the user is logged in.
                    e =>
                    {
                        log.Error(e,
                            "Retrieving {RepositoryType} user repositories failed because user is not stored in the cache",
                            repositoryType);
                        return Observable.Return(new IRemoteRepositoryModel[] {});
                    });
        }

        IObservable<IEnumerable<RepositoryCacheItem>> GetUserRepositoriesFromApi(RepositoryType repositoryType)
        {
            return ApiClient.GetUserRepositories(repositoryType)
                .WhereNotNull()
                .Select(RepositoryCacheItem.Create)
                .ToList()
                .Catch<IEnumerable<RepositoryCacheItem>, Exception>(_ => Observable.Return(Enumerable.Empty<RepositoryCacheItem>()));
        }

        IObservable<IReadOnlyList<IRemoteRepositoryModel>> GetAllRepositoriesForAllOrganizations()
        {
            return GetUserOrganizations()
                .SelectMany(org => org.ToObservable())
                .SelectMany(org => GetOrganizationRepositories(org.Login).TakeLast(1));
        }

        IObservable<IReadOnlyList<IRemoteRepositoryModel>> GetOrganizationRepositories(string organization)
        {
            return Observable.Defer(() => GetUserFromCache().SelectMany(user =>
                hostCache.GetAndRefreshObject(string.Format(CultureInfo.InvariantCulture, "{0}|{1}|repos", user.Login, organization),
                    () => ApiClient.GetRepositoriesForOrganization(organization).Select(
                        RepositoryCacheItem.Create).ToList(),
                        TimeSpan.FromMinutes(2),
                        TimeSpan.FromDays(7)))
                .ToReadOnlyList(Create))
                .Catch<IReadOnlyList<IRemoteRepositoryModel>, KeyNotFoundException>(
                    // This could in theory happen if we try to call this before the user is logged in.
                    e =>
                    {
                        log.Error(e, "Retrieveing {Organization} org repositories failed because user is not stored in the cache",
                            organization);
                        return Observable.Return(new IRemoteRepositoryModel[] {});
                    });
        }

#pragma warning disable CS0618 // DatabaseId is marked obsolete by GraphQL but we need it
        async Task<IList<IPullRequestReviewModel>> GetPullRequestReviews(string owner, string name, int number)
        {
            string cursor = null;
            var result = new List<IPullRequestReviewModel>();

            while (true)
            {
                var query = new Query()
                    .Repository(owner, name)
                    .PullRequest(number)
                    .Reviews(first: 30, after: cursor)
                    .Select(x => new
                    {
                        x.PageInfo.HasNextPage,
                        x.PageInfo.EndCursor,
                        Items = x.Nodes.Select(y => new PullRequestReviewModel
                        {
                            Id = y.DatabaseId.Value,
                            NodeId = y.Id,
                            Body = y.Body,
                            CommitId = y.Commit.Oid,
                            State = FromGraphQL(y.State),
                            SubmittedAt = y.SubmittedAt,
                            User = Create(y.Author.Login, y.Author.AvatarUrl(null))
                        }).ToList()
                    });

                var page = await graphql.Run(query);
                result.AddRange(page.Items);

                if (page.HasNextPage)
                    cursor = page.EndCursor;
                else
                    return result;
            }
        }

        async Task<IList<IPullRequestReviewCommentModel>> GetPullRequestReviewComments(string owner, string name, int number)
        {
            var result = new List<IPullRequestReviewCommentModel>();

            // Reads a single page of reviews and for each review the first page of review comments.
            var query = new Query()
                .Repository(owner, name)
                .PullRequest(number)
                .Reviews(first: 100, after: Var("cursor"))
                .Select(x => new
                {
                    x.PageInfo.HasNextPage,
                    x.PageInfo.EndCursor,
                    Reviews = x.Nodes.Select(y => new
                    {
                        y.Id,
                        CommentPage = y.Comments(100, null, null, null).Select(z => new
                        {
                            z.PageInfo.HasNextPage,
                            z.PageInfo.EndCursor,
                            Items = z.Nodes.Select(a => new PullRequestReviewCommentModel
                            {
                                Id = a.DatabaseId.Value,
                                NodeId = a.Id,
                                Body = a.Body,
                                CommitId = a.Commit.Oid,
                                CreatedAt = a.CreatedAt.Value,
                                DiffHunk = a.DiffHunk,
                                OriginalCommitId = a.OriginalCommit.Oid,
                                OriginalPosition = a.OriginalPosition,
                                Path = a.Path,
                                Position = a.Position,
                                PullRequestReviewId = y.DatabaseId.Value,
                                User = Create(a.Author.Login, a.Author.AvatarUrl(null)),
                                IsPending = y.State == Octokit.GraphQL.Model.PullRequestReviewState.Pending,
                            }).ToList(),
                        }).Single()
                    }).ToList()
                }).Compile();

            var vars = new Dictionary<string, object>
            {
                { "cursor", null }
            };

            // Read all pages of reviews.
            while (true)
            {
                var reviewPage = await graphql.Run(query, vars);

                foreach (var review in reviewPage.Reviews)
                {
                    result.AddRange(review.CommentPage.Items);

                    // The the review has >1 page of review comments, read the remaining pages.
                    if (review.CommentPage.HasNextPage)
                    {
                        result.AddRange(await GetPullRequestReviewComments(review.Id, review.CommentPage.EndCursor));
                    }
                }

                if (reviewPage.HasNextPage)
                    vars["cursor"] = reviewPage.EndCursor;
                else
                    return result;
            }
        }

        private async Task<IEnumerable<IPullRequestReviewCommentModel>> GetPullRequestReviewComments(string reviewId, string commentCursor)
        {
            var result = new List<IPullRequestReviewCommentModel>();
            var query = new Query()
                .Node(reviewId)
                .Cast<Octokit.GraphQL.Model.PullRequestReview>()
                .Select(x => new
                {
                    CommentPage = x.Comments(100, Var("cursor"), null, null).Select(z => new
                    {
                        z.PageInfo.HasNextPage,
                        z.PageInfo.EndCursor,
                        Items = z.Nodes.Select(a => new PullRequestReviewCommentModel
                        {
                            Id = a.DatabaseId.Value,
                            NodeId = a.Id,
                            Body = a.Body,
                            CommitId = a.Commit.Oid,
                            CreatedAt = a.CreatedAt.Value,
                            DiffHunk = a.DiffHunk,
                            OriginalCommitId = a.OriginalCommit.Oid,
                            OriginalPosition = a.OriginalPosition,
                            Path = a.Path,
                            Position = a.Position,
                            PullRequestReviewId = x.DatabaseId.Value,
                            User = Create(a.Author.Login, a.Author.AvatarUrl(null)),
                        }).ToList(),
                    }).Single()
                }).Compile();
            var vars = new Dictionary<string, object>
            {
                { "cursor", commentCursor }
            };

            while (true)
            {
                var page = await graphql.Run(query, vars);
                result.AddRange(page.CommentPage.Items);

                if (page.CommentPage.HasNextPage)
                    vars["cursor"] = page.CommentPage.EndCursor;
                else
                    return result;
            }
        }
#pragma warning restore CS0618 // Type or member is obsolete

        public IObservable<IBranch> GetBranches(IRepositoryModel repo)
        {
            var keyobs = GetUserFromCache()
                .Select(user => string.Format(CultureInfo.InvariantCulture, "{0}|{1}|branch", user.Login, repo.Name));

            return Observable.Defer(() => keyobs
                    .SelectMany(key => ApiClient.GetBranches(repo.CloneUrl.Owner, repo.CloneUrl.RepositoryName)))
                .Select(x => new BranchModel(x, repo));
        }

        static GitIgnoreItem Create(GitIgnoreCacheItem item)
        {
            return GitIgnoreItem.Create(item.Name);
        }

        static LicenseItem Create(LicenseCacheItem licenseCacheItem)
        {
            return new LicenseItem(licenseCacheItem.Key, licenseCacheItem.Name);
        }

        IAccount Create(AccountCacheItem accountCacheItem)
        {
            return new Models.Account(
                accountCacheItem.Login,
                accountCacheItem.IsUser,
                accountCacheItem.IsEnterprise,
                accountCacheItem.OwnedPrivateRepositoriesCount,
                accountCacheItem.PrivateRepositoriesInPlanCount,
                accountCacheItem.AvatarUrl,
                avatarProvider.GetAvatar(accountCacheItem));
        }

        IAccount Create(string login, string avatarUrl)
        {
            return new Models.Account(
                login,
                true,
                false,
                0,
                0,
                avatarUrl,
                avatarProvider.GetAvatar(avatarUrl));
        }

        IRemoteRepositoryModel Create(RepositoryCacheItem item)
        {
            return new RemoteRepositoryModel(
                item.Id,
                item.Name,
                new UriString(item.CloneUrl),
                item.Private,
                item.Fork,
                Create(item.Owner),
                item.Parent != null ? Create(item.Parent) : null)
            {
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt
            };
        }

        GitReferenceModel Create(GitReferenceCacheItem item)
        {
            return new GitReferenceModel(item.Ref, item.Label, item.Sha, item.RepositoryCloneUrl);
        }

        IPullRequestModel Create(PullRequestCacheItem prCacheItem)
        {
            return new PullRequestModel(
                prCacheItem.Number,
                prCacheItem.Title,
                Create(prCacheItem.Author),
                prCacheItem.CreatedAt,
                prCacheItem.UpdatedAt)
            {
                Assignee = prCacheItem.Assignee != null ? Create(prCacheItem.Assignee) : null,
                Base = Create(prCacheItem.Base),
                Body = prCacheItem.Body ?? string.Empty,
                ChangedFiles = prCacheItem.ChangedFiles.Select(x => 
                    (IPullRequestFileModel)new PullRequestFileModel(x.FileName, x.Sha, x.Status)).ToList(),
                Comments = prCacheItem.Comments.Select(x =>
                    (ICommentModel)new IssueCommentModel
                    {
                        Id = x.Id,
                        Body = x.Body,
                        User = Create(x.User),
                        CreatedAt = x.CreatedAt ?? DateTimeOffset.MinValue,
                    }).ToList(),
                Reviews = prCacheItem.Reviews.Select(x =>
                    (IPullRequestReviewModel)new PullRequestReviewModel
                    {
                        Id = x.Id,
                        NodeId = x.NodeId,
                        User = Create(x.User),
                        Body = x.Body,
                        State = x.State,
                        CommitId = x.CommitId,
                        SubmittedAt = x.SubmittedAt,
                    }).ToList(),
                ReviewComments = prCacheItem.ReviewComments.Select(x =>
                    (IPullRequestReviewCommentModel)new PullRequestReviewCommentModel
                    {
                        Id = x.Id,
                        NodeId = x.NodeId,
                        PullRequestReviewId = x.PullRequestReviewId,
                        Path = x.Path,
                        Position = x.Position,
                        OriginalPosition = x.OriginalPosition,
                        CommitId = x.CommitId,
                        OriginalCommitId = x.OriginalCommitId,
                        DiffHunk = x.DiffHunk,
                        User = Create(x.User),
                        Body = x.Body,
                        CreatedAt = x.CreatedAt,
                        IsPending = x.IsPending,
                    }).ToList(),
                CommentCount = prCacheItem.CommentCount,
                CommitCount = prCacheItem.CommitCount,
                CreatedAt = prCacheItem.CreatedAt,
                Head = Create(prCacheItem.Head),
                State = prCacheItem.State.HasValue ? 
                    prCacheItem.State.Value : 
                    prCacheItem.IsOpen.Value ? PullRequestStateEnum.Open : PullRequestStateEnum.Closed,                
            };
        }

        public IObservable<Unit> InsertUser(AccountCacheItem user)
        {
            return hostCache.InsertObject("user", user);
        }

        protected virtual void Dispose(bool disposing)
        {}

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        static GitHub.Models.PullRequestReviewState FromGraphQL(Octokit.GraphQL.Model.PullRequestReviewState s)
        {
            return (GitHub.Models.PullRequestReviewState)s;
        }

        public class GitIgnoreCacheItem : CacheItem
        {
            public static GitIgnoreCacheItem Create(string ignore)
            {
                return new GitIgnoreCacheItem { Key = ignore, Name = ignore, Timestamp = DateTime.Now };
            }

            public string Name { get; set; }
        }


        public class LicenseCacheItem : CacheItem
        {
            public static LicenseCacheItem Create(LicenseMetadata licenseMetadata)
            {
                return new LicenseCacheItem { Key = licenseMetadata.Key, Name = licenseMetadata.Name, Timestamp = DateTime.Now };
            }

            public string Name { get; set; }
        }

        public class RepositoryCacheItem : CacheItem
        {
            public static RepositoryCacheItem Create(Repository apiRepository)
            {
                return new RepositoryCacheItem(apiRepository);
            }

            public RepositoryCacheItem() {}

            public RepositoryCacheItem(Repository apiRepository)
            {
                Id = apiRepository.Id;
                Name = apiRepository.Name;
                Owner = AccountCacheItem.Create(apiRepository.Owner);
                CloneUrl = apiRepository.CloneUrl;
                Private = apiRepository.Private;
                Fork = apiRepository.Fork;
                Key = string.Format(CultureInfo.InvariantCulture, "{0}/{1}", Owner.Login, Name);
                CreatedAt = apiRepository.CreatedAt;
                UpdatedAt = apiRepository.UpdatedAt;
                Timestamp = apiRepository.UpdatedAt;
                Parent = apiRepository.Parent != null ? new RepositoryCacheItem(apiRepository.Parent) : null;
            }

            public long Id { get; set; }

            public string Name { get; set; }
            public AccountCacheItem Owner { get; set; }
            public string CloneUrl { get; set; }
            public bool Private { get; set; }
            public bool Fork { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public RepositoryCacheItem Parent { get; set; }
        }

        public class PullRequestCacheItem : CacheItem
        {
            public static PullRequestCacheItem Create(PullRequest pr)
            {
                return new PullRequestCacheItem(
                    pr,
                    new PullRequestFile[0],
                    new IssueComment[0],
                    new IPullRequestReviewModel[0],
                    new IPullRequestReviewCommentModel[0]);
            }

            public static PullRequestCacheItem Create(
                PullRequest pr,
                IReadOnlyList<PullRequestFile> files,
                IReadOnlyList<IssueComment> comments,
                IReadOnlyList<IPullRequestReviewModel> reviews,
                IReadOnlyList<IPullRequestReviewCommentModel> reviewComments)
            {
                return new PullRequestCacheItem(pr, files, comments, reviews, reviewComments);
            }

            public PullRequestCacheItem() {}

            public PullRequestCacheItem(PullRequest pr)
                : this(pr, new PullRequestFile[0], new IssueComment[0], new IPullRequestReviewModel[0], new IPullRequestReviewCommentModel[0])
            {
            }

            public PullRequestCacheItem(
                PullRequest pr,
                IReadOnlyList<PullRequestFile> files,
                IReadOnlyList<IssueComment> comments,
                IReadOnlyList<IPullRequestReviewModel> reviews,
                IReadOnlyList<IPullRequestReviewCommentModel> reviewComments)
            {
                Title = pr.Title;
                Number = pr.Number;
                Base = new GitReferenceCacheItem
                {
                    Label = pr.Base.Label,
                    Ref = pr.Base.Ref,
                    Sha = pr.Base.Sha,
                    RepositoryCloneUrl = pr.Base.Repository.CloneUrl,
                };
                Head = new GitReferenceCacheItem
                {
                    Label = pr.Head.Label,
                    Ref = pr.Head.Ref,
                    Sha = pr.Head.Sha,
                    RepositoryCloneUrl = pr.Head.Repository?.CloneUrl
                };
                CommentCount = pr.Comments;
                CommitCount = pr.Commits;
                Author = new AccountCacheItem(pr.User);
                Assignee = pr.Assignee != null ? new AccountCacheItem(pr.Assignee) : null;
                CreatedAt = pr.CreatedAt;
                UpdatedAt = pr.UpdatedAt;
                Body = pr.Body;
                ChangedFiles = files.Select(x => new PullRequestFileCacheItem(x)).ToList();
                Comments = comments.Select(x => new IssueCommentCacheItem(x)).ToList();
                Reviews = reviews.Select(x => new PullRequestReviewCacheItem(x)).ToList();
                ReviewComments = reviewComments.Select(x => new PullRequestReviewCommentCacheItem(x)).ToList();
                State = GetState(pr);
                IsOpen = pr.State == ItemState.Open;
                Merged = pr.Merged;
                Key = Number.ToString(CultureInfo.InvariantCulture);
                Timestamp = UpdatedAt;
            }

            public string Title {get; set; }
            public int Number { get; set; }
            public GitReferenceCacheItem Base { get; set; }
            public GitReferenceCacheItem Head { get; set; }
            public int CommentCount { get; set; }
            public int CommitCount { get; set; }
            public AccountCacheItem Author { get; set; }
            public AccountCacheItem Assignee { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public string Body { get; set; }
            public IList<PullRequestFileCacheItem> ChangedFiles { get; set; } = new PullRequestFileCacheItem[0];
            public IList<IssueCommentCacheItem> Comments { get; set; } = new IssueCommentCacheItem[0];
            public IList<PullRequestReviewCacheItem> Reviews { get; set; } = new PullRequestReviewCacheItem[0];
            public IList<PullRequestReviewCommentCacheItem> ReviewComments { get; set; } = new PullRequestReviewCommentCacheItem[0];

            // Nullable for compatibility with old caches.
            public PullRequestStateEnum? State { get; set; }

            // This fields exists only for compatibility with old caches. The State property should be used.
            public bool? IsOpen { get; set; }
            public bool? Merged { get; set; }

            static PullRequestStateEnum GetState(PullRequest pullRequest)
            {
                if (pullRequest.State == ItemState.Open)
                {
                    return PullRequestStateEnum.Open;
                }
                else if (pullRequest.Merged)
                {
                    return PullRequestStateEnum.Merged;
                }
                else
                {
                    return PullRequestStateEnum.Closed;
                }
            }
        }

        public class PullRequestFileCacheItem
        {
            public PullRequestFileCacheItem()
            {
            }

            public PullRequestFileCacheItem(PullRequestFile file)
            {
                FileName = file.FileName;
                Sha = file.Sha;
                Status = (PullRequestFileStatus)Enum.Parse(typeof(PullRequestFileStatus), file.Status, true);
            }

            public string FileName { get; set; }
            public string Sha { get; set; }
            public PullRequestFileStatus Status { get; set; }
        }

        public class IssueCommentCacheItem
        {
            public IssueCommentCacheItem()
            {
            }

            public IssueCommentCacheItem(IssueComment comment)
            {
                Id = comment.Id;
                User = new AccountCacheItem(comment.User);
                Body = comment.Body;
                CreatedAt = comment.CreatedAt;
            }

            public int Id { get; }
            public AccountCacheItem User { get; set; }
            public string Body { get; set; }
            public DateTimeOffset? CreatedAt { get; set; }
        }

        public class PullRequestReviewCacheItem
        {
            public PullRequestReviewCacheItem()
            {
            }

            public PullRequestReviewCacheItem(IPullRequestReviewModel review)
            {
                Id = review.Id;
                NodeId = review.NodeId;
                User = new AccountCacheItem
                {
                    Login = review.User.Login,
                    AvatarUrl = review.User.AvatarUrl,
                };
                Body = review.Body;
                State = review.State;
                SubmittedAt = review.SubmittedAt;
            }

            public long Id { get; set; }
            public string NodeId { get; set; }
            public AccountCacheItem User { get; set; }
            public string Body { get; set; }
            public GitHub.Models.PullRequestReviewState State { get; set; }
            public string CommitId { get; set; }
            public DateTimeOffset? SubmittedAt { get; set; }
        }

        public class PullRequestReviewCommentCacheItem
        {
            public PullRequestReviewCommentCacheItem()
            {
            }

            public PullRequestReviewCommentCacheItem(IPullRequestReviewCommentModel comment)
            {
                Id = comment.Id;
                NodeId = comment.NodeId;
                PullRequestReviewId = comment.PullRequestReviewId;
                Path = comment.Path;
                Position = comment.Position;
                OriginalPosition = comment.OriginalPosition;
                CommitId = comment.CommitId;
                OriginalCommitId = comment.OriginalCommitId;
                DiffHunk = comment.DiffHunk;
                User = new AccountCacheItem
                {
                    Login = comment.User.Login,
                    AvatarUrl = comment.User.AvatarUrl,
                };
                Body = comment.Body;
                CreatedAt = comment.CreatedAt;
                IsPending = comment.IsPending;
            }

            public int Id { get; }
            public string NodeId { get; }
            public int PullRequestReviewId { get; set; }
            public string Path { get; set; }
            public int? Position { get; set; }
            public int? OriginalPosition { get; set; }
            public string CommitId { get; set; }
            public string OriginalCommitId { get; set; }
            public string DiffHunk { get; set; }
            public AccountCacheItem User { get; set; }
            public string Body { get; set; }
            public DateTimeOffset CreatedAt { get; set; }
            public bool IsPending { get; set; }
        }

        public class GitReferenceCacheItem
        {
            public string Ref { get; set; }
            public string Label { get; set; }
            public string Sha { get; set; }
            public string RepositoryCloneUrl { get; set; }
        }
    }
}
