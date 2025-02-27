﻿/*
 * Copyright 2017 Stanislav Muhametsin. All rights Reserved.
 *
 * Licensed  under the  Apache License,  Version 2.0  (the "License");
 * you may not use  this file  except in  compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed  under the  License is distributed on an "AS IS" BASIS,
 * WITHOUT  WARRANTIES OR CONDITIONS  OF ANY KIND, either  express  or
 * implied.
 *
 * See the License for the specific language governing permissions and
 * limitations under the License. 
 */
using Newtonsoft.Json.Linq;
using NuGet.Commands;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Repositories;
using NuGet.RuntimeModel;
using NuGet.Versioning;
using NuGetUtils.Lib.Common;
using NuGetUtils.Lib.Restore;
using NuGetUtils.Lib.Restore.Agnostic;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UtilPack;



namespace NuGetUtils.Lib.Restore
{
   using TPackageInfo = ImmutableDictionary<String, VersionRange>;

   /// <summary>
   /// This is event argument class for <see cref="BoundRestoreCommandUser.PackageSpecCreated"/> event.
   /// </summary>
   public sealed class PackageSpecCreatedArgs
   {
      /// <summary>
      /// Creates a new instance of <see cref="PackageSpecCreatedArgs"/> with given <see cref="global::NuGet.ProjectModel.PackageSpec"/>.
      /// </summary>
      /// <param name="packageSpec">The <see cref="global::NuGet.ProjectModel.PackageSpec"/> that was created.</param>
      /// <exception cref="ArgumentNullException">If <paramref name="packageSpec"/> is <c>null</c>.</exception>
      public PackageSpecCreatedArgs( PackageSpec packageSpec )
      {
         this.PackageSpec = ArgumentValidator.ValidateNotNull( nameof( packageSpec ), packageSpec );
      }

      /// <summary>
      /// Gets the <see cref="global::NuGet.ProjectModel.PackageSpec"/> that was created.
      /// </summary>
      /// <value>The <see cref="global::NuGet.ProjectModel.PackageSpec"/> that was created.</value>
      public PackageSpec PackageSpec { get; }
   }

   /// <summary>
   /// This class binds itself to specific <see cref="NuGetFramework"/> and then performs restore commands via <see cref="RestoreIfNeeded"/> method.
   /// It also caches results so that restore command is invoked only if needed.
   /// </summary>
   public sealed class BoundRestoreCommandUser : IDisposable
   {
      static BoundRestoreCommandUser()
      {
         UserAgent.SetUserAgentString( new UserAgentStringBuilder( $"{nameof( NuGetUtils )}-Based Client" ) );
      }

      /// <summary>
      /// This is the default package ID of the NuGet package containing serialized runtime graph.
      /// </summary>
      /// <seealso cref="global::NuGet.RuntimeModel.RuntimeGraph"/>
      public const String DEFAULT_RUNTIME_GRAPH_PACKAGE_ID = "Microsoft.NETCore.Platforms";

      /// <summary>
      /// This is the default name of the environment variable which holds the default value for directory where the results of <see cref="RestoreIfNeeded"/> are stored for faster caching.
      /// </summary>
      public const String DEFAULT_LOCK_FILE_CACHE_DIR_ENV_NAME = "NUGET_UTILS_CACHE_DIR";

      /// <summary>
      /// This is the default name of the directory within user's home directory which will serve as lock file cache directory.
      /// </summary>
      public const String DEFAULT_LOCK_FILE_CACHE_SUBDIR = ".nuget-utils-cache";

      /// <summary>
      /// This is the default callback which will simply combine given home directory with <see cref="DEFAULT_LOCK_FILE_CACHE_SUBDIR"/>. It is used to deduce the default lock file cache directory when none is given via constructor parameter or environment variable.
      /// </summary>
      public static Func<String, String> GetDefaultLockFileDir { get; } = homeDir => Path.Combine( homeDir, DEFAULT_LOCK_FILE_CACHE_SUBDIR );

      private static readonly Encoding _DiskCacheEncoding = new UTF8Encoding( false, false );

      private readonly SourceCacheContext _cacheContext;
      private readonly RestoreCommandProviders _restoreCommandProvider;
      private readonly String _nugetRestoreRootDir; // NuGet restore command never writes anything to disk (apart from packages themselves), but if certain file paths are omitted, it simply fails with argumentnullexception when invoking Path.Combine or Path.GetFullName. So this can be anything, really, as long as it's understandable by Path class.
      private readonly TargetFrameworkInformation _restoreTargetFW;
      private readonly Boolean _disposeSourceCacheContext;
      private readonly LockFileFormat _lockFileFormat;
      private readonly ConcurrentDictionary<ImmutableSortedSet<String>, ImmutableDictionary<ImmutableArray<NuGetVersion>, String>> _allLockFiles;
      private readonly ClientPolicyContext _clientPolicyContext;

