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
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Repositories;
using NuGet.RuntimeModel;
using NuGet.Versioning;
using NuGetUtils.Lib.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UtilPack;

namespace NuGetUtils.Lib.Common
{

   /// <summary>
   /// This class contains extension method which are for types not contained in this library.
   /// </summary>
   public static class NuGetUtility
   {
      /// <summary>
      /// This is constant for generic Windows runtime - without version or architecture information.
      /// </summary>
      public const String RID_WINDOWS = "win";

      /// <summary>
      /// This is constant for generic Unix runtime - without version or architecture information.
      /// </summary>
      public const String RID_UNIX = "unix";

      /// <summary>
      /// This is constant for generic Linux runtime - without version or architecture information.
      /// </summary>
      public const String RID_LINUX = "linux";

      /// <summary>
      /// This is constant for generic OSX runtime - without version or architecture information.
      /// </summary>
      public const String RID_OSX = "osx";

      private static readonly NuGetFramework NETCOREAPP22 = new NuGetFramework( FrameworkConstants.FrameworkIdentifiers.NetCoreApp, new Version( 2, 2, 0, 0 ) );

      /// <summary>
      /// Gets the matching assembly path from set of assembly paths, the expanded home path of the package, and optional assembly path from "outside world", e.g. configuration.
      /// </summary>
      /// <param name="packageID">This package ID. It will be used as assembly name without an extension, if <paramref name="optionalGivenAssemblyPath"/> is <c>null</c> or empty.</param>
      /// <param name="assemblyPaths">All assembly paths to be considered from this package.</param>
      /// <param name="optionalGivenAssemblyPath">Optional assembly path from "outside world", e.g. configuration.</param>
      /// <param name="suitableAssemblyPathChecker">The callback to check whether the assembly path is suitable (e.g. file exists on disk).</param>
      /// <returns>Best matched assembly path, or <c>null</c>.</returns>
      public static String GetAssemblyPathFromNuGetAssemblies(
         String packageID,
         String[] assemblyPaths,
         String optionalGivenAssemblyPath,
         Func<String, Boolean> suitableAssemblyPathChecker = null
         )
      {
         if ( suitableAssemblyPathChecker == null )
         {
            suitableAssemblyPathChecker = path => File.Exists( path );
         }
         String assemblyPath;
         if ( assemblyPaths.Length == 1 )
         {
            assemblyPath = assemblyPaths[0];
            if ( !suitableAssemblyPathChecker( assemblyPath ) )
            {
               assemblyPath = null;
            }
         }
         else if ( assemblyPaths.Length > 1 )
         {
            if ( String.IsNullOrEmpty( optionalGivenAssemblyPath ) )
            {
               optionalGivenAssemblyPath = packageID + ".dll";
            }

            assemblyPath = assemblyPaths
               .FirstOrDefault( ap => String.Equals( Path.GetFullPath( ap ), Path.GetFullPath( Path.Combine( Path.GetDirectoryName( ap ), optionalGivenAssemblyPath ) ) )
               && ( suitableAssemblyPathChecker( ap ) ) );
         }
         else
         {
            assemblyPath = null;
         }


         return assemblyPath;
      }

      /// <summary>
      /// The package ID for SDK package of framework <c>.NET Core App</c>. The value is <c>Microsoft.NETCore.App</c>.
      /// </summary>
      public const String SDK_PACKAGE_NETCORE = "Microsoft.NETCore.App";

      /// <summary>
      /// The package ID for SDK package of framework <c>.NET Standard</c>. The value is <c>NETStandard.Library</c>.
      /// </summary>
      public const String SDK_PACKAGE_NETSTANDARD = "NETStandard.Library";

      /// <summary>
      /// The package ID for sdk package of framework <c>ASP.NETCore</c>. The value is <c>Microsoft.AspNetCore.App</c>.
      /// </summary>
      public const String SDK_PACKAGE_ASPNETCORE = "Microsoft.AspNetCore.App";

