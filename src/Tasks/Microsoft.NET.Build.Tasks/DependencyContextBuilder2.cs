﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.DependencyModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace Microsoft.NET.Build.Tasks
{
    internal class DependencyContextBuilder2
    {
        private readonly SingleProjectInfo _mainProjectInfo;
        private readonly bool _includeRuntimeFileVersions;
        private IEnumerable<ReferenceInfo> _referenceAssemblies;
        private IEnumerable<ReferenceInfo> _directReferences;
        private IEnumerable<ReferenceInfo> _dependencyReferences;
        private Dictionary<string, List<ReferenceInfo>> _compileReferences;
        private Dictionary<string, List<ResolvedFile>> _resolvedNuGetFiles;
        private Dictionary<string, SingleProjectInfo> _referenceProjectInfos;
        private IEnumerable<string> _excludeFromPublishPackageIds;
        private Dictionary<string, List<RuntimePackAssetInfo>> _runtimePackAssets;
        private CompilationOptions _compilationOptions;
        private string _referenceAssembliesPath;
        private Dictionary<PackageIdentity, string> _filteredPackages;
        private bool _includeMainProjectInDepsFile = true;
        private Dictionary<string, DependencyLibrary> _dependencyLibraries;
        private Dictionary<string, List<LibraryDependency>> _libraryDependencies;
        private List<string> _mainProjectDependencies;
        private HashSet<PackageIdentity> _packagesToBeFiltered;
        private bool _isFrameworkDependent;
        private string _platformLibrary;
        private string _dotnetFrameworkName;
        private string _runtimeIdentifier;
        private bool _isPortable;
        private HashSet<string> _usedLibraryNames;

        private Dictionary<ReferenceInfo, string> _referenceLibraryNames;

        // This resolver is only used for building file names, so that base path is not required.
        private readonly VersionFolderPathResolver _versionFolderPathResolver = new VersionFolderPathResolver(rootPath: null);

        public DependencyContextBuilder2(SingleProjectInfo mainProjectInfo, ProjectContext projectContext, bool includeRuntimeFileVersions)
        {
            _mainProjectInfo = mainProjectInfo;
            _includeRuntimeFileVersions = includeRuntimeFileVersions;

            var libraryLookup = projectContext.LockFile.Libraries.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);

            _dependencyLibraries = projectContext.LockFileTarget.Libraries
                .Select(lockFileTargetLibrary =>
                {
                    var dependencyLibrary = new DependencyLibrary(lockFileTargetLibrary.Name, lockFileTargetLibrary.Version, lockFileTargetLibrary.Type);

                    if (libraryLookup.TryGetValue(dependencyLibrary.Name, out var library))
                    {
                        dependencyLibrary.Sha512 = library.Sha512;
                        dependencyLibrary.Path = library.Path;
                        dependencyLibrary.MSBuildProject = library.MSBuildProject;
                    }

                    return dependencyLibrary;
                }).ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

            _libraryDependencies = new Dictionary<string, List<LibraryDependency>>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in projectContext.LockFileTarget.Libraries)
            {
                _libraryDependencies[library.Name] = library.Dependencies
                    .Select(d => new LibraryDependency()
                    {
                        Name = d.Id,
                        MinVersion = d.VersionRange.MinVersion
                    }).ToList();
            }

            _mainProjectDependencies = projectContext.GetTopLevelDependencies().ToList();
            _packagesToBeFiltered = projectContext.PackagesToBeFiltered;

            _isFrameworkDependent = projectContext.IsFrameworkDependent;
            _platformLibrary = projectContext.PlatformLibrary?.Name;
            _dotnetFrameworkName = projectContext.LockFileTarget.TargetFramework.DotNetFrameworkName;
            _runtimeIdentifier = projectContext.LockFileTarget.RuntimeIdentifier;
            _isPortable = projectContext.IsPortable;

            _usedLibraryNames = new HashSet<string>(_dependencyLibraries.Keys, StringComparer.OrdinalIgnoreCase);
        }

        private bool IncludeCompilationLibraries => _compilationOptions != null;

        private Dictionary<ReferenceInfo, string> ReferenceLibraryNames
        {
            get
            {
                if (_referenceLibraryNames == null)
                {
                    _referenceLibraryNames = new Dictionary<ReferenceInfo, string>();
                }

                return _referenceLibraryNames;
            }
        }

        public DependencyContextBuilder2 WithReferenceAssemblies(IEnumerable<ReferenceInfo> referenceAssemblies)
        {
            // note: ReferenceAssembly libraries only export compile-time stuff
            // since they assume the runtime library is present already
            _referenceAssemblies = referenceAssemblies;
            return this;
        }

        public DependencyContextBuilder2 WithDirectReferences(IEnumerable<ReferenceInfo> directReferences)
        {
            _directReferences = directReferences;
            return this;
        }

        public DependencyContextBuilder2 WithDependencyReferences(IEnumerable<ReferenceInfo> dependencyReferences)
        {
            _dependencyReferences = dependencyReferences;
            return this;
        }

        public DependencyContextBuilder2 WithCompileReferences(IEnumerable<ReferenceInfo> compileReferences)
        {
            _compileReferences = new Dictionary<string, List<ReferenceInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in compileReferences.GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase))
            {
                _compileReferences.Add(group.Key, group.ToList());
            }

            return this;
        }

        public DependencyContextBuilder2 WithResolvedNuGetFiles(IEnumerable<ResolvedFile> resolvedNuGetFiles)
        {
            _resolvedNuGetFiles = new Dictionary<string, List<ResolvedFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in resolvedNuGetFiles.GroupBy(f => f.PackageName, StringComparer.OrdinalIgnoreCase))
            {
                _resolvedNuGetFiles.Add(group.Key, group.ToList());
            }

            return this;
        }

        public DependencyContextBuilder2 WithReferenceProjectInfos(Dictionary<string, SingleProjectInfo> referenceProjectInfos)
        {
            _referenceProjectInfos = referenceProjectInfos;
            return this;
        }

        public DependencyContextBuilder2 WithMainProjectInDepsFile(bool includeMainProjectInDepsFile)
        {
            _includeMainProjectInDepsFile = includeMainProjectInDepsFile;
            return this;
        }

        public DependencyContextBuilder2 WithExcludeFromPublishAssets(IEnumerable<string> excludeFromPublishPackageIds)
        {
            _excludeFromPublishPackageIds = excludeFromPublishPackageIds;
            return this;
        }

        public DependencyContextBuilder2 WithRuntimePackAssets(IEnumerable<RuntimePackAssetInfo> runtimePackAssets)
        {
            _runtimePackAssets = new Dictionary<string, List<RuntimePackAssetInfo>>();
            foreach (var runtimePackGroup in runtimePackAssets.GroupBy(a => a.PackageName))
            {
                var dependencyLibrary = new DependencyLibrary(runtimePackGroup.Key,
                    NuGetVersion.Parse(runtimePackGroup.First().PackageVersion),
                    "runtimepack");

                _dependencyLibraries.Add(dependencyLibrary.Name, dependencyLibrary);

                _runtimePackAssets[dependencyLibrary.Name] = runtimePackGroup.ToList();
            }
            return this;
        }

        public DependencyContextBuilder2 WithCompilationOptions(CompilationOptions compilationOptions)
        {
            _compilationOptions = compilationOptions;
            return this;
        }

        public DependencyContextBuilder2 WithReferenceAssembliesPath(string referenceAssembliesPath)
        {
            // if the path is empty, we want to use the original string instead of a single trailing character.
            if (string.IsNullOrEmpty(referenceAssembliesPath) ||
                referenceAssembliesPath[referenceAssembliesPath.Length - 1] == Path.DirectorySeparatorChar)
            {
                _referenceAssembliesPath = referenceAssembliesPath;
            }
            else
            {
                _referenceAssembliesPath = referenceAssembliesPath + Path.DirectorySeparatorChar;
            }

            return this;
        }

        public DependencyContextBuilder2 WithPackagesThatWereFiltered(Dictionary<PackageIdentity, string> packagesThatWhereFiltered)
        {
            _filteredPackages = packagesThatWhereFiltered;
            return this;
        }

        public DependencyContext Build()
        {
            CalculateExcludedLibraries();

            List<RuntimeLibrary> runtimeLibraries = new List<RuntimeLibrary>();

            if (_includeMainProjectInDepsFile)
            {
                runtimeLibraries.Add(GetProjectRuntimeLibrary());
            }

            runtimeLibraries.AddRange(GetRuntimePackLibraries());

            foreach (var library in _dependencyLibraries.Values.Where(l => !l.ExcludeFromRuntime))
            {
                var runtimeLibrary = GetRuntimeLibrary(library);
                if (runtimeLibrary != null)
                {
                    runtimeLibraries.Add(runtimeLibrary);
                }
            }

            var directAndDependencyReferences = _directReferences ?? Enumerable.Empty<ReferenceInfo>();
            if (_dependencyReferences != null)
            {
                directAndDependencyReferences = directAndDependencyReferences.Concat(_dependencyReferences);
            }

            foreach (var directReference in directAndDependencyReferences)
            {
                var runtimeLibrary = new RuntimeLibrary(
                    type: "reference",
                    name: GetReferenceLibraryName(directReference),
                    version: directReference.Version,
                    hash: string.Empty,
                    runtimeAssemblyGroups: new[] { new RuntimeAssetGroup(string.Empty, new[] { CreateRuntimeFile(directReference.FileName, directReference.FullPath) }) },
                    nativeLibraryGroups: new RuntimeAssetGroup[] { },
                    resourceAssemblies: CreateResourceAssemblies(directReference.ResourceAssemblies),
                    dependencies: Enumerable.Empty<Dependency>(),
                    path: null,
                    hashPath: null,
                    runtimeStoreManifestName: null,
                    serviceable: false);

                runtimeLibraries.Add(runtimeLibrary);
            }

            List<CompilationLibrary> compilationLibraries = new List<CompilationLibrary>();
            if (IncludeCompilationLibraries)
            {
                if (_includeMainProjectInDepsFile)
                {
                    var dependencies = GetProjectDependencies(runtime: false);

                    var projectCompilationLibrary = new CompilationLibrary(
                        type: "project",
                        name: _mainProjectInfo.Name,
                        version: _mainProjectInfo.Version,
                        hash: string.Empty,
                        assemblies: new[] { _mainProjectInfo.OutputName },
                        dependencies: dependencies,
                        serviceable: false);

                    compilationLibraries.Add(projectCompilationLibrary);
                }

                if (_referenceAssemblies != null)
                {
                    foreach (var referenceAssembly in _referenceAssemblies)
                    {
                        string resolvedPath;
                        if (!string.IsNullOrEmpty(_referenceAssembliesPath) &&
                            referenceAssembly.FullPath?.StartsWith(_referenceAssembliesPath) == true)
                        {
                            resolvedPath = referenceAssembly.FullPath.Substring(_referenceAssembliesPath.Length);
                        }
                        else
                        {
                            resolvedPath = Path.GetFileName(referenceAssembly.FullPath);
                        }

                        compilationLibraries.Add(new CompilationLibrary(
                            type: "referenceassembly",
                            name: GetReferenceLibraryName(referenceAssembly),
                            version: referenceAssembly.Version,
                            hash: string.Empty,
                            assemblies: new[] { resolvedPath },
                            dependencies: Enumerable.Empty<Dependency>(),
                            serviceable: false));
                    }
                }

                foreach (var library in _dependencyLibraries.Values.Where(l => !l.ExcludeFromCompilation))
                {
                    var compilationLibrary = GetCompilationLibrary(library);
                    if (compilationLibrary != null)
                    {
                        compilationLibraries.Add(compilationLibrary);
                    }
                }

                if (_directReferences != null)
                {
                    foreach (var directReference in _directReferences)
                    {
                        compilationLibraries.Add(new CompilationLibrary(
                            type: "reference",
                            name: GetReferenceLibraryName(directReference),
                            version: directReference.Version,
                            hash: string.Empty,
                            assemblies: new[] { directReference.FileName },
                            dependencies: Enumerable.Empty<Dependency>(),
                            serviceable: false));
                    }
                }
            }


            var targetInfo = new TargetInfo(
                _dotnetFrameworkName,
                _runtimeIdentifier,
                runtimeSignature: string.Empty,
                _isPortable);

            return new DependencyContext(
                targetInfo,
                _compilationOptions ?? CompilationOptions.Default,
                compilationLibraries,
                runtimeLibraries,
                new RuntimeFallbacks[] { });
        }

        private RuntimeLibrary GetProjectRuntimeLibrary()
        {
            RuntimeAssetGroup[] runtimeAssemblyGroups = new[] { new RuntimeAssetGroup(string.Empty, _mainProjectInfo.OutputName) };

            var dependencies = GetProjectDependencies(runtime: true);

            return new RuntimeLibrary(
                type: "project",
                name: _mainProjectInfo.Name,
                version: _mainProjectInfo.Version,
                hash: string.Empty,
                runtimeAssemblyGroups: runtimeAssemblyGroups,
                nativeLibraryGroups: Array.Empty<RuntimeAssetGroup>(),
                resourceAssemblies: CreateResourceAssemblies(_mainProjectInfo.ResourceAssemblies),
                dependencies: dependencies,
                path: null,
                hashPath: null,
                runtimeStoreManifestName: GetRuntimeStoreManifestName(_mainProjectInfo.Name, _mainProjectInfo.Version),
                serviceable: false);
        }

        private List<Dependency> GetProjectDependencies(bool runtime)
        {
            List<Dependency> dependencies = new List<Dependency>();
            foreach (var dependencyName in _mainProjectDependencies)
            {
                if (_dependencyLibraries.TryGetValue(dependencyName, out var dependencyLibrary))
                {
                    if (runtime && dependencyLibrary.ExcludeFromRuntime)
                    {
                        continue;
                    }
                    if (!runtime && dependencyLibrary.ExcludeFromCompilation)
                    {
                        continue;
                    }
                    dependencies.Add(dependencyLibrary.Dependency);
                }
            }

            var references = _directReferences;
            if (IncludeCompilationLibraries && _referenceAssemblies != null)
            {
                if (references == null)
                {
                    references = _referenceAssemblies;
                }
                else
                {
                    references = references.Concat(_referenceAssemblies);
                }
            }

            if (references != null)
            {
                foreach (var directReference in references)
                {
                    dependencies.Add(
                        new Dependency(
                            GetReferenceLibraryName(directReference),
                            directReference.Version));
                }
            }
            if (runtime && _runtimePackAssets != null)
            {
                foreach (var runtimePackName in _runtimePackAssets.Keys)
                {
                    dependencies.Add(_dependencyLibraries[runtimePackName].Dependency);
                }
            }

            return dependencies;
        }

        private IEnumerable<RuntimeLibrary> GetRuntimePackLibraries()
        {
            if (_runtimePackAssets == null)
            {
                return Enumerable.Empty<RuntimeLibrary>();
            }
            return _runtimePackAssets.Select(runtimePack =>
            {
                var runtimeAssemblyGroup = new RuntimeAssetGroup(string.Empty,
                    runtimePack.Value.Where(asset => asset.AssetType == AssetType.Runtime)
                    .Select(asset => CreateRuntimeFile(asset.DestinationSubPath, asset.SourcePath)));

                var nativeLibraryGroup = new RuntimeAssetGroup(string.Empty,
                    runtimePack.Value.Where(asset => asset.AssetType == AssetType.Native)
                    .Select(asset => CreateRuntimeFile(asset.DestinationSubPath, asset.SourcePath)));

                return new RuntimeLibrary(
                    type: "runtimepack",
                    name: runtimePack.Key,
                    version: runtimePack.Value.First().PackageVersion,
                    hash: string.Empty,
                    runtimeAssemblyGroups: new[] { runtimeAssemblyGroup },
                    nativeLibraryGroups: new[] { nativeLibraryGroup },
                    resourceAssemblies: Enumerable.Empty<ResourceAssembly>(),
                    dependencies: Enumerable.Empty<Dependency>(),
                    serviceable: false);
            });
        }

        private RuntimeLibrary GetRuntimeLibrary(DependencyLibrary library)
        {
            GetCommonLibraryProperties(library,
                out string hash,
                out HashSet<Dependency> libraryDependencies,
                out bool serviceable,
                out string path,
                out string hashPath,
                out SingleProjectInfo referenceProjectInfo);

            if (referenceProjectInfo is UnreferencedProjectInfo)
            {
                // unreferenced ProjectInfos will be added later as simple dll dependencies
                return null;
            }

            List<RuntimeAssetGroup> runtimeAssemblyGroups = new List<RuntimeAssetGroup>();
            List<RuntimeAssetGroup> nativeLibraryGroups = new List<RuntimeAssetGroup>();
            List<ResourceAssembly> resourceAssemblies = new List<ResourceAssembly>();

            if (library.Type == "project" && !(referenceProjectInfo is UnreferencedProjectInfo))
            {
                runtimeAssemblyGroups.Add(new RuntimeAssetGroup(string.Empty, referenceProjectInfo.OutputName));

                resourceAssemblies.AddRange(referenceProjectInfo.ResourceAssemblies
                                .Select(r => new ResourceAssembly(r.RelativePath, r.Culture)));
            }
            else
            {
                if (_resolvedNuGetFiles != null && _resolvedNuGetFiles.TryGetValue(library.Name, out var resolvedNuGetFiles))
                {
                    var runtimeFiles = resolvedNuGetFiles.Where(f => f.Asset == AssetType.Runtime &&
                                                                !f.IsRuntimeTarget);

                    runtimeAssemblyGroups.Add(new RuntimeAssetGroup(string.Empty,
                                                runtimeFiles.Select(CreateRuntimeFile)));

                    var nativeFiles = resolvedNuGetFiles.Where(f => f.Asset == AssetType.Native &&
                                                                !f.IsRuntimeTarget);

                    nativeLibraryGroups.Add(new RuntimeAssetGroup(string.Empty,
                                                nativeFiles.Select(CreateRuntimeFile)));

                    var resourceFiles = resolvedNuGetFiles.Where(f => f.Asset == AssetType.Resources &&
                                                                !f.IsRuntimeTarget);

                    resourceAssemblies.AddRange(resourceFiles.Select(f => new ResourceAssembly(f.DestinationSubPath, f.Culture)));

                    var runtimeTargets = resolvedNuGetFiles.Where(f => f.IsRuntimeTarget)
                                                                .GroupBy(f => f.RuntimeIdentifier);

                    foreach (var runtimeIdentifierGroup in runtimeTargets)
                    {
                        var managedRuntimeTargetsFiles = runtimeIdentifierGroup.Where(f => f.Asset == AssetType.Runtime).ToList();
                        if (managedRuntimeTargetsFiles.Any())
                        {
                            runtimeAssemblyGroups.Add(new RuntimeAssetGroup(runtimeIdentifierGroup.Key,
                                                            managedRuntimeTargetsFiles.Select(CreateRuntimeFile)));
                        }

                        var nativeRuntimeTargetsFiles = runtimeIdentifierGroup.Where(f => f.Asset == AssetType.Native).ToList();
                        if (nativeRuntimeTargetsFiles.Any())
                        {
                            nativeLibraryGroups.Add(new RuntimeAssetGroup(runtimeIdentifierGroup.Key,
                                                            nativeRuntimeTargetsFiles.Select(CreateRuntimeFile)));
                        }
                    }
                }

            }

            var runtimeLibrary = new RuntimeLibrary(
                type: library.Type,
                name: library.Name,
                version: library.Version.ToString(),
                hash: hash,
                runtimeAssemblyGroups: runtimeAssemblyGroups,
                nativeLibraryGroups: nativeLibraryGroups,
                resourceAssemblies: resourceAssemblies,
                dependencies: libraryDependencies,
                path: path,
                hashPath: hashPath,
                runtimeStoreManifestName: GetRuntimeStoreManifestName(library.Name, library.Version.ToString()),
                serviceable: serviceable);

            return runtimeLibrary;
        }

        private CompilationLibrary GetCompilationLibrary(DependencyLibrary library)
        {
            GetCommonLibraryProperties(library,
                out string hash,
                out HashSet<Dependency> libraryDependencies,
                out bool serviceable,
                out string path,
                out string hashPath,
                out SingleProjectInfo referenceProjectInfo);

            List<string> assemblies = new List<string>();

            if (library.Type == "project" && !(referenceProjectInfo is UnreferencedProjectInfo))
            {
                assemblies.Add(referenceProjectInfo.OutputName);
            }
            else if (_compileReferences != null && _compileReferences.TryGetValue(library.Name, out var compileReferences))
            {
                foreach (var compileReference in compileReferences)
                {
                    assemblies.Add(compileReference.PathInPackage);
                }
            }

            return new CompilationLibrary(
                type: library.Type,
                name: library.Name,
                version: library.Version.ToString(),
                hash,
                assemblies,
                libraryDependencies,
                serviceable,
                path,
                hashPath);
        }

        private void GetCommonLibraryProperties(DependencyLibrary library,
                    out string hash,
                    out HashSet<Dependency> libraryDependencies,
                    out bool serviceable,
                    out string path,
                    out string hashPath,
                    out SingleProjectInfo referenceProjectInfo)
        {
            serviceable = true;
            referenceProjectInfo = null;

            libraryDependencies = new HashSet<Dependency>();
            foreach (var dependency in _libraryDependencies[library.Name])
            {
                if (_dependencyLibraries.TryGetValue(dependency.Name, out var libraryDependency))
                {
                    if (!libraryDependency.ExcludeFromRuntime ||
                        (!libraryDependency.ExcludeFromCompilation && IncludeCompilationLibraries))
                    {
                        libraryDependencies.Add(libraryDependency.Dependency);
                    }
                }
            }

            hash = string.Empty;
            path = null;
            hashPath = null;
            if (library.Type == "package")
            {
                // TEMPORARY: All packages are serviceable in RC2
                // See https://github.com/dotnet/cli/issues/2569
                serviceable = true;
                if (!string.IsNullOrEmpty(library.Sha512))
                {
                    hash = "sha512-" + library.Sha512;
                    hashPath = _versionFolderPathResolver.GetHashFileName(library.Name, library.Version);
                }

                path = library.Path;
            }
            else if (library.Type == "project")
            {
                serviceable = false;
                referenceProjectInfo = GetProjectInfo(library);

                foreach (var dependencyReference in referenceProjectInfo.DependencyReferences)
                {
                    libraryDependencies.Add(
                        new Dependency(
                            GetReferenceLibraryName(dependencyReference),
                            dependencyReference.Version));
                }
            }
        }

        private RuntimeFile CreateRuntimeFile(ResolvedFile resolvedFile)
        {
            string relativePath = resolvedFile.PathInPackage;
            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = resolvedFile.DestinationSubPath;
            }
            return CreateRuntimeFile(relativePath, resolvedFile.SourcePath);
        }

        private RuntimeFile CreateRuntimeFile(string path, string fullPath)
        {
            if (_includeRuntimeFileVersions)
            {
                string fileVersion = FileUtilities.GetFileVersion(fullPath).ToString();
                string assemblyVersion = FileUtilities.TryGetAssemblyVersion(fullPath)?.ToString();
                return new RuntimeFile(path, assemblyVersion, fileVersion);
            }
            else
            {
                return new RuntimeFile(path, null, null);
            }
        }

        private static IEnumerable<ResourceAssembly> CreateResourceAssemblies(IEnumerable<ResourceAssemblyInfo> resourceAssemblyInfos)
        {
            return resourceAssemblyInfos
                .Select(r => new ResourceAssembly(r.RelativePath, r.Culture));
        }

        private SingleProjectInfo GetProjectInfo(DependencyLibrary library)
        {
            string projectPath = library.MSBuildProject;
            if (string.IsNullOrEmpty(projectPath))
            {
                throw new BuildErrorException(Strings.CannotFindProjectInfo, library.Name);
            }

            string mainProjectDirectory = Path.GetDirectoryName(_mainProjectInfo.ProjectPath);
            string fullProjectPath = Path.GetFullPath(Path.Combine(mainProjectDirectory, projectPath));

            SingleProjectInfo referenceProjectInfo = null;
            if (_referenceProjectInfos?.TryGetValue(fullProjectPath, out referenceProjectInfo) != true ||
                referenceProjectInfo == null)
            {
                return UnreferencedProjectInfo.Default;
            }

            return referenceProjectInfo;
        }

        private void CalculateExcludedLibraries()
        {
            Dictionary<string, DependencyLibrary> libraries = _dependencyLibraries;

            HashSet<string> runtimeExclusionList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_isFrameworkDependent && !string.IsNullOrEmpty(_platformLibrary))
            {
                //  Exclude platform library and dependencies.
                runtimeExclusionList.Add(_platformLibrary);

                Stack<LibraryDependency> dependenciesToWalk = new Stack<LibraryDependency>(_libraryDependencies[_platformLibrary]);

                while (dependenciesToWalk.Any())
                {
                    var dependency = dependenciesToWalk.Pop();
                    if (runtimeExclusionList.Contains(dependency.Name))
                    {
                        continue;
                    }

                    //  Resolved version of library has to match dependency version exactly, so that we
                    //  don't exclude newer versions of libraries that are part of the platform
                    if (_dependencyLibraries[dependency.Name].Version == dependency.MinVersion)
                    {
                        runtimeExclusionList.Add(dependency.Name);
                        foreach (var newDependency in _libraryDependencies[dependency.Name])
                        {
                            dependenciesToWalk.Push(newDependency);
                        }
                    }
                }
            }

            if (_packagesToBeFiltered != null)
            {
                foreach (var packageToFilter in _packagesToBeFiltered)
                {
                    if (_dependencyLibraries.TryGetValue(packageToFilter.Id, out var library))
                    {
                        if (library.Type == "package" &&
                            _dependencyLibraries[packageToFilter.Id].Version == packageToFilter.Version)
                        {
                            runtimeExclusionList.Add(packageToFilter.Id);
                        }
                    }
                }
            }

            foreach (var packageToExcludeFromRuntime in runtimeExclusionList)
            {
                _dependencyLibraries[packageToExcludeFromRuntime].ExcludeFromRuntime = true;
            }

            if (_excludeFromPublishPackageIds != null && _excludeFromPublishPackageIds.Any())
            {
                //  Include transitive dependencies of all top-level dependencies which are not
                //  excluded from publish

                Dictionary<string, DependencyLibrary> includedDependencies = new Dictionary<string, DependencyLibrary>(StringComparer.OrdinalIgnoreCase);

                Stack<string> dependenciesToWalk = new Stack<string>(
                    _mainProjectDependencies.Except(_excludeFromPublishPackageIds, StringComparer.OrdinalIgnoreCase));

                while (dependenciesToWalk.Any())
                {
                    var dependencyName = dependenciesToWalk.Pop();
                    if (!includedDependencies.ContainsKey(dependencyName))
                    {
                        includedDependencies.Add(dependencyName, _dependencyLibraries[dependencyName]);
                        foreach (var newDependency in _libraryDependencies[dependencyName])
                        {
                            dependenciesToWalk.Push(newDependency.Name);
                        }
                    }
                }

                foreach (var dependencyLibrary in _dependencyLibraries.Values)
                {
                    if (!includedDependencies.ContainsKey(dependencyLibrary.Name))
                    {
                        dependencyLibrary.ExcludeFromCompilation = true;
                        dependencyLibrary.ExcludeFromRuntime = true;
                    }
                }
            }
        }

        private string GetReferenceLibraryName(ReferenceInfo reference)
        {
            if (!ReferenceLibraryNames.TryGetValue(reference, out string name))
            {
                // Reference names can conflict with PackageReference names, so
                // ensure that the Reference names are unique when creating libraries
                name = GetUniqueReferenceName(reference.Name);

                ReferenceLibraryNames.Add(reference, name);
                _usedLibraryNames.Add(name);
            }

            return name;
        }

        private string GetUniqueReferenceName(string name)
        {
            if (_usedLibraryNames.Contains(name))
            {
                string startingName = $"{name}.Reference";
                name = startingName;

                int suffix = 1;
                while (_usedLibraryNames.Contains(name))
                {
                    name = $"{startingName}{suffix++}";
                }
            }

            return name;
        }

        private string GetRuntimeStoreManifestName(string packageName, string packageVersion)
        {
            string runtimeStoreManifestName = null;
            if (_filteredPackages != null && _filteredPackages.Any())
            {
                var pkg = new PackageIdentity(packageName, NuGetVersion.Parse(packageVersion));
                _filteredPackages?.TryGetValue(pkg, out runtimeStoreManifestName);
            }
            return runtimeStoreManifestName;
        }

        private class DependencyLibrary
        {
            public string Name { get; }
            public NuGetVersion Version { get; }
            public string Type { get; }
            public Dependency Dependency { get; }
            public string Sha512 { get; set; }
            public string Path { get; set; }
            public string MSBuildProject { get; set; }

            public bool ExcludeFromRuntime { get; set; }

            public bool ExcludeFromCompilation { get; set; }

            public DependencyLibrary(string name, NuGetVersion version, string type)
            {
                Name = name;
                Version = version;
                Type = type;
                Dependency = new Dependency(name, version.ToString());
            }
        }

        private struct LibraryDependency
        {
            public string Name { get; set; }
            public NuGetVersion MinVersion { get; set; }
        }
    }
}