      /// <summary>
      /// Creates new instance of <see cref="BoundRestoreCommandUser"/> with given parameters.
      /// </summary>
      /// <param name="nugetSettings">The settings to use.</param>
      /// <param name="thisFramework">The framework to bind to.</param>
      /// <param name="runtimeIdentifier">The runtime identifier. Will be used by <see cref="E_NuGetUtils.ExtractAssemblyPaths{TResult}(BoundRestoreCommandUser, LockFile, Func{String, IEnumerable{String}, TResult}, GetFileItemsDelegate, IEnumerable{String})"/> method.</param>
      /// <param name="runtimeGraph">Optional value indicating runtime graph information: either <see cref="global::NuGet.RuntimeModel.RuntimeGraph"/> directly, or <see cref="String"/> containing package ID of package holding <c>runtime.json</c> file, containing serialized runtime graph definition. If neither is specified, then <c>"Microsoft.NETCore.Platforms"</c> package ID used to locate <c>runtime.json</c> file, as per <see href="https://docs.microsoft.com/en-us/dotnet/core/rid-catalog">official documentation</see>.</param>
      /// <param name="nugetLogger">The logger to use in restore command.</param>
      /// <param name="sourceCacheContext">The optional <see cref="SourceCacheContext"/> to use.</param>
      /// <param name="nuspecCache">The optional <see cref="LocalPackageFileCache"/> to use.</param>
      /// <param name="clientPolicyContext">The optional <see cref="ClientPolicyContext"/> to use.</param>
      /// <param name="leaveSourceCacheOpen">Whether to leave the <paramref name="sourceCacheContext"/> open when disposing this <see cref="BoundRestoreCommandUser"/>.</param>
      /// <param name="lockFileCacheDir">The directory where to store serialized lock files returned by <see cref="RestoreIfNeeded"/>. If <c>null</c> or empty, then <paramref name="lockFileCacheEnvironmentVariableName"/> will be used. Set <paramref name="disableLockFileCacheDir"/> to true to disable caching lock files to file system.</param>
      /// <param name="lockFileCacheEnvironmentVariableName">The name of the environment variable containing the value for lock file cache directory. If <c>null</c> or empty, then environment variable reading will be skipped. If the environment variable itself is <c>null</c> or empty, then the user's home directory in conjunction with <paramref name="getDefaultLockFileCacheDir"/> will be used to deduce lock file cache directory. Set <paramref name="disableLockFileCacheDir"/> to true to disable caching lock files to file system.</param>
      /// <param name="getDefaultLockFileCacheDir">This callback will be used when <paramref name="lockFileCacheEnvironmentVariableName"/> is <c>null</c> or empty or when the named environment variable itself was <c>null</c> or empty. This callback will receive current user's home directory as parameter and should return the lock file cache directory. If <c>null</c>, then <see cref="GetDefaultLockFileDir"/> will be used. Set <paramref name="disableLockFileCacheDir"/> to true to disable caching lock files to file system.</param>
      /// <param name="disableLockFileCacheDir">This variable controls whether the results of <see cref="RestoreIfNeeded"/> will be stored to file system lock file cache directory. By default, the lock file caching is enabled. Set this parameter to <c>true</c> to completely disable caching lock files to file system.</param>
      /// <exception cref="ArgumentNullException">If <paramref name="nugetSettings"/> is <c>null</c>.</exception>
      public BoundRestoreCommandUser(
         ISettings nugetSettings,
         NuGetFramework thisFramework = null,
         String runtimeIdentifier = null,
         EitherOr<RuntimeGraph, String> runtimeGraph = default,
         ILogger nugetLogger = null,
         SourceCacheContext sourceCacheContext = null,
         LocalPackageFileCache nuspecCache = null,
         ClientPolicyContext clientPolicyContext = null,
         Boolean leaveSourceCacheOpen = false,
         String lockFileCacheDir = null,
         String lockFileCacheEnvironmentVariableName = DEFAULT_LOCK_FILE_CACHE_DIR_ENV_NAME,
         Func<String, String> getDefaultLockFileCacheDir = null,
         Boolean disableLockFileCacheDir = false
         )
      {
         ArgumentValidator.ValidateNotNull( nameof( nugetSettings ), nugetSettings );
         this.ThisFramework = thisFramework ?? NuGetUtility.TryAutoDetectThisProcessFramework();
         if ( nugetLogger == null )
         {
            nugetLogger = NullLogger.Instance;
         }

         var global = SettingsUtility.GetGlobalPackagesFolder( nugetSettings );
         var fallbacks = SettingsUtility.GetFallbackPackageFolders( nugetSettings );
         if ( sourceCacheContext == null )
         {
            leaveSourceCacheOpen = false;
         }
         var ctx = sourceCacheContext ?? new SourceCacheContext();
         var psp = new PackageSourceProvider( nugetSettings );
         var csp = new CachingSourceProvider( psp );
         this.RuntimeIdentifier = NuGetUtility.TryAutoDetectThisProcessRuntimeIdentifier( runtimeIdentifier );
         this._cacheContext = ctx;
         this._disposeSourceCacheContext = !leaveSourceCacheOpen;

         this.NuGetLogger = nugetLogger;
         this._restoreCommandProvider = RestoreCommandProviders.Create(
            global,
            fallbacks,
            new PackageSourceProvider( nugetSettings ).LoadPackageSources().Where( s => s.IsEnabled ).Select( s => csp.CreateRepository( s ) ),
            ctx,
            nuspecCache ?? new LocalPackageFileCache(),
            nugetLogger
            );
         this._nugetRestoreRootDir = Path.Combine( Path.GetTempPath(), Path.GetRandomFileName() );
         this._restoreTargetFW = new TargetFrameworkInformation()
         {
            FrameworkName = this.ThisFramework
         };
         this.LocalRepositories = this._restoreCommandProvider.GlobalPackages.Singleton()
            .Concat( this._restoreCommandProvider.FallbackPackageFolders )
            .ToImmutableDictionary( r => r.RepositoryRoot, r => r );
         this.RuntimeGraph = new Lazy<RuntimeGraph>( () =>
            {
               var rGraph = runtimeGraph.GetFirstOrDefault();
               if ( rGraph == null )
               {
                  var packageName = runtimeGraph.GetSecondOrDefault();
                  if ( String.IsNullOrEmpty( packageName ) )
                  {
                     packageName = DEFAULT_RUNTIME_GRAPH_PACKAGE_ID;
                  }
                  var platformsPackagePath = this.LocalRepositories.Values
                     .SelectMany( r => r.FindPackagesById( packageName ) )
                     .OrderByDescending( p => p.Version )
                     .FirstOrDefault()
                     ?.ExpandedPath;
                  rGraph = String.IsNullOrEmpty( platformsPackagePath ) ?
                     null :
                     JsonRuntimeFormat.ReadRuntimeGraph( Path.Combine( platformsPackagePath, global::NuGet.RuntimeModel.RuntimeGraph.RuntimeGraphFileName ) );
               }
               return rGraph;
            }, LazyThreadSafetyMode.ExecutionAndPublication );

         if ( !disableLockFileCacheDir )
         {
            this.DiskCacheDirectory = lockFileCacheDir
               .OrIfNullOrEmpty( String.IsNullOrEmpty( lockFileCacheEnvironmentVariableName ) ? null : Environment.GetEnvironmentVariable( lockFileCacheEnvironmentVariableName ) )
               .OrIfNullOrEmpty( ( getDefaultLockFileCacheDir ?? GetDefaultLockFileDir )( Environment.GetEnvironmentVariable(
#if NET472
                  Environment.OSVersion.Platform == PlatformID.Win32NT || Environment.OSVersion.Platform == PlatformID.Win32S || Environment.OSVersion.Platform == PlatformID.Win32Windows || Environment.OSVersion.Platform == PlatformID.WinCE
#else
                  System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform( System.Runtime.InteropServices.OSPlatform.Windows )
#endif
                  ? "USERPROFILE" : "HOME" ) )
                  )
               .OrIfNullOrEmpty( null );
         }
         this._allLockFiles = new ConcurrentDictionary<ImmutableSortedSet<String>, ImmutableDictionary<ImmutableArray<NuGetVersion>, String>>();
         this._lockFileFormat = new LockFileFormat();
         this._clientPolicyContext = clientPolicyContext ?? ClientPolicyContext.GetClientPolicy( nugetSettings, nugetLogger );
      }