      /// <summary>
      /// Gets the package ID of the SDK package for given framework. If the optional override is supplied, always returns that.
      /// </summary>
      /// <param name="framework">This <see cref="NuGetFramework"/>.</param>
      /// <param name="givenID">The optional override.</param>
      /// <returns>The value of <paramref name="givenID"/>, if it is non-<c>null</c> and not empty; otherwise tries to deduce the value from this <see cref="NuGetFramework"/>. Currently, returns value of <see cref="SDK_PACKAGE_NETSTANDARD"/> for .NET Standard and .NET Desktop frameworks, and <see cref="SDK_PACKAGE_NETCORE"/> for .NET Core framework, and <see cref="SDK_PACKAGE_ASPNETCORE"/> for ASP.NETCore framework.</returns>
      /// <seealso cref="GetSDKPackageVersion"/>
      public static String GetSDKPackageID( this NuGetFramework framework, String givenID = null )
      {
         // NuGet library should really have something like this method, or this information should be somewhere in repository
         String id;
         if ( !String.IsNullOrEmpty( givenID ) )
         {
            id = givenID;
         }
         else
         {
            id = framework.Framework;
            if ( String.Equals( id, FrameworkConstants.FrameworkIdentifiers.NetCoreApp, StringComparison.OrdinalIgnoreCase ) )
            {
               id = SDK_PACKAGE_NETCORE;
            }
            else if ( String.Equals( id, FrameworkConstants.FrameworkIdentifiers.AspNetCore, StringComparison.OrdinalIgnoreCase ) )
            {
               id = SDK_PACKAGE_ASPNETCORE;
            }
            else if (
               (
                  String.Equals( id, FrameworkConstants.FrameworkIdentifiers.Net, StringComparison.OrdinalIgnoreCase )
                  && framework.Version >= new Version( 4, 5 )
               ) ||
               String.Equals( id, FrameworkConstants.FrameworkIdentifiers.NetStandard, StringComparison.OrdinalIgnoreCase )
               )
            {
               id = SDK_PACKAGE_NETSTANDARD;
            }
            else
            {
               id = null;
            }
         }
         return id;
      }

      /// <summary>
      /// Gets the package version of the SDK package for given framework. If the optional override is supplied, always returns that.
      /// </summary>
      /// <param name="framework">This <see cref="NuGetFramework"/>.</param>
      /// <param name="sdkPackageID">The package ID of the SDK package.</param>
      /// <param name="givenVersion">The optional override.</param>
      /// <returns>The value of <paramref name="givenVersion"/>, if it is non-<c>null</c> and not empty; otherwise tries to deduce the value from this <see cref="NuGetFramework"/> and <paramref name="sdkPackageID"/>.</returns>
      /// <seealso cref="GetSDKPackageID"/>
      public static String GetSDKPackageVersion( this NuGetFramework framework, String sdkPackageID, String givenVersion = null )
      {
         String retVal;
         if ( !String.IsNullOrEmpty( givenVersion ) )
         {
            retVal = givenVersion;
         }
         else
         {
            switch ( sdkPackageID )
            {
               case SDK_PACKAGE_NETSTANDARD:
                  switch ( framework.Version.Major )
                  {
                     case 1:
                        retVal = "1.6.1";
                        break;
                     default:
                        retVal = "2.0.3";
                        break;
                  }
                  //retVal = "2.0.3";
                  //if ( String.Equals( framework.Framework, FrameworkConstants.FrameworkIdentifiers.NetStandard, StringComparison.OrdinalIgnoreCase ) )
                  //{
                  //   retVal = framework.Version.ToString();
                  //}
                  //else
                  //{
                  //   // .NETFramework compatibility, see https://docs.microsoft.com/en-gb/dotnet/standard/net-standard
                  //   var version = framework.Version;
                  //   var minor = version.Minor;
                  //   var build = version.Build;
                  //   switch ( minor )
                  //   {
                  //      case 5:
                  //         retVal = build == 0 ? "1.1" : "1.2";
                  //         break;
                  //      case 6:
                  //         retVal = build == 0 ? "1.3" : ( build == 1 ? "1.4" : "1.5" );
                  //         break;
                  //      default:
                  //         retVal = null;
                  //         break;
                  //   }
                  //}
                  break;
               case SDK_PACKAGE_NETCORE:
                  {
#if !NET46
                     retVal = TryDetectSDKPackgeIDFromPaths( sdkPackageID );
                     if ( String.IsNullOrEmpty( retVal ) )
                     {
#endif
                        var version = framework.Version;
                        switch ( version.Major )
                        {
                           case 1:
                              switch ( version.Minor )
                              {
                                 case 0:
                                    retVal = "1.0.5";
                                    break;
                                 case 1:
                                    retVal = "1.1.2";
                                    break;
                                 default:
                                    retVal = null;
                                    break;
                              }
                              break;
                           case 2:
                              switch ( version.Minor )
                              {
                                 case 0:
                                    retVal = "2.0.9";
                                    break;
                                 case 1:
                                    retVal = "2.1.5";
                                    break;
                                 default:
                                    retVal = null;
                                    break;
                              }
                              break;
                           default:
                              retVal = null;
                              break;
                        }

#if !NET46
                     }
#endif
                  }
                  break;
               default:
                  retVal = null;
                  break;
            }
         }
         return retVal;
      }

#if !NET46

