﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Microsoft.Build.Tasks.Git
{
    internal sealed class GitRepository
    {
        private const int SupportedGitRepoFormatVersion = 0;

        private const string CommonDirFileName = "commondir";
        private const string GitDirName = ".git";
        private const string GitDirPrefix = "gitdir: ";
        private const string GitDirFileName = "gitdir";

        // See https://git-scm.com/docs/gitrepository-layout#Documentation/gitrepository-layout.txt-HEAD
        internal const string GitHeadFileName = "HEAD";

        private const string GitModulesFileName = ".gitmodules";

        public GitConfig Config { get; }

        public GitIgnore Ignore => _lazyIgnore.Value;

        /// <summary>
        /// Normalized full path. OS specific directory separators.
        /// </summary>
        public string GitDirectory { get; }

        /// <summary>
        /// Normalized full path. OS specific directory separators.
        /// </summary>
        public string CommonDirectory { get; }

        /// <summary>
        /// Normalized full path. OS specific directory separators. Optional.
        /// </summary>
        public string WorkingDirectory { get; }

        public GitEnvironment Environment { get; }

        private readonly Lazy<(ImmutableArray<GitSubmodule> Submodules, ImmutableArray<string> Diagnostics)> _lazySubmodules;
        private readonly Lazy<GitIgnore> _lazyIgnore;
        private readonly Lazy<string> _lazyHeadCommitSha;
        private readonly GitReferenceResolver _referenceResolver;

        internal GitRepository(GitEnvironment environment, GitConfig config, string gitDirectory, string commonDirectory, string workingDirectory)
        {
            Debug.Assert(environment != null);
            Debug.Assert(config != null);
            Debug.Assert(PathUtils.IsNormalized(gitDirectory));
            Debug.Assert(PathUtils.IsNormalized(commonDirectory));
            Debug.Assert(workingDirectory == null || PathUtils.IsNormalized(workingDirectory));

            Config = config;
            GitDirectory = gitDirectory;
            CommonDirectory = commonDirectory;
            WorkingDirectory = workingDirectory;
            Environment = environment;

            _referenceResolver = new GitReferenceResolver(gitDirectory, commonDirectory);
            _lazySubmodules = new Lazy<(ImmutableArray<GitSubmodule>, ImmutableArray<string>)>(ReadSubmodules);
            _lazyIgnore = new Lazy<GitIgnore>(LoadIgnore);
            _lazyHeadCommitSha = new Lazy<string>(ReadHeadCommitSha);
        }

        // test only
        internal GitRepository(
            GitEnvironment environment,
            GitConfig config,
            string gitDirectory,
            string commonDirectory,
            string workingDirectory,
            ImmutableArray<GitSubmodule> submodules,
            ImmutableArray<string> submoduleDiagnostics,
            GitIgnore ignore,
            string headCommitSha)
            : this(environment, config, gitDirectory, commonDirectory, workingDirectory)
        {
            _lazySubmodules = new Lazy<(ImmutableArray<GitSubmodule>, ImmutableArray<string>)>(() => (submodules, submoduleDiagnostics));
            _lazyIgnore = new Lazy<GitIgnore>(() => ignore);
            _lazyHeadCommitSha = new Lazy<string>(() => headCommitSha);
        }        

        /// <summary>
        /// Opens a repository at the specified location.
        /// </summary>
        /// <exception cref="IOException" />
        /// <exception cref="InvalidDataException" />
        /// <exception cref="NotSupportedException">The repository found requires higher version of git repository format that is currently supported.</exception>
        /// <returns>null if no git repository can be found that contains the specified path.</returns>
        internal static GitRepository OpenRepository(string path, GitEnvironment environment)
            => TryFindRepository(path, out var location) ? OpenRepository(location, environment) : null;

        /// <summary>
        /// Opens a repository at the specified location.
        /// </summary>
        /// <exception cref="IOException" />
        /// <exception cref="InvalidDataException" />
        /// <exception cref="NotSupportedException">The repository found requires higher version of git repository format that is currently supported.</exception>
        public static GitRepository OpenRepository(GitRepositoryLocation location, GitEnvironment environment)
        {
            Debug.Assert(environment != null);
            Debug.Assert(location.GitDirectory != null);
            Debug.Assert(location.CommonDirectory != null);

            // See https://git-scm.com/docs/gitrepository-layout

            var reader = new GitConfig.Reader(location.GitDirectory, location.CommonDirectory, environment);
            var config = reader.Load();

            var workingDirectory = GetWorkingDirectory(config, location.GitDirectory, location.CommonDirectory) ?? location.WorkingDirectory;

            // See https://github.com/git/git/blob/master/Documentation/technical/repository-version.txt
            string versionStr = config.GetVariableValue("core", "repositoryformatversion");
            if (GitConfig.TryParseInt64Value(versionStr, out var version) && version > SupportedGitRepoFormatVersion)
            {
                throw new NotSupportedException(string.Format(Resources.UnsupportedRepositoryVersion, versionStr, SupportedGitRepoFormatVersion));
            }

            return new GitRepository(environment, config, location.GitDirectory, location.CommonDirectory, workingDirectory);
        }

        // internal for testing
        internal static string GetWorkingDirectory(GitConfig config, string gitDirectory, string commonDirectory)
        {
            // Working trees cannot have the same common directory and git directory.
            // 'gitdir' file in a git directory indicates a working tree.

            var gitdirFilePath = Path.Combine(gitDirectory, GitDirFileName);

            var isLinkedWorkingTree = PathUtils.ToPosixDirectoryPath(commonDirectory) != PathUtils.ToPosixDirectoryPath(gitDirectory) && 
                File.Exists(gitdirFilePath);

            if (isLinkedWorkingTree)
            {
                // https://git-scm.com/docs/gitrepository-layout#Documentation/gitrepository-layout.txt-worktreesltidgtgitdir

                string workingDirectory;
                try
                {
                    workingDirectory = File.ReadAllText(gitdirFilePath);
                }
                catch (Exception e) when (!(e is IOException))
                {
                    throw new IOException(e.Message, e);
                }

                workingDirectory = workingDirectory.TrimEnd(CharUtils.AsciiWhitespace);

                // Path in gitdir file must be absolute.
                if (!PathUtils.IsAbsolute(workingDirectory))
                {
                    throw new InvalidDataException(string.Format(Resources.PathSpecifiedInFileIsNotAbsolute, gitdirFilePath, workingDirectory));
                }

                try
                {
                    return Path.GetFullPath(workingDirectory);
                }
                catch
                {
                    throw new InvalidDataException(string.Format(Resources.PathSpecifiedInFileIsInvalid, gitdirFilePath, workingDirectory));
                }
            }

            // See https://git-scm.com/docs/git-config#Documentation/git-config.txt-coreworktree
            string value = config.GetVariableValue("core", "worktree");
            if (value != null)
            {
                // git does not expand home dir relative path ("~/")
                try
                {
                    return Path.GetFullPath(Path.Combine(gitDirectory, value));
                }
                catch
                {
                    throw new InvalidDataException(string.Format(Resources.ValueOfIsNotValidPath, "core.worktree", value));
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the commit SHA of the current HEAD tip.
        /// </summary>
        /// <exception cref="IOException"/>
        /// <exception cref="InvalidDataException"/>
        /// <returns>Null if the HEAD tip reference can't be resolved.</returns>
        public string GetHeadCommitSha()
            => _lazyHeadCommitSha.Value;

        /// <exception cref="IOException"/>
        /// <exception cref="InvalidDataException"/>
        private string ReadHeadCommitSha()
            => _referenceResolver.ResolveHeadReference();

        /// <summary>
        /// Reads and resolves the commit SHA of the current HEAD tip of the specified submodule.
        /// </summary>
        /// <exception cref="IOException"/>
        /// <exception cref="InvalidDataException"/>
        /// <returns>Null if the HEAD tip reference can't be resolved.</returns>
        internal string ReadSubmoduleHeadCommitSha(string submoduleWorkingDirectoryFullPath)
        {
            var gitDirectory = ReadDotGitFile(Path.Combine(submoduleWorkingDirectoryFullPath, GitDirName));
            if (!IsGitDirectory(gitDirectory, out var commonDirectory))
            {
                return null;
            }

            var resolver = new GitReferenceResolver(gitDirectory, commonDirectory);
            return resolver.ResolveHeadReference();
        } 

        private string GetWorkingDirectory()
            => WorkingDirectory ?? throw new InvalidOperationException(Resources.RepositoryDoesNotHaveWorkingDirectory);

        /// <exception cref="IOException"/>
        /// <exception cref="InvalidDataException"/>
        /// <exception cref="NotSupportedException"/>
        public ImmutableArray<GitSubmodule> GetSubmodules()
            => _lazySubmodules.Value.Submodules;

        /// <exception cref="IOException"/>
        /// <exception cref="InvalidDataException"/>
        /// <exception cref="NotSupportedException"/>
        public ImmutableArray<string> GetSubmoduleDiagnostics()
            => _lazySubmodules.Value.Diagnostics;

        /// <exception cref="IOException"/>
        /// <exception cref="InvalidDataException"/>
        /// <exception cref="NotSupportedException"/>
        private (ImmutableArray<GitSubmodule> Submodules, ImmutableArray<string> Diagnostics) ReadSubmodules()
        {
            var workingDirectory = GetWorkingDirectory();
            var submoduleConfig = ReadSubmoduleConfig();
            if (submoduleConfig == null)
            {
                return (ImmutableArray<GitSubmodule>.Empty, ImmutableArray<string>.Empty);
            }

            ImmutableArray<string>.Builder lazyDiagnostics = null;

            void reportDiagnostic(string diagnostic)
                => (lazyDiagnostics ??= ImmutableArray.CreateBuilder<string>()).Add(diagnostic);

            var builder = ImmutableArray.CreateBuilder<GitSubmodule>();

            foreach (var (name, path, url) in EnumerateSubmoduleConfig(submoduleConfig))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    reportDiagnostic(string.Format(Resources.InvalidSubmodulePath, name, path));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(url))
                {
                    reportDiagnostic(string.Format(Resources.InvalidSubmoduleUrl, name, url));
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(Path.Combine(workingDirectory, path));
                }
                catch
                {
                    reportDiagnostic(string.Format(Resources.InvalidSubmodulePath, name, path));
                    continue;
                }

                string headCommitSha;
                try
                {
                    headCommitSha = ReadSubmoduleHeadCommitSha(fullPath);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    reportDiagnostic(e.Message);
                    continue;
                }

                builder.Add(new GitSubmodule(name, path, fullPath, url, headCommitSha));
            }

            return (builder.ToImmutable(), (lazyDiagnostics != null) ? lazyDiagnostics.ToImmutable() : ImmutableArray<string>.Empty);
        }

        // internal for testing
        internal GitConfig ReadSubmoduleConfig()
        {
            var workingDirectory = GetWorkingDirectory();
            var submodulesConfigFile = Path.Combine(workingDirectory, GitModulesFileName);
            if (!File.Exists(submodulesConfigFile))
            {
                return null;
            }

            var reader = new GitConfig.Reader(GitDirectory, CommonDirectory, Environment);
            return reader.LoadFrom(submodulesConfigFile);
        }

        // internal for testing
        internal static IEnumerable<(string Name, string Path, string Url)> EnumerateSubmoduleConfig(GitConfig submoduleConfig)
        {
            foreach (var group in submoduleConfig.Variables.
                Where(kvp => kvp.Key.SectionNameEquals("submodule")).
                GroupBy(kvp => kvp.Key.SubsectionName, GitVariableName.SubsectionNameComparer).
                OrderBy(group => group.Key))
            {
                string name = group.Key;
                string url = null;
                string path = null;

                foreach (var variable in group)
                {
                    if (variable.Key.VariableNameEquals("path"))
                    {
                        path = variable.Value.Last();
                    }
                    else if (variable.Key.VariableNameEquals("url"))
                    {
                        url = variable.Value.Last();
                    }
                }

                yield return (name, path, url);
            }
        }

        private GitIgnore LoadIgnore()
        {
            var workingDirectory = GetWorkingDirectory();
            var ignoreCase = GitConfig.ParseBooleanValue(Config.GetVariableValue("core", "ignorecase"));
            var excludesFile = Config.GetVariableValue("core", "excludesFile");
            var commonInfoExclude = Path.Combine(CommonDirectory, "info", "exclude");

            var root = GitIgnore.LoadFromFile(commonInfoExclude, GitIgnore.LoadFromFile(excludesFile, parent: null));
            return new GitIgnore(root, workingDirectory, ignoreCase);
        }

        /// <summary>
        /// Returns <see cref="GitRepositoryLocation"/> if the specified <paramref name="repositoryDirectory"/> is 
        /// a valid repository directory.
        /// </summary>
        /// <exception cref="IOException" />
        /// <exception cref="InvalidDataException" />
        /// <exception cref="NotSupportedException">The repository found requires higher version of git repository format that is currently supported.</exception>
        /// <returns>False if no git repository can be found that contains the specified path.</returns>
        public static bool TryGetRepositoryLocation(string directory, out GitRepositoryLocation location)
        {
            try
            {
                directory = Path.GetFullPath(directory);
            }
            catch
            {
                location = default;
                return false;
            }

            return TryGetRepositoryLocationImpl(directory, out location);
        }

        /// <summary>
        /// Finds a git repository containing the specified path, if any.
        /// </summary>
        /// <exception cref="IOException" />
        /// <exception cref="InvalidDataException" />
        /// <exception cref="NotSupportedException">The repository found requires higher version of git repository format that is currently supported.</exception>
        /// <returns>False if no git repository can be found that contains the specified path.</returns>
        public static bool TryFindRepository(string directory, out GitRepositoryLocation location)
        {
            try
            {
                directory = Path.GetFullPath(directory);
            }
            catch
            {
                location = default;
                return false;
            }

            while (directory != null)
            {
                if (TryGetRepositoryLocationImpl(directory, out location))
                {
                    return true;
                }

                // TODO: https://github.com/dotnet/sourcelink/issues/302
                // stop on device boundary
                directory = Path.GetDirectoryName(directory);
            }

            location = default;
            return false;
        }

        private static bool TryGetRepositoryLocationImpl(string directory, out GitRepositoryLocation location)
        {
            string commonDirectory;
            var dotGitPath = Path.Combine(directory, GitDirName);

            if (Directory.Exists(dotGitPath))
            {
                if (IsGitDirectory(dotGitPath, out commonDirectory))
                {
                    location = new GitRepositoryLocation(gitDirectory: dotGitPath, commonDirectory, workingDirectory: directory);
                    return true;
                }
            }
            else if (File.Exists(dotGitPath))
            {
                var link = ReadDotGitFile(dotGitPath);
                if (IsGitDirectory(link, out commonDirectory))
                {
                    location = new GitRepositoryLocation(gitDirectory: link, commonDirectory, workingDirectory: directory);
                    return true;
                }

                location = default;
                return false;
            }

            if (Directory.Exists(directory))
            {
                if (IsGitDirectory(directory, out commonDirectory))
                {
                    location = new GitRepositoryLocation(gitDirectory: directory, commonDirectory, workingDirectory: null);
                    return true;
                }
            }

            location = default;
            return false;
        }

        private static string ReadDotGitFile(string path)
        {
            string content;
            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception e) when (!(e is IOException))
            {
                throw new IOException(e.Message, e);
            }

            if (!content.StartsWith(GitDirPrefix))
            {
                throw new InvalidDataException(string.Format(Resources.FormatOfFileIsInvalid, path));
            }

            var link = content.Substring(GitDirPrefix.Length).TrimEnd(CharUtils.AsciiWhitespace);

            try
            {
                // link is relative to the directory containing the file:
                return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), link));
            }
            catch
            {
                throw new InvalidDataException(string.Format(Resources.PathSpecifiedInFileIsInvalid, path, link));
            }
        }

        private static bool IsGitDirectory(string directory, out string commonDirectory)
        {
            // HEAD file is required
            if (!File.Exists(Path.Combine(directory, GitHeadFileName)))
            {
                commonDirectory = null;
                return false;
            }

            // Spec https://git-scm.com/docs/gitrepository-layout#Documentation/gitrepository-layout.txt-commondir:
            var commonLinkPath = Path.Combine(directory, CommonDirFileName);
            if (File.Exists(commonLinkPath))
            {
                try
                {
                    commonDirectory = Path.Combine(directory, File.ReadAllText(commonLinkPath).TrimEnd(CharUtils.AsciiWhitespace));
                }
                catch
                {
                    // git does not consider the directory valid git directory if the content of commondir file is malformed
                    commonDirectory = null;
                    return false;
                }
            }
            else
            {
                commonDirectory = directory;
            }

            // Git also requires objects and refs directories, but we allow them to be missing.
            // See https://github.com/dotnet/sourcelink/tree/master/docs#minimal-git-repository-metadata
            return Directory.Exists(commonDirectory);
        }
    }
}