      /// <summary>
      /// Gets the framework that this <see cref="BoundRestoreCommandUser"/> is bound to.
      /// </summary>
      /// <value>The framework that this <see cref="BoundRestoreCommandUser"/> is bound to.</value>
      public NuGetFramework ThisFramework { get; }

      /// <summary>
      /// Gets the logger used in restore command.
      /// </summary>
      /// <value>The framework used in restore command.</value>
      public ILogger NuGetLogger { get; }

      /// <summary>
      /// Gets the local repositories by their root path.
      /// </summary>
      /// <value>The local repositories by their root path.</value>
      public ImmutableDictionary<String, NuGetv3LocalRepository> LocalRepositories { get; }

      /// <summary>
      /// Gets the runtime identifier that this <see cref="BoundRestoreCommandUser"/> is bound to.
      /// </summary>
      /// <value>The runtime identifier that this <see cref="BoundRestoreCommandUser"/> is bound to.</value>
      public String RuntimeIdentifier { get; }

      /// <summary>
      /// Gets the <see cref="Lazy{T}"/> holding the <see cref="global::NuGet.RuntimeModel.RuntimeGraph"/> of this <see cref="BoundRestoreCommandUser"/>.
      /// </summary>
      /// <value>The <see cref="Lazy{T}"/> holding the <see cref="global::NuGet.RuntimeModel.RuntimeGraph"/> of this <see cref="BoundRestoreCommandUser"/>.</value>
      public Lazy<RuntimeGraph> RuntimeGraph { get; }

      /// <summary>
      /// Gets the directory where the lock files returned by <see cref="RestoreIfNeeded"/> are stored. Will be <c>null</c> if lock file caching to file system is not enabled in this <see cref="BoundRestoreCommandUser"/>.
      /// </summary>
      /// <value>The directory where the lock files returned by <see cref="RestoreIfNeeded"/> are stored.</value>
      public String DiskCacheDirectory { get; }

      /// <summary>
      /// This event can be registered to in order to further modify the <see cref="PackageSpec"/> that was created during restore.
      /// </summary>
      public event GenericEventHandler<PackageSpecCreatedArgs> PackageSpecCreated;