      private static String TryDetectSDKPackgeIDFromPaths(
         String sdkPackageID
         )
      {
         String retVal = null;
         try
         {
            // When one runs normal dotnet command, the DLLs will be visible as e.g. in C:\Program Files\dotnet\shared\Microsoft.NETCore.App\2.1.5
            // When one runs e.g. dotnet build, the DLLs will be visible as e.g. in C:\Program Files\dotnet\sdk\2.1.403
            // Therefore, we must match both version and SDK package ID
            var dir = Path.GetDirectoryName( new Uri( typeof( Object ).GetTypeInfo().Assembly.CodeBase ).LocalPath );
            var maybeVersion = Path.GetFileName( dir );
            var maybePackageID = Path.GetFileName( Path.GetDirectoryName( dir ) );
            if ( Version.TryParse( maybeVersion, out var ignored ) && String.Equals( sdkPackageID, maybePackageID, StringComparison.OrdinalIgnoreCase ) )
            {
               retVal = maybeVersion;
            }
         }
         catch
         {

         }

         return retVal;
      }

#endif

      /// <summary>
      /// This is helper method to try and deduce the <see cref="NuGetFramework"/> representing the currently running process.
      /// If optional framework information is specified as parameter, this method will always return that information as wrapped around <see cref="NuGetFramework"/>.
      /// Otherwise, it will try deduce the required information from entry point assembly <see cref="System.Runtime.Versioning.TargetFrameworkAttribute"/> on desktop, and from <see cref="P:System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription"/> on core.
      /// </summary>
      /// <param name="givenInformation">Optional framework information passed from "outside world", e.g. configuration.</param>
      /// <returns>The deduced <see cref="NuGetFramework"/>, or <see cref="NuGetFramework.AnyFramework"/> if automatic deduce failed.</returns>
      /// <remarks>This method never returns <c>null</c>.</remarks>
      public static NuGetFramework TryAutoDetectThisProcessFramework(
         (String FrameworkName, String FrameworkVersion)? givenInformation = null
         )
      {
         NuGetFramework retVal;
         if (
            givenInformation.HasValue
            && !String.IsNullOrEmpty( givenInformation.Value.FrameworkName )
            && !String.IsNullOrEmpty( givenInformation.Value.FrameworkVersion )
            && Version.TryParse( givenInformation.Value.FrameworkVersion, out var version )
            )
         {
            retVal = new NuGetFramework( givenInformation.Value.FrameworkName, version );
         }
         else
         {
#if NET472 || NET46
            var epAssembly = Assembly.GetEntryAssembly();
            if ( epAssembly == null )
            {
               // Deduct entrypoint assembly as the top-most assembly in current stack trace with TargetFramework attribute
               var fwString = new System.Diagnostics.StackTrace()
                  .GetFrames()
                  .Select( f => f.GetMethod().DeclaringType.Assembly.GetNuGetFrameworkStringFromAssembly() )
                  .LastOrDefault( fwName => !String.IsNullOrEmpty( fwName ) );
               retVal = fwString == null
                 ? NuGetFramework.AnyFramework
                 : NuGetFramework.ParseFrameworkName( fwString, DefaultFrameworkNameProvider.Instance );
            }
            else
            {
               retVal = epAssembly.GetNuGetFrameworkFromAssembly();
            }
#else
            var fwName = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            retVal = null;
            if ( !String.IsNullOrEmpty( fwName )
               && fwName.StartsWith( ".NET Core" )
               && Version.TryParse( fwName.Substring( 10 ), out var netCoreVersion )
               )
            {
               if ( netCoreVersion.Major == 4 )
               {
                  if ( netCoreVersion.Minor == 0 )
                  {
                     retVal = FrameworkConstants.CommonFrameworks.NetCoreApp10;
                  }
                  else if ( netCoreVersion.Minor == 6 )
                  {
                     // The strings are a bit messed up, e.g.:
                     // Core 1.1: ".NET Core 4.6.25211.01"
                     // Core 2.0: ".NET Core 4.6.00001.0"
                     // Core 2.1: ".NET Core 4.6.26614.01" (and also ".NET Core 4.6.26919.02")
                     // Core 2.2: ".NET Core 4.6.27110.4"
                     switch ( netCoreVersion.Build )
                     {
                        case 25211:
                           retVal = FrameworkConstants.CommonFrameworks.NetCoreApp11;
                           break;
                        case 00001:
                           retVal = FrameworkConstants.CommonFrameworks.NetCoreApp20;
                           break;
                        case 26614:
                        case 26919:
                           retVal = FrameworkConstants.CommonFrameworks.NetCoreApp21;
                           break;
                        default:
                           retVal = NETCOREAPP22;
                           break;
                     }
                  }
               }

            }
#endif
         }
         return retVal ?? NuGetFramework.AnyFramework;
      }

