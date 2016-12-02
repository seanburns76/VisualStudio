﻿using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GitHub.Models;
using GitHub.Primitives;
using GitHub.Services;
using GitHub.Settings;
using GitHub.ViewModels;
using LibGit2Sharp;
using NSubstitute;
using Octokit;
using Xunit;

namespace UnitTests.GitHub.App.ViewModels
{
    public class PullRequestDetailViewModelTests : TestBaseClass
    {
        static readonly Uri Uri = new Uri("http://foo");

        public class TheBodyProperty
        {
            [Fact]
            public async Task ShouldUsePlaceholderBodyIfNoneExists()
            {
                var target = CreateTarget();

                await target.Load(CreatePullRequest(body: string.Empty));

                Assert.Equal("*No description provided.*", target.Body);
            }
        }

        public class TheHeadProperty
        {
            [Fact]
            public async Task ShouldAcceptNullHead()
            {
                var target = CreateTarget();
                var model = CreatePullRequest();

                // PullRequest.Head can be null for example if a user deletes the repository after creating the PR.
                model.Head = null;

                await target.Load(model);

                Assert.Equal("[Invalid]", target.SourceBranchDisplayName);
            }
        }

        public class TheChangedFilesTreeProperty
        {
            [Fact]
            public async Task ShouldCreateChangesTree()
            {
                var target = CreateTarget();
                var pr = CreatePullRequest();

                pr.ChangedFiles = new[]
                {
                new PullRequestFileModel("readme.md", PullRequestFileStatus.Modified),
                new PullRequestFileModel("dir1/f1.cs", PullRequestFileStatus.Modified),
                new PullRequestFileModel("dir1/f2.cs", PullRequestFileStatus.Modified),
                new PullRequestFileModel("dir1/dir1a/f3.cs", PullRequestFileStatus.Modified),
                new PullRequestFileModel("dir2/f4.cs", PullRequestFileStatus.Modified),
            };

                await target.Load(pr);

                Assert.Equal(3, target.ChangedFilesTree.Count);

                var dir1 = (PullRequestDirectoryNode)target.ChangedFilesTree[0];
                Assert.Equal("dir1", dir1.DirectoryName);
                Assert.Equal(2, dir1.Files.Count);
                Assert.Equal(1, dir1.Directories.Count);
                Assert.Equal("f1.cs", dir1.Files[0].FileName);
                Assert.Equal("f2.cs", dir1.Files[1].FileName);
                Assert.Equal("dir1", dir1.Files[0].DirectoryPath);
                Assert.Equal("dir1", dir1.Files[1].DirectoryPath);

                var dir1a = (PullRequestDirectoryNode)dir1.Directories[0];
                Assert.Equal("dir1a", dir1a.DirectoryName);
                Assert.Equal(1, dir1a.Files.Count);
                Assert.Equal(0, dir1a.Directories.Count);

                var dir2 = (PullRequestDirectoryNode)target.ChangedFilesTree[1];
                Assert.Equal("dir2", dir2.DirectoryName);
                Assert.Equal(1, dir2.Files.Count);
                Assert.Equal(0, dir2.Directories.Count);

                var readme = (PullRequestFileNode)target.ChangedFilesTree[2];
                Assert.Equal("readme.md", readme.FileName);
            }
        }