      /// <summary>
      /// Performs restore command for given combinations of package and version.
      /// Will use cached results, if available.
      /// Returns resulting lock file.
      /// </summary>
      /// <param name="token">The cancellation token, in case actual restore will need to be performed.</param>
      /// <param name="packageInfo">The package ID + version combination. The package version should be parseable into <see cref="VersionRange"/>. If the version is <c>null</c> or empty, <see cref="VersionRange.AllFloating"/> will be used.</param>
      /// <returns>Cached or restored lock file.</returns>
      /// <seealso cref="RestoreCommand"/>
      public async Task<LockFile> RestoreIfNeeded(
         CancellationToken token,
         params (String PackageID, String PackageVersion)[] packageInfo
         )
      {
         // There are control flow paths when we never do anything async so we should check here explicitly for cancellation.
         token.ThrowIfCancellationRequested();
         // Prepare for invoking restore command
         var versionRanges = this.CreatePackageInfo( packageInfo );

         async Task<(LockFile, String)> RestoreUsingNuGetAndWriteToDisk( String lockFilesDir, String lockFileCachePath )
         {
            var lf = ( await this.PerformRestore( versionRanges, token ) ).LockFile;

            (var serializedLockFile, var wroteToDisk) = await this.SaveToDiskCache( versionRanges, lockFilesDir, lockFileCachePath, lf );
            if ( wroteToDisk )
            {
               this.NuGetLogger?.Log( LogLevel.Verbose, "Wrote lock file to disk cache." );
            }
            return (lf, serializedLockFile);
         }

         LockFile retVal = null;
         if ( versionRanges.Count > 0 )
         {
            (var key, var serializedLockFile) = this.TryGetFromInMemoryCache( versionRanges );
            String lockFileCachePath, lockFilesDir;
            if ( serializedLockFile == null )
            {
               (serializedLockFile, lockFilesDir, lockFileCachePath) = await this.TryGetFromDiskCache( versionRanges, key != null );
               if ( serializedLockFile == null )
               {
                  // Disk cache not in use or lock file has not been cached to disk, perform actual restore
                  (retVal, serializedLockFile) = await RestoreUsingNuGetAndWriteToDisk( lockFilesDir, lockFileCachePath );
               }
               else
               {
                  this.NuGetLogger?.Log( LogLevel.Verbose, $"Found information about restorable packages within disk cache at \"{ lockFileCachePath }\"." );
               }

               this.SaveToInMemoryCache( serializedLockFile, key, retVal );
            }
            else
            {
               lockFileCachePath = lockFilesDir = null;
               this.NuGetLogger?.Log( LogLevel.Verbose, "Found restorable packages within in-memory cache." );
            }

            if ( retVal == null && serializedLockFile != null )
            {
               retVal = this._lockFileFormat.Parse( serializedLockFile, Path.Combine( this._nugetRestoreRootDir, "dummy" ) );

               if ( lockFileCachePath != null && lockFilesDir != null )
               {
                  // Check if the disk cache has gone stale (e.g. someone manually deleted package from repo folder)
                  var staled = versionRanges.Where( v => this.LocalRepositories.Values.All( lr => !Directory.Exists( Path.Combine( lr.RepositoryRoot, lr.PathResolver.GetPackageDirectory( v.Key, retVal.Targets[0].GetTargetLibrary( v.Key ).Version ) ) ) ) );
                  if ( staled.Any() )
                  {
                     // Have to re-restore
                     String ignored;
                     this.NuGetLogger?.Log( LogLevel.Verbose, $"Detected the disk cache version of restorable packages at \"{ lockFileCachePath }\" to have been gone stale, re-restoring." );
                     (retVal, ignored) = await RestoreUsingNuGetAndWriteToDisk( lockFilesDir, lockFileCachePath );
                  }
               }
            }
         }


         return retVal;
      }

      /// <summary>
      /// This method is invoked by <see cref="RestoreIfNeeded"/> when the lock file is not in cache and restore command needs to be actually run.
      /// </summary>
      /// <param name="targets">What packages to restore.</param>
      /// <param name="token">The cancellation token to use when performing restore.</param>
      /// <returns>The <see cref="LockFile"/> generated by <see cref="RestoreCommand"/>.</returns>
      private async Task<RestoreResult> PerformRestore(
         TPackageInfo targets,
         CancellationToken token
         )
      {
         var request = new RestoreRequest(
            this.CreatePackageSpec( targets ),
            this._restoreCommandProvider,
            this._cacheContext,
            this._clientPolicyContext,
            this.NuGetLogger
            )
         {
            ProjectStyle = ProjectStyle.Standalone,
            RestoreOutputPath = this._nugetRestoreRootDir
         };
         return await ( new RestoreCommand( request ).ExecuteAsync( token ) );
      }