      /// <summary>
      /// Tries to automatically detect the runtime identifier of currently running process.
      /// </summary>
      /// <param name="givenRID">The optional override.</param>
      /// <returns>The value of <paramref name="givenRID"/>, if it is non-<c>null</c> and not empty; otherwise tries to deduce the RID using framework library methods. In such cahse, the result is always one of <c>"win"</c>, <c>"linux"</c>, <c>"osx"</c>, or <c>null</c>.</returns>
      public static String TryAutoDetectThisProcessRuntimeIdentifier(
         String givenRID = null
         )
      {
         // I wish these constants were in NuGet.Client library
         String retVal;
         if ( !String.IsNullOrEmpty( givenRID ) )
         {
            retVal = givenRID;
         }
         else
         {
#if NET472 || NET46
            switch ( Environment.OSVersion.Platform )
            {
               case PlatformID.Win32NT:
               case PlatformID.Win32S:
               case PlatformID.Win32Windows:
               case PlatformID.WinCE:
                  retVal = RID_WINDOWS;
                  break;
               // We will most likely never go into cases below, but one never knows...
               case PlatformID.Unix:
                  retVal = RID_UNIX;
                  break;
               case PlatformID.MacOSX:
                  retVal = RID_OSX;
                  break;
               default:
                  retVal = null;
                  break;
            }
#else

            if ( System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform( System.Runtime.InteropServices.OSPlatform.Windows ) )
            {
               retVal = RID_WINDOWS;
            }
            else if ( System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform( System.Runtime.InteropServices.OSPlatform.Linux ) )
            {
               retVal = RID_LINUX;
            }
            else if ( System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform( System.Runtime.InteropServices.OSPlatform.OSX ) )
            {
               retVal = RID_OSX;
            }
            else
            {
               retVal = null;
            }
#endif

         }

         if ( !String.IsNullOrEmpty( retVal ) )
         {
            const Char ARCHITECTURE_SEPARATOR = '-';
            var numericChars = new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' };
            var architectureIndex = retVal.IndexOf( ARCHITECTURE_SEPARATOR );
            var versionIndex = retVal.IndexOfAny( numericChars );
            // Append version, unless we have generic UNIX/LINUX RID
            if ( !String.Equals( retVal, RID_LINUX, StringComparison.OrdinalIgnoreCase )
               && !String.Equals( retVal, RID_UNIX, StringComparison.OrdinalIgnoreCase )
               && ( versionIndex < 0 || architectureIndex < 0 || versionIndex > architectureIndex )
               )
            {
               Version osVersion;
#if NET472 || NET46
               osVersion = Environment.OSVersion.Version;
#else
               // Append version. This is a bit tricky...
               // OS Description is typically something like "Microsoft Windows x.y.z"
               // And we need to extract the x.y.z out of it.
               var osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
               var idx = osDescription.IndexOfAny( numericChars );
               if ( idx > 0 )
               {
                  Version.TryParse( osDescription.Substring( idx ), out osVersion );
               }
               else
               {
                  osVersion = null;
               }
#endif

               if ( osVersion != null )
               {
                  // Append version: More 'fun' special cases.
                  // On Windows, it is "majorminor" (without dot), unless minor is 0, then it is just "major"
                  // On others, it is ".major.minor" (with the dots)
                  String versionSuffix;
                  if ( String.Equals( retVal, RID_WINDOWS ) )
                  {
                     if ( osVersion.Minor == 0 )
                     {
                        versionSuffix = "" + osVersion.Major;
                     }
                     else
                     {
                        versionSuffix = "" + osVersion.Major + "" + osVersion.Minor;
                     }
                  }
                  else
                  {
                     versionSuffix = "." + osVersion.Major + "." + osVersion.Minor;
                  }

                  if ( architectureIndex < 0 )
                  {
                     // Can append directly
                     retVal += versionSuffix;
                  }
                  else
                  {
                     // Insert version before architecture separator
                     retVal = retVal.Substring( 0, architectureIndex ) + versionSuffix + retVal.Substring( architectureIndex );
                  }
               }
            }

            // Append architecture
            if ( architectureIndex < 0 )
            {
               String architectureString;
#if NET472 || NET46
               architectureString = Environment.Is64BitProcess ? "x64" : "x86";
#else
               architectureString = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
#endif
               retVal += ARCHITECTURE_SEPARATOR + architectureString;
            }
         }

         return retVal;
      }