        public class TheCheckoutCommand
        {
            [Fact]
            public async Task CheckedOutAndUpToDate()
            {
                var target = CreateTarget(
                    currentBranch: "pr/123",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                Assert.False(target.Checkout.CanExecute(null));
                Assert.Null(target.CheckoutState);
            }

            [Fact]
            public async Task NotCheckedOut()
            {
                var target = CreateTarget(
                    currentBranch: "master",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                Assert.True(target.Checkout.CanExecute(null));
                Assert.Null(target.CheckoutState.DisabledMessage);
            }

            [Fact]
            public async Task NotCheckedOutWithWorkingDirectoryDirty()
            {
                var target = CreateTarget(
                    currentBranch: "master",
                    existingPrBranch: "pr/123",
                    dirty: true);
                await target.Load(CreatePullRequest());

                Assert.False(target.Checkout.CanExecute(null));
                Assert.Equal("Cannot checkout as your working directory has uncommitted changes.", target.CheckoutState.DisabledMessage);
            }

            [Fact]
            public async Task CheckoutExistingLocalBranch()
            {
                var target = CreateTarget(
                    currentBranch: "master",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest(number: 123));

                Assert.True(target.Checkout.CanExecute(null));
                Assert.Equal("Checkout pr/123", target.CheckoutState.Caption);
            }

            [Fact]
            public async Task CheckoutNonExistingLocalBranch()
            {
                var target = CreateTarget(
                    currentBranch: "master");
                await target.Load(CreatePullRequest(number: 123));

                Assert.True(target.Checkout.CanExecute(null));
                Assert.Equal("Checkout to pr/123", target.CheckoutState.Caption);
            }

            [Fact]
            public async Task UpdatesOperationErrorWithExceptionMessage()
            {
                var target = CreateTarget(
                    currentBranch: "master",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                await Assert.ThrowsAsync<Exception>(() => target.Checkout.ExecuteAsyncTask(null));
                Assert.Equal("Switch threw", target.OperationError);
            }
        }

        public class ThePullCommand
        {
            [Fact]
            public async Task NotCheckedOut()
            {
                var target = CreateTarget(
                    currentBranch: "master",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                Assert.False(target.Pull.CanExecute(null));
                Assert.Null(target.UpdateState);
            }

            [Fact]
            public async Task CheckedOutAndUpToDate()
            {
                var target = CreateTarget(
                    currentBranch: "pr/123",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                Assert.False(target.Pull.CanExecute(null));
                Assert.Equal(0, target.UpdateState.CommitsAhead);
                Assert.Equal(0, target.UpdateState.CommitsBehind);
                Assert.Equal("No commits to pull", target.UpdateState.PullDisabledMessage);
            }

            [Fact]
            public async Task CheckedOutAndBehind()
            {
                var target = CreateTarget(
                    currentBranch: "pr/123",
                    existingPrBranch: "pr/123",
                    behindBy: 2);
                await target.Load(CreatePullRequest());

                Assert.True(target.Pull.CanExecute(null));
                Assert.Equal(0, target.UpdateState.CommitsAhead);
                Assert.Equal(2, target.UpdateState.CommitsBehind);
                Assert.Null(target.UpdateState.PullDisabledMessage);
            }

            [Fact]
            public async Task CheckedOutAndAheadAndBehind()
            {
                var target = CreateTarget(
                    currentBranch: "pr/123",
                    existingPrBranch: "pr/123",
                    aheadBy: 3,
                    behindBy: 2);
                await target.Load(CreatePullRequest());

                Assert.True(target.Pull.CanExecute(null));
                Assert.Equal(3, target.UpdateState.CommitsAhead);
                Assert.Equal(2, target.UpdateState.CommitsBehind);
                Assert.Null(target.UpdateState.PullDisabledMessage);
            }

            [Fact]
            public async Task UpdatesOperationErrorWithExceptionMessage()
            {
                var target = CreateTarget(
                    currentBranch: "master",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                await Assert.ThrowsAsync<Exception>(() => target.Pull.ExecuteAsyncTask(null));
                Assert.Equal("Pull threw", target.OperationError);
            }
        }

        public class ThePushCommand
        {
            [Fact]
            public async Task NotCheckedOut()
            {
                var target = CreateTarget(
                    currentBranch: "master",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                Assert.False(target.Push.CanExecute(null));
                Assert.Null(target.UpdateState);
            }

            [Fact]
            public async Task CheckedOutAndUpToDate()
            {
                var target = CreateTarget(
                    currentBranch: "pr/123",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                Assert.False(target.Push.CanExecute(null));
                Assert.Equal(0, target.UpdateState.CommitsAhead);
                Assert.Equal(0, target.UpdateState.CommitsBehind);
                Assert.Equal("No commits to push", target.UpdateState.PushDisabledMessage);
            }

            [Fact]
            public async Task CheckedOutAndAhead()
            {
                var target = CreateTarget(
                    currentBranch: "pr/123",
                    existingPrBranch: "pr/123",
                    aheadBy: 2);
                await target.Load(CreatePullRequest());

                Assert.True(target.Push.CanExecute(null));
                Assert.Equal(2, target.UpdateState.CommitsAhead);
                Assert.Equal(0, target.UpdateState.CommitsBehind);
                Assert.Null(target.UpdateState.PushDisabledMessage);
            }

            [Fact]
            public async Task CheckedOutAndBehind()
            {
                var target = CreateTarget(
                    currentBranch: "pr/123",
                    existingPrBranch: "pr/123",
                    behindBy: 2);
                await target.Load(CreatePullRequest());

                Assert.False(target.Push.CanExecute(null));
                Assert.Equal(0, target.UpdateState.CommitsAhead);
                Assert.Equal(2, target.UpdateState.CommitsBehind);
                Assert.Equal("No commits to push", target.UpdateState.PushDisabledMessage);
            }

            [Fact]
            public async Task CheckedOutAndAheadAndBehind()
            {
                var target = CreateTarget(
                    currentBranch: "pr/123",
                    existingPrBranch: "pr/123",
                    aheadBy: 3,
                    behindBy: 2);
                await target.Load(CreatePullRequest());

                Assert.False(target.Push.CanExecute(null));
                Assert.Equal(3, target.UpdateState.CommitsAhead);
                Assert.Equal(2, target.UpdateState.CommitsBehind);
                Assert.Equal("You must pull before you can push", target.UpdateState.PushDisabledMessage);
            }

            [Fact]
            public async Task UpdatesOperationErrorWithExceptionMessage()
            {
                var target = CreateTarget(
                    currentBranch: "master",
                    existingPrBranch: "pr/123");
                await target.Load(CreatePullRequest());

                await Assert.ThrowsAsync<Exception>(() => target.Push.ExecuteAsyncTask(null));
                Assert.Equal("Push threw", target.OperationError);
            }
        }

        static PullRequestDetailViewModel CreateTarget(
            string currentBranch = "master",
            string existingPrBranch = null,
            bool prFromFork = false,
            bool dirty = false,
            int aheadBy = 0,
            int behindBy = 0)
        {
            return CreateTargetAndService(
                currentBranch: currentBranch,
                existingPrBranch: existingPrBranch,
                prFromFork: prFromFork,
                dirty: dirty,
                aheadBy: aheadBy,
                behindBy: behindBy).Item1;
        }

        static Tuple<PullRequestDetailViewModel, IPullRequestService> CreateTargetAndService(
            string currentBranch = "master",
            string existingPrBranch = null,
            bool prFromFork = false,
            bool dirty = false,
            int aheadBy = 0,
            int behindBy = 0)
        {
            var repository = Substitute.For<ILocalRepositoryModel>();
            var currentBranchModel = new BranchModel(currentBranch, repository);
            repository.CurrentBranch.Returns(currentBranchModel);
            repository.CloneUrl.Returns(new UriString(Uri.ToString()));

            var pullRequestService = Substitute.For<IPullRequestService>();

            if (existingPrBranch != null)
            {
                var existingBranchModel = new BranchModel(existingPrBranch, repository);
                pullRequestService.GetLocalBranches(repository, Arg.Any<IPullRequestModel>())
                    .Returns(Observable.Return(existingBranchModel));
            }
            else
            {
                pullRequestService.GetLocalBranches(repository, Arg.Any<IPullRequestModel>())
                    .Returns(Observable.Empty<IBranch>());
            }

            pullRequestService.FetchAndCheckout(repository, Arg.Any<int>(), Arg.Any<string>()).Returns(x => Throws("Checkout threw"));
            pullRequestService.GetDefaultLocalBranchName(repository, Arg.Any<int>(), Arg.Any<string>()).Returns(x => Observable.Return($"pr/{x[1]}"));
            pullRequestService.IsPullRequestFromFork(repository, Arg.Any<IPullRequestModel>()).Returns(prFromFork);
            pullRequestService.IsWorkingDirectoryClean(repository).Returns(Observable.Return(!dirty));
            pullRequestService.Pull(repository).Returns(x => Throws("Pull threw"));
            pullRequestService.Push(repository).Returns(x => Throws("Push threw"));
            pullRequestService.SwitchToBranch(repository, Arg.Any<IPullRequestModel>()).Returns(x => Throws("Switch threw"));

            var divergence = Substitute.For<HistoryDivergence>();
            divergence.AheadBy.Returns(aheadBy);
            divergence.BehindBy.Returns(behindBy);
            pullRequestService.CalculateHistoryDivergence(repository, Arg.Any<int>())
                .Returns(Observable.Return(divergence));

            var settings = Substitute.For<IPackageSettings>();
            settings.UIState.Returns(new UIState { PullRequestDetailState = new PullRequestDetailUIState() });

            var vm = new PullRequestDetailViewModel(
                repository,
                Substitute.For<IModelService>(),
                pullRequestService,
                settings);

            return Tuple.Create(vm, pullRequestService);
        }

        static PullRequestModel CreatePullRequest(int number = 1, string body = "PR Body")
        {
            var author = Substitute.For<IAccount>();

            return new PullRequestModel(number, "PR 1", author, DateTimeOffset.Now)
            {
                State = PullRequestStateEnum.Open,
                Body = string.Empty,
                Head = new GitReferenceModel("source", "foo:baz", "sha", "https://github.com/foo/bar.git"),
                Base = new GitReferenceModel("dest", "foo:bar", "sha", "https://github.com/foo/bar.git"),
            };
        }

        static IObservable<Unit> Throws(string message)
        {
            Func<IObserver<Unit>, Action> f = _ => { throw new Exception(message); };
            return Observable.Create(f);
        }
    }
}