      /// <summary>
      /// Helper method to create <see cref="PackageSpec"/> out of package ID + version combinations.
      /// </summary>
      /// <param name="targets">The package ID + version combinations.</param>
      /// <returns>A new instance of <see cref="PackageSpec"/> having <see cref="PackageSpec.TargetFrameworks"/> and <see cref="PackageSpec.Dependencies"/> populated as needed.</returns>
      private PackageSpec CreatePackageSpec(
         TPackageInfo targets
         )
      {
         var projectName = $"Restoring: {String.Join( ", ", targets )}";
         var spec = new PackageSpec()
         {
            Name = projectName,
            FilePath = Path.Combine( this._nugetRestoreRootDir, "dummy" ),
            RestoreMetadata = new ProjectRestoreMetadata() // restore command will call GetBuildIntegratedProjectCacheFilePath, which will use request.Project.RestoreMetadata.ProjectPath without null-checks
            {
               ProjectPath = "dummy.csproj",
               ProjectName = projectName // If this is left to anything else than project name, Nuget.Commands.TransitiveNoWarnUtils.ExtractTransitiveNoWarnProperties method will fail to nullref or key-not-found
            },
            // TODO now that this class has its own RuntimeGraph, investigate whether we can use it here. On preliminar check, it seems though that only equality comparison, hash coding, and serializing uses this property.
            //RuntimeGraph = new RuntimeGraph( new RuntimeDescription( this.RuntimeIdentifier ).Singleton() )
         };

         this.PackageSpecCreated?.Invoke( new PackageSpecCreatedArgs( spec ) );

         spec.TargetFrameworks.Add( this._restoreTargetFW );
         spec.Dependencies.AddRange( targets.Select( kvp => new LibraryDependency() { LibraryRange = new LibraryRange( kvp.Key, kvp.Value, LibraryDependencyTarget.Package ) } ) );
         return spec;
      }

      /// <summary>
      /// Disposes the managed resources held by this <see cref="BoundRestoreCommandUser"/>
      /// </summary>
      public void Dispose()
      {
         if ( this._disposeSourceCacheContext )
         {
            this._cacheContext.DisposeSafely();
         }
      }

      private TPackageInfo CreatePackageInfo(
         (String PackageID, String PackageVersion)[] packageInfo
         )
      {
         return packageInfo
               .Where( p => !String.IsNullOrEmpty( p.PackageID ) )
               .GroupBy( p => p.PackageID )
               .ToImmutableDictionary(
                  g => g.Key,
                  g =>
                  {
                     var hasFloating = g.Any( p => String.IsNullOrEmpty( p.PackageVersion ) );
                     return hasFloating ?
                        VersionRange.AllStableFloating :
                        g
                           .Where( p => !String.IsNullOrEmpty( p.PackageVersion ) )
                           .Select( p => VersionRange.Parse( p.PackageVersion ) )
                           .OrderByDescending( v => v )
                           .First();
                  } );
      }

      private (ImmutableSortedSet<String> Key, String SerializedLockFile) TryGetFromInMemoryCache(
         TPackageInfo packageInfo
         )
      {
         String serializedLockFile = null;
         ImmutableSortedSet<String> key = null;
         if ( packageInfo.Values.All( v => !v.IsFloating ) )
         {
            key = packageInfo.Keys.ToImmutableSortedSet( StringComparer.OrdinalIgnoreCase );
            if ( this._allLockFiles.TryGetValue( key, out var cachedLockFiles ) )
            {
               if ( packageInfo.Count == 1 )
               {
                  serializedLockFile = cachedLockFiles.OrderByDescending( kvp => kvp.Value ).First().Value;
               }
               else
               {
                  var rangesArray = key.Select( pID => packageInfo[pID] ).ToImmutableArray();
                  // TODO choose the most "maximized" value if reasonable.
                  serializedLockFile = cachedLockFiles
                     .FirstOrDefault( kvp => kvp.Key.All( ( cachedVersion, cachedVersionIndex ) => rangesArray[cachedVersionIndex].Satisfies( cachedVersion ) ) ) //  versionRanges.Values.All( ideal => ideal.FindBestMatch( kvp.Value.Item1.Where( v => ) )
                     .Value;
               }
            }
         }

         return (key, serializedLockFile);
      }

      private async Task<(String SerializedLockFile, String LockFileCacheDir, String LockFileCachePath)> TryGetFromDiskCache(
         TPackageInfo packageInfo,
         Boolean allNotFloating
         )
      {
         var diskCacheDir = this.DiskCacheDirectory;
         var hasDiskCache = !String.IsNullOrEmpty( diskCacheDir );
         String path = null;
         String lockFilesDir = null;
         String serializedLockFile = null;
         if ( hasDiskCache // Disk cache using is an option
            && allNotFloating // No floating versions -> even one floating version forces us to use restore
            )
         {
            // No serialized value in in-memory cache available, let's try disk cache
            if ( packageInfo.Count == 1 )
            {
               var first = packageInfo.First();
               lockFilesDir = Path.Combine(
                  diskCacheDir,
                  this.ThisFramework.Framework.ToString().ToLowerInvariant(),
                  this.ThisFramework.Version.ToString().ToLowerInvariant(),
                  this.RuntimeIdentifier.OrIfNullOrEmpty( "unknown-rid" ).ToLowerInvariant(),
                  first.Key.ToLowerInvariant()
                  );
               if ( first.Value.IsExact() )
               {
                  path = Path.Combine( lockFilesDir, first.Value.MinVersion.ToString() );
               }
               else if ( Directory.Exists( lockFilesDir ) )
               {
                  path = Directory.EnumerateFiles( lockFilesDir, "*", SearchOption.TopDirectoryOnly )
                     .FindBestMatch( first.Value, fileName => fileName.IsNullOrEmpty() ? null : NuGetVersion.Parse( Path.GetFileName( fileName ) ) )
                     .OrIfNullOrEmpty( null );
               }
            }
            else if ( packageInfo.Values.All( v => v.IsExact() ) )
            {
               // TODO hash of all
            }
            if ( path != null && File.Exists( path ) )
            {
               using ( var reader = new StreamReader( File.OpenRead( path ), _DiskCacheEncoding, false ) )
               {
                  serializedLockFile = ( await reader.ReadToEndAsync() ).OrIfNullOrEmpty( null );
               }
            }
         }

         return (serializedLockFile, lockFilesDir, path);
      }