      /// <summary>
      /// Helper method to get NuGet <see cref="ISettings"/> object from multiple potential NuGet configuration file locations.
      /// </summary>
      /// <param name="potentialConfigFileLocations">The potential configuration file locations. Will be traversed in given order.</param>
      /// <returns>A <see cref="ISettings"/> loaded from first specified configuration file location, or <see cref="ISettings"/> loaded using defaults if no potential configuration file locations are specified (array is <c>null</c>, empty, or contains only <c>null</c>s).</returns>
      public static ISettings GetNuGetSettings(
         params String[] potentialConfigFileLocations
         ) => GetNuGetSettingsWithDefaultRootDirectory( null, potentialConfigFileLocations );

      /// <summary>
      /// Helper method to get Nuget <see cref="ISettings"/> object from multiple potential NuGet configuration file locations, and use specified root directory if none of them work.
      /// </summary>
      /// <param name="rootDirectory">The root directory if none of the <paramref name="potentialConfigFileLocations"/> are valid.</param>
      /// <param name="potentialConfigFileLocations">The potential configuration file locations. Will be traversed in given order.</param>
      /// <returns>A <see cref="ISettings"/> loaded from first specified configuration file location, or <see cref="ISettings"/> loaded using defaults if no potential configuration file locations are specified (array is <c>null</c>, empty, or contains only <c>null</c>s).</returns>
      public static ISettings GetNuGetSettingsWithDefaultRootDirectory(
         String rootDirectory,
         params String[] potentialConfigFileLocations
         )
      {
         ISettings nugetSettings = null;
         if ( !potentialConfigFileLocations.IsNullOrEmpty() )
         {
            for ( var i = 0; i < potentialConfigFileLocations.Length && nugetSettings == null; ++i )
            {
               var curlocation = potentialConfigFileLocations[i];
               if ( !String.IsNullOrEmpty( curlocation ) )
               {
                  var fp = Path.GetFullPath( curlocation );
                  nugetSettings = Settings.LoadSpecificSettings( Path.GetDirectoryName( fp ), Path.GetFileName( fp ) );
               }
            }
         }

         if ( nugetSettings == null )
         {
            nugetSettings = Settings.LoadDefaultSettings( rootDirectory, null, new XPlatMachineWideSetting() );
         }

         return nugetSettings;
      }


      /// <summary>
      /// Tries to parse the <see cref="System.Runtime.Versioning.TargetFrameworkAttribute"/> applied to this assembly into <see cref="NuGetFramework"/>.
      /// </summary>
      /// <param name="assembly">This <see cref="Assembly"/>.</param>
      /// <returns>A <see cref="NuGetFramework"/> parsed from <see cref="System.Runtime.Versioning.TargetFrameworkAttribute.FrameworkName"/>, or <see cref="NuGetFramework.AnyFramework"/> if no such attribute is applied to this assembly.</returns>
      /// <exception cref="NullReferenceException">If this <see cref="Assembly"/> is <c>null</c>.</exception>
      public static NuGetFramework GetNuGetFrameworkFromAssembly( this Assembly assembly )
      {
         var thisFrameworkString = assembly.GetNuGetFrameworkStringFromAssembly();
         return thisFrameworkString == null
              ? NuGetFramework.AnyFramework
              : NuGetFramework.ParseFrameworkName( thisFrameworkString, DefaultFrameworkNameProvider.Instance );
      }

      /// <summary>
      /// Tries to get the framework string from <see cref="System.Runtime.Versioning.TargetFrameworkAttribute"/> possibly applied to this assembly.
      /// </summary>
      /// <param name="assembly">This <see cref="Assembly"/>.</param>
      /// <returns>The value of <see cref="System.Runtime.Versioning.TargetFrameworkAttribute.FrameworkName"/> possibly applied to this assembly, or <c>null</c> if no such attribute was found.</returns>
      /// <exception cref="NullReferenceException">If this <see cref="Assembly"/> is <c>null</c>.</exception>
      public static String GetNuGetFrameworkStringFromAssembly( this Assembly assembly )
      {
         return ArgumentValidator.ValidateNotNullReference( assembly ).GetCustomAttributes<System.Runtime.Versioning.TargetFrameworkAttribute>()
            .Select( x => x.FrameworkName )
            .FirstOrDefault();
      }

      /// <summary>
      /// Given an enumerable of strings (paths), filters out those which end with <c>"_._"</c>. This is/was NuGet's way of telling that assembly is supported for specific framework, but it is included as framework's own assembly.
      /// </summary>
      /// <param name="paths">This enumerable of strings (paths).</param>
      /// <returns>Those strings, which do not end with <c>"_._"</c>.</returns>
      public static IEnumerable<String> FilterUnderscores( this IEnumerable<String> paths )
      {
         return paths?.Where( p => !p.EndsWith( "_._" ) );
      }

      /// <summary>
      /// Gets the default <see cref="GetFileItemsDelegate"/> which will return all runtime assemblies for given <see cref="LockFileTargetLibrary"/>.
      /// </summary>
      public static GetFileItemsDelegate GetRuntimeAssembliesDelegate { get; } = ( runtimeGraph, currentRID, targetLib, libs ) =>
      {
         IEnumerable<String> retVal = null;
         if ( !String.IsNullOrEmpty( currentRID ) && targetLib.RuntimeTargets.Count > 0 )
         {
            var rGraph = runtimeGraph.Value;
            retVal = targetLib.RuntimeTargets
                  .Where( rt => rGraph.AreCompatible( currentRID, rt.Runtime ) )
                  .Select( rt => rt.Path );
         }
         if ( retVal.IsNullOrEmpty() )
         {
            retVal = targetLib.RuntimeAssemblies.Select( ra => ra.Path );
         }
         else
         {
            // Filter out the ones which have same name as native ones
            var arr = retVal.Select( p => Path.GetFileNameWithoutExtension( p ) ).ToArray();
            retVal = retVal.Concat( targetLib.RuntimeAssemblies.Where( ra => Array.IndexOf( arr, Path.GetFileNameWithoutExtension( ra.Path ) ) < 0 ).Select( ra => ra.Path ) );
         }
         return retVal;
      };