      private async Task<(String SerializedLockFile, Boolean WroteToDisk)> SaveToDiskCache(
         TPackageInfo packageInfo,
         String lockFilesDir,
         String lockFileCachePath,
         LockFile restoredLockFile
         )
      {
         if ( packageInfo.Count == 1 && lockFilesDir != null && lockFileCachePath == null )
         {
            lockFileCachePath = Path.Combine( lockFilesDir, restoredLockFile.Targets[0].GetTargetLibrary( packageInfo.Keys.First() ).Version.ToString() );
         }

         String serializedLockFile = null;

         var writeToDisk = lockFileCachePath != null && !File.Exists( lockFileCachePath );
         if ( writeToDisk )
         {
            Directory.CreateDirectory( Path.GetDirectoryName( lockFileCachePath ) );
            // Add to disk cache
            using ( var writer = new StreamWriter( File.Open( lockFileCachePath, FileMode.Create, FileAccess.Write, FileShare.None ), _DiskCacheEncoding ) )
            {
               await writer.WriteAsync( serializedLockFile = this._lockFileFormat.Render( restoredLockFile ) );
            }
         }

         return (serializedLockFile, writeToDisk);
      }

      private void SaveToInMemoryCache(
         String serializedLockFile,
         ImmutableSortedSet<String> cacheKey,
         LockFile restoredLockFile
         )
      {
         if ( cacheKey != null && ( restoredLockFile != null || serializedLockFile != null ) )
         {
            // Add to in-memory cache
            if ( serializedLockFile == null )
            {
               serializedLockFile = this._lockFileFormat.Render( restoredLockFile );
            }

            if ( restoredLockFile == null )
            {
               restoredLockFile = this._lockFileFormat.Parse( serializedLockFile, Path.Combine( this._nugetRestoreRootDir, "dummy" ) );
            }

            var libs = restoredLockFile.Targets[0].Libraries.ToDictionary( l => l.Name, l => l.Version, StringComparer.OrdinalIgnoreCase );

            this._allLockFiles
               .AddOrUpdate(
                  cacheKey,
                  ignored => ImmutableDictionary<ImmutableArray<NuGetVersion>, String>.Empty.SetItem( cacheKey.Select( pID => libs[pID] ).ToImmutableArray(), serializedLockFile ),
                  ( ignored, existing ) => existing.SetItem( cacheKey.Select( pID => libs[pID] ).ToImmutableArray(), serializedLockFile )
               );
         }
      }
   }

}