      /// <summary>
      /// Helper method to compute closed set of dependencies of given package IDs, using information of this <see cref="LockFileTarget"/>.
      /// </summary>
      /// <param name="target">This <see cref="LockFileTarget"/>.</param>
      /// <param name="packageIDs">The IDs of entrypoint packages.</param>
      /// <param name="additionalCheck">Optional additional check for single <see cref="PackageDependency"/>. If supplied, all dependencies that the callback returns <c>false</c> will be filtered out.</param>
      /// <returns>An enumerable of all direct and indirect dependencies of given package IDs, including the package IDs themselves.</returns>
      public static IEnumerable<LockFileTargetLibrary> GetAllDependencies(
         this LockFileTarget target,
         IEnumerable<String> packageIDs,
         Func<PackageDependency, Boolean> additionalCheck = null
         )
      {
         var targetLibsDictionary = target.Libraries.ToDictionary( lib => lib.Name, lib => lib );
         IEnumerable<LockFileTargetLibrary> GetChildrenExceptFilterable( LockFileTargetLibrary curLib )
         {
            return curLib.Dependencies
                   .Where( dep => !String.IsNullOrEmpty( dep.Id ) && targetLibsDictionary.ContainsKey( dep.Id ) && ( additionalCheck?.Invoke( dep ) ?? true ) )
                   .Select( dep => targetLibsDictionary[dep.Id] );
         }

         //filterablePackagesArray = filterablePackagesArray
         //   .Where( f => targetLibsDictionary.ContainsKey( f ) )
         //   .Select( f => targetLibsDictionary[f] )
         //   .SelectMany( targetLib => targetLib.AsDepthFirstEnumerableWithLoopDetection( curLib => GetChildrenExceptFilterable( curLib, false ), returnHead: true ) )
         //   .Select( targetLib => targetLib.Name )
         //   .Distinct()
         //   .ToArray();

         return packageIDs
            .Where( pID => targetLibsDictionary.ContainsKey( pID ) )
            .Select( pID => targetLibsDictionary[pID] )
            .SelectMany( targetLib => targetLib.AsDepthFirstEnumerableWithLoopDetection( curLib => GetChildrenExceptFilterable( curLib ), returnHead: true ) )
            .Distinct();
      }


      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some Windows runtime.
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <see cref="RID_WINDOWS"/> case insensitively.</returns>
      public static Boolean IsWindowsRID( this String thisRID )
         => IsOfGenericRID( thisRID, RID_WINDOWS );

      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some Unix runtime.
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <see cref="RID_UNIX"/> case insensitively.</returns>
      public static Boolean IsUnixRID( this String thisRID )
         => IsOfGenericRID( thisRID, RID_UNIX );

      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some Linux runtime (generic, but not specific).
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <see cref="RID_LINUX"/> case insensitively.</returns>
      public static Boolean IsGenericLinuxRID( this String thisRID )
         => IsOfGenericRID( thisRID, RID_LINUX );

      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some OSX runtime.
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <see cref="RID_OSX"/> case insensitively.</returns>
      public static Boolean IsOSXRID( this String thisRID )
         => IsOfGenericRID( thisRID, RID_OSX );

      /// <summary>
      /// This is helper method to detect wither this <see cref="String"/> is NuGet platform runtime identifier, which represents some other generic runtime.
      /// </summary>
      /// <param name="thisRID">This <see cref="String"/>.</param>
      /// <param name="genericRID">The generic RID to test against.</param>
      /// <returns><c>true</c> if this <see cref="String"/> is not <c>null</c>, not empty, and starts with <paramref name="genericRID"/> case insensitively.</returns>
      public static Boolean IsOfGenericRID( this String thisRID, String genericRID )
      {
         return !String.IsNullOrEmpty( thisRID ) && !String.IsNullOrEmpty( genericRID ) && thisRID.StartsWith( genericRID, StringComparison.OrdinalIgnoreCase );
      }
   }

   /// <summary>
   /// This delegate is used to get required assembly paths from single <see cref="LockFileTargetLibrary"/>.
   /// </summary>
   /// <param name="runtimeGraph">The lazy holding <see cref="RuntimeGraph"/> containing information about various runtime identifiers (RIDs) and their dependencies.</param>
   /// <param name="currentRID">The current RID string.</param>
   /// <param name="targetLibrary">The current <see cref="LockFileTargetLibrary"/>.</param>
   /// <param name="libraries">Lazily evaluated dictionary of all <see cref="LockFileLibrary"/> instances, based on package ID.</param>
   /// <returns>The assembly paths for this <see cref="LockFileTargetLibrary"/>.</returns>
   public delegate IEnumerable<String> GetFileItemsDelegate( Lazy<RuntimeGraph> runtimeGraph, String currentRID, LockFileTargetLibrary targetLibrary, Lazy<IDictionary<String, LockFileLibrary>> libraries );
}