/// <summary>
/// Contains extension methods for types defined in this assembly
/// </summary>
public static partial class E_NuGetUtils
{
   /// <summary>
   /// Given this <see cref="NuGetUsageConfiguration{TLogLevel}"/>, creates a new instance of <see cref="BoundRestoreCommandUser"/> utilizing the properties of <see cref="NuGetUsageConfiguration{TLogLevel}"/>, and then calls given <paramref name="callback"/>.
   /// </summary>
   /// <typeparam name="TResult">The return type of given <paramref name="callback"/>.</typeparam>
   /// <param name="configuration">This <see cref="NuGetUsageConfiguration{TLogLevel}"/>.</param>
   /// <param name="nugetSettingsPath">The object specifying what to pass as first parameter <see cref="NuGetUtility.GetNuGetSettingsWithDefaultRootDirectory"/>: if <see cref="String"/>, then it is passed directly as is, otherwise when it is <see cref="Type"/>, the <see cref="Assembly.CodeBase"/> of the <see cref="Assembly"/> holding the given <see cref="Type"/> is used to extract directory, and that directory is then passed on to <see cref="NuGetUtility.GetNuGetSettingsWithDefaultRootDirectory"/> method.</param>
   /// <param name="lockFileCacheDirEnvName">The environment name of the variable holding default lock file cache directory.</param>
   /// <param name="lockFileCacheDirWithinHomeDir">The directory name within home directory of current user which can be used as lock file cache directory.</param>
   /// <param name="callback">The callback to use created <see cref="BoundRestoreCommandUser"/>. The parameter contains <see cref="BoundRestoreCommandUser"/> as first tuple component, the SDK package ID deduced using <see cref="BoundRestoreCommandUser.ThisFramework"/> and <see cref="NuGetUsageConfiguration{TLogLevel}.SDKFrameworkPackageID"/> as second tuple component, and the SDK package version deduced using <see cref="BoundRestoreCommandUser.ThisFramework"/>, SDK package ID, and <see cref="NuGetUsageConfiguration{TLogLevel}.SDKFrameworkPackageVersion"/> as third tuple component.</param>
   /// <param name="loggerFactory">The callback to create <see cref="ILogger"/> for the <see cref="BoundRestoreCommandUser"/>. May be <c>null</c>.</param>
   /// <returns>The return value of <paramref name="callback"/>.</returns>
   /// <exception cref="NullReferenceException">If this <see cref="NuGetUsageConfiguration{TLogLevel}"/> is <c>null</c>.</exception>
   public static TResult CreateAndUseRestorerAsync<TResult>(
      this NuGetUsageConfiguration<LogLevel> configuration,
      EitherOr<String, Type> nugetSettingsPath,
      String lockFileCacheDirEnvName,
      String lockFileCacheDirWithinHomeDir,
      Func<(BoundRestoreCommandUser Restorer, String SDKPackageID, String SDKPackageVersion), TResult> callback,
      Func<ILogger> loggerFactory
      )
   {
      var targetFWString = configuration.RestoreFramework;

      using ( var restorer = new BoundRestoreCommandUser(
         NuGetUtility.GetNuGetSettingsWithDefaultRootDirectory(
            nugetSettingsPath.IsFirst ? nugetSettingsPath.First : Path.GetDirectoryName( new Uri( nugetSettingsPath.Second.GetTypeInfo().Assembly.CodeBase ).LocalPath ),
            configuration.NuGetConfigurationFile
            ),
         thisFramework: String.IsNullOrEmpty( targetFWString ) ? null : NuGetFramework.Parse( targetFWString ),
         nugetLogger: configuration.DisableLogging ? null : loggerFactory?.Invoke(),
         lockFileCacheDir: configuration.LockFileCacheDirectory,
         lockFileCacheEnvironmentVariableName: lockFileCacheDirEnvName,
         getDefaultLockFileCacheDir: homeDir => Path.Combine( homeDir, lockFileCacheDirWithinHomeDir ),
         disableLockFileCacheDir: configuration.DisableLockFileCache,
         runtimeIdentifier: configuration.RestoreRuntimeID
         ) )
      {

         var thisFramework = restorer.ThisFramework;
         var sdkPackageID = thisFramework.GetSDKPackageID( configuration.SDKFrameworkPackageID );

         return callback( (
            restorer,
            sdkPackageID,
            thisFramework.GetSDKPackageVersion( sdkPackageID, configuration.SDKFrameworkPackageVersion )
            ) );
      }
   }

   /// <summary>
   /// Performs restore command for given package and version, if not already cached.
   /// Returns resulting lock file.
   /// </summary>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="packageID">The package ID to use.</param>
   /// <param name="version">The version to use. Should be parseable into <see cref="VersionRange"/>. If <c>null</c> or empty, <see cref="VersionRange.AllFloating"/> will be used.</param>
   /// <param name="token">The optional cancellation token.</param>
   /// <returns>Cached or restored lock file.</returns>
   /// <seealso cref="RestoreCommand"/>
   public static Task<LockFile> RestoreIfNeeded(
      this BoundRestoreCommandUser restorer,
      String packageID,
      String version,
      CancellationToken token
      )
   {
      return restorer.RestoreIfNeeded( token, (packageID, version) );
   }

   /// <summary>
   /// Performs restore command for given package, if not already cached.
   /// </summary>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="token">The <see cref="CancellationToken"/> to use when restoring.</param>
   /// <param name="packageInfo">The package information containing package ID and package version.</param>
   /// <returns>Cached or restored lock file.</returns>
   /// <seealso cref="RestoreCommand"/>
   public static Task<LockFile> RestoreIfNeeded(
      this BoundRestoreCommandUser restorer,
      CancellationToken token,
      params (String PackageID, String version)[] packageInfo
      )
   {
      return restorer.RestoreIfNeeded( token, packageInfo );
   }

   /// <summary>
   /// This is helper method to extract assembly path information from <see cref="LockFile"/> potentially produced by <see cref="BoundRestoreCommandUser.RestoreIfNeeded"/>.
   /// The key will be package ID, and the result will be object generated by <paramref name="resultCreator"/>.
   /// </summary>
   /// <typeparam name="TResult">The type containing information about assembly paths for a single package.</typeparam>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="lockFile">The <see cref="LockFile"/> containing information about packages.</param>
   /// <param name="resultCreator">The callback to create <typeparamref name="TResult"/> from assemblies of a single package.</param>
   /// <param name="fileGetter">Optional callback to extract assembly paths from single <see cref="LockFileTargetLibrary"/>.</param>
   /// <param name="filterablePackages">Optional array of package IDs which will be (along with their dependencies) filtered out from result.</param>
   /// <returns>A dictionary containing assembly paths of packages.</returns>
   /// <exception cref="NullReferenceException">If this <see cref="BoundRestoreCommandUser"/> is <c>null</c>.</exception>
   /// <exception cref="ArgumentNullException">If <paramref name="lockFile"/> or <paramref name="resultCreator"/> is <c>null</c>.</exception>
   public static IDictionary<String, TResult> ExtractAssemblyPaths<TResult>(
      this BoundRestoreCommandUser restorer,
      LockFile lockFile,
      Func<String, IEnumerable<String>, TResult> resultCreator,
      GetFileItemsDelegate fileGetter = null,
      IEnumerable<String> filterablePackages = null
   )
   {
      ArgumentValidator.ValidateNotNullReference( restorer );
      ArgumentValidator.ValidateNotNull( nameof( lockFile ), lockFile );
      ArgumentValidator.ValidateNotNull( nameof( resultCreator ), resultCreator );

      var retVal = new Dictionary<String, TResult>();
      if ( fileGetter == null )
      {
         fileGetter = NuGetUtility.GetRuntimeAssembliesDelegate;
      }
      var libDic = new Lazy<IDictionary<String, LockFileLibrary>>( () =>
      {
         return lockFile.Libraries.ToDictionary( lib => lib.Name, lib => lib );
      }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication );

      // We will always have only one target, since we are running restore always against one target framework
      IEnumerable<LockFileTargetLibrary> libraries = lockFile.Targets[0].Libraries;
      ISet<String> filterablePackagesSet;
      if (
         filterablePackages != null
         && ( filterablePackagesSet = new HashSet<String>( filterablePackages ) ).Count > 0
         )
      {
         libraries = lockFile.Targets[0].GetAllDependencies(
            lockFile.PackageSpec.Dependencies.Select( dep => dep.Name ),
            dep => !filterablePackagesSet.Contains( dep.Id )
            );
      }

      foreach ( var targetLib in libraries )
      {
         var curLib = targetLib;
         var targetLibFullPath = restorer.ResolveFullPath( lockFile, pathResolver => pathResolver.GetPackageDirectory( curLib.Name, curLib.Version ) );
         if ( !String.IsNullOrEmpty( targetLibFullPath ) )
         {
            retVal.Add( curLib.Name, resultCreator(
               targetLibFullPath,
               fileGetter( restorer.RuntimeGraph, restorer.RuntimeIdentifier, curLib, libDic )
                  ?.Select( p => Path.Combine( targetLibFullPath, p ) )
                  ?? Empty<String>.Enumerable
               ) );
         }
      }

      return retVal;
   }

   /// <summary>
   /// Helper method to resolve full path from relative path (to one of the <see cref="LockFile.PackageFolders"/>) with the lock file.
   /// </summary>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="lockFile">The <see cref="LockFile"/>.</param>
   /// <param name="pathWithinPackageFolder">Some path originating from <paramref name="lockFile"/> and relative to <see cref="LockFile.PackageFolders"/>.</param>
   /// <returns>The full path, or <c>null</c>.</returns>
   public static String ResolveFullPath( this BoundRestoreCommandUser restorer, LockFile lockFile, String pathWithinPackageFolder )
   {
      return restorer.ResolveFullPath(
         lockFile,
         String.IsNullOrEmpty( pathWithinPackageFolder ) ?
            (Func<VersionFolderPathResolver, String>) null :
            _ => pathWithinPackageFolder
         );
   }

   /// <summary>
   /// Helper method to resolve full path from relative path (to one of the <see cref="LockFile.PackageFolders"/>) callback.
   /// </summary>
   /// <param name="restorer">This <see cref="BoundRestoreCommandUser"/>.</param>
   /// <param name="lockFile">The <see cref="LockFile"/>.</param>
   /// <param name="pathExtractor">The callback which should return path relative to <see cref="LockFile.PackageFolders"/>.</param>
   /// <returns>The full path, or <c>null</c>.</returns>
   public static String ResolveFullPath( this BoundRestoreCommandUser restorer, LockFile lockFile, Func<VersionFolderPathResolver, String> pathExtractor )
   {
      ArgumentValidator.ValidateNotNullReference( restorer );
      var onlyOnePackageFolder = ArgumentValidator.ValidateNotNull( nameof( lockFile ), lockFile ).PackageFolders.Count == 1;
      return pathExtractor == null ? null : lockFile.PackageFolders
         .Select( f =>
         {
            return restorer.LocalRepositories.TryGetValue( f.Path, out var curRepo ) ?
                  Path.GetFullPath( Path.Combine( curRepo.RepositoryRoot, pathExtractor( curRepo.PathResolver ) ) ) :
                  null;
         } )
         .FirstOrDefault( fp => !String.IsNullOrEmpty( fp ) && ( onlyOnePackageFolder || Directory.Exists( fp ) ) );
   }

   internal static String OrIfNullOrEmpty( this String str, String nullOrEmptyValue )
   {
      return String.IsNullOrEmpty( str ) ? nullOrEmptyValue : str;
   }

   internal static Boolean IsExact( this VersionRangeBase version )
   {
      return version.HasLowerAndUpperBounds
         && version.IsMinInclusive
         && version.IsMaxInclusive
         && version.MinVersion.Equals( version.MaxVersion );
   }

   //internal static String GetCachedLockFilesDirectory( this BoundRestoreCommandUser restorer, String packageID )
   //{
   //   return Path.Combine(
   //      restorer.DiskCacheDirectory,
   //      restorer.ThisFramework.Framework.ToString().ToLowerInvariant(),
   //      restorer.ThisFramework.Version.ToString().ToLowerInvariant(),
   //      restorer.RuntimeIdentifier.OrIfNullOrEmpty( "unknown-rid" ).ToLowerInvariant(),
   //      packageID.ToLowerInvariant()
   //      );
   //}

}