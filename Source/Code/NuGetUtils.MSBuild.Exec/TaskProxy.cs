﻿/*
 * Copyright 2019 Stanislav Muhametsin. All rights Reserved.
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
using Microsoft.Build.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGetUtils.MSBuild.Exec;
using NuGetUtils.MSBuild.Exec.Common;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using ArgumentValidator = UtilPack.ArgumentValidator;

namespace NuGetUtils.MSBuild.Exec
{

   public sealed class TaskProxy
   {
      private readonly ImmutableDictionary<String, TaskPropertyHolder> _propertyInfos;
      private readonly InitializationArgs _initializationArgs;
      private readonly EnvironmentValue _environment;
      private readonly InspectionValue _entrypoint;
      private readonly MethodInspectionInfo _entrypointMethod;

      private readonly CancellationTokenSource _cancellationTokenSource;
      private readonly NuGetUtilsExecProcessMonitor _processMonitor;

      internal TaskProxy(
         NuGetUtilsExecProcessMonitor processMonitor,
         InitializationArgs initializationArgs,
         EnvironmentValue environment,
         InspectionValue entrypoint,
         MethodInspectionInfo entrypointMethod,
         TypeGenerationResult generationResult
         )
      {
         this._processMonitor = ArgumentValidator.ValidateNotNull( nameof( processMonitor ), processMonitor );
         this._initializationArgs = ArgumentValidator.ValidateNotNull( nameof( initializationArgs ), initializationArgs );
         this._environment = ArgumentValidator.ValidateNotNull( nameof( environment ), environment );
         this._entrypoint = ArgumentValidator.ValidateNotNull( nameof( entrypoint ), entrypoint );
         this._entrypointMethod = ArgumentValidator.ValidateNotNull( nameof( entrypointMethod ), entrypointMethod );
         this._propertyInfos = generationResult
            .Properties
            .Select( ( p, idx ) => (p, idx) )
            .ToImmutableDictionary( t => t.p.Name, t => new TaskPropertyHolder( generationResult.PropertyTypeNames[t.idx], t.p.Output, !Equals( t.p.PropertyType, typeof( String ) ) ) );
         this._cancellationTokenSource = new CancellationTokenSource();
      }

      // Called by generated task type
      public void Cancel()
      {
         this._cancellationTokenSource.Cancel();
      }

      // Called by generated task type
      public Object GetProperty( String propertyName )
      {
         return this._propertyInfos.TryGetValue( propertyName, out var info ) ?
            info.Value :
            throw new InvalidOperationException( $"Property \"{propertyName}\" is not supported for this task." );
      }

      // Called by generated task type
      public void SetProperty( String propertyName, Object value )
      {
         if ( this._propertyInfos.TryGetValue( propertyName, out var info ) )
         {
            info.Value = value;
         }
         else
         {
            throw new InvalidOperationException( $"Property \"{propertyName}\" is not supported for this task." );
         }
      }

      // Called by generated task type
      public Boolean Execute( IBuildEngine be )
      {
         return this.ExecuteAsync( be ).GetAwaiter().GetResult();
      }

      public async Task<Boolean> ExecuteAsync( IBuildEngine be )
      {
         const String PROCESS = "NuGetUtils.MSBuild.Exec.Perform";
         // Call process, deserialize result, set output properties.
         var tempFileLocation = Path.Combine( Path.GetTempPath(), $"NuGetUtilsExec_" + Guid.NewGuid() );

         Boolean retVal;
         try
         {
            await be.LogProcessInvocationMessage( PROCESS );
            var startTime = DateTime.UtcNow;

            var returnCode = await this._processMonitor.CallProcessAndStreamOutputAsync(
               PROCESS,
               new PerformConfiguration<String>
               {
                  NuGetConfigurationFile = this._initializationArgs.SettingsLocation,
                  RestoreFramework = this._environment.ThisFramework,
                  RestoreRuntimeID = this._environment.ThisRuntimeID,
                  SDKFrameworkPackageID = this._environment.SDKPackageID,
                  SDKFrameworkPackageVersion = this._environment.SDKPackageVersion,
                  PackageID = this._environment.PackageID,
                  PackageVersion = this._entrypoint.ExactPackageVersion,
                  MethodToken = this._entrypointMethod.MethodToken,
                  AssemblyPath = this._initializationArgs.AssemblyPath,

                  ProjectFilePath = be?.ProjectFileOfTaskNode,
                  ShutdownSemaphoreName = NuGetUtilsExecProcessMonitor.CreateNewShutdownSemaphoreName(),
                  ReturnValuePath = tempFileLocation,
                  InputProperties = new JObject(
                     this._propertyInfos
                        .Where( kvp => !kvp.Value.IsOutput )
                        .Select( kvp =>
                        {
                           var valInfo = kvp.Value;
                           var val = valInfo.Value;
                           return new JProperty( kvp.Key, valInfo.GetInputPropertyValue() );
                        } )
                     ).ToString( Formatting.None ),
               },
               this._cancellationTokenSource.Token,
               be == null ? default( Func<String, Boolean, Task> ) : ( line, isError ) =>
               {
                  if ( isError )
                  {
                     be.LogErrorEvent( new BuildErrorEventArgs(
                        null,
                        null,
                        null,
                        -1,
                        -1,
                        -1,
                        -1,
                        line,
                        null,
                        NuGetExecutionTaskFactory.TASK_NAME
                        ) );
                  }
                  else
                  {
                     be.LogMessageEvent( new BuildMessageEventArgs(
                        line,
                        null,
                        null,
                        MessageImportance.High
                        ) );
                  }
                  return null;
               }
               );
            await be.LogProcessEndMessage( PROCESS, startTime );

            if ( returnCode.HasValue && File.Exists( tempFileLocation ) )
            {
               using ( var sReader = new StreamReader( File.Open( tempFileLocation, FileMode.Open, FileAccess.Read, FileShare.None ), new UTF8Encoding( false, false ), false ) )
               using ( var jReader = new JsonTextReader( sReader ) )
               {
                  foreach ( var tuple in ( JObject.Load( jReader ) ) // No LoadAsync in 9.0.0.
                     .Properties()
                     .Select( p => (p, this._propertyInfos.TryGetValue( p.Name, out var prop ) ? prop : null) )
                     .Where( t => t.Item2?.IsOutput ?? false )
                     )
                  {
                     var jProp = tuple.p;
                     this._propertyInfos[jProp.Name].Value = tuple.Item2.GetOutputPropertyValue( jProp.Value );
                  }
               }
            }

            retVal = returnCode.HasValue && returnCode.Value == 0;
         }
         catch
         {
            if ( this._cancellationTokenSource.IsCancellationRequested )
            {
               retVal = false;

            }
            else
            {
               throw;
            }
         }
         finally
         {
            if ( File.Exists( tempFileLocation ) )
            {
               File.Delete( tempFileLocation );
            }
         }

         return retVal;
      }

      internal sealed class TaskPropertyHolder
      {
         public TaskPropertyHolder(
            String propertyTypeName,
            Boolean isOutput,
            Boolean isTaskItemArray
            )
         {
            this.IsOutput = isOutput;
            this.PropertyTypeName = ArgumentValidator.ValidateNotEmpty( nameof( propertyTypeName ), propertyTypeName );
            this.IsTaskItemArray = isTaskItemArray;
         }

         public String PropertyTypeName { get; }
         public Boolean IsOutput { get; }
         public Boolean IsTaskItemArray { get; }
         public Object Value { get; set; }
      }
   }
}

public static partial class E_NuGetUtils
{
   // TODO in the NuGetUtils.MSBuild.Exec.Inspect, check the type of the array. If it is
   // string -> use ItemSpec
   // other primitive -> use Convert.ChangeType( ItemSpec)
   // non-primitve -> use new JObject() { properties built from task item metadata ... }
   internal static JToken GetInputPropertyValue( this TaskProxy.TaskPropertyHolder propertyHolder )
   {
      var val = propertyHolder.Value;
      return propertyHolder.IsTaskItemArray ?
         (JToken) new JArray( ( (ITaskItem[]) val ).Select( v => v.GetJTokenFromTaskItem( propertyHolder.PropertyTypeName ) ).ToArray() ) :
         new JValue( val );
   }

   internal static Object GetOutputPropertyValue( this TaskProxy.TaskPropertyHolder propertyHolder, JToken token )
   {
      return propertyHolder.IsTaskItemArray ?
         (Object) ( ( token as JArray )?.Select( j => j.GetTaskItemFromJToken() )?.Where( t => t != null )?.ToArray() ?? UtilPack.Empty<ITaskItem>.Array ) :
         ( token as JValue )?.Value?.ToString();
   }

   const String ITEM_SPEC = "ItemSpec";

   private static JToken GetJTokenFromTaskItem(
      this ITaskItem item,
      String targetTypeName
      )
   {
      JToken jToken;
      switch ( targetTypeName )
      {
         case "System.String[]":
            jToken = new JValue( item.ItemSpec );
            break;
         default:
            jToken = new JObject( item.MetadataNames
               .OfType<String>()
               .Select( mdName => new JProperty( mdName, new JValue( item.GetMetadata( mdName ) ) ) )
               .Prepend( new JProperty( ITEM_SPEC, item.ItemSpec ) )
               .ToArray()
               );
            break;
      }
      return jToken;
   }

   private static ITaskItem GetTaskItemFromJToken(
      this JToken token
      )
   {
      ITaskItem retVal;
      switch ( token )
      {
         case JValue value:
            retVal = new TaskItem( value.Value?.ToString() ?? "" );
            break;
         case JObject obj:
            retVal = new TaskItem(
               obj.Property( ITEM_SPEC ).GetItemMetaData(),
               obj.Properties()
                  .Where( p => !String.Equals( p.Name, ITEM_SPEC ) )
                  .ToDictionary( p => p.Name, p => p.GetItemMetaData() )
               );
            break;
         default:
            retVal = null;
            break;
      }

      return retVal;
   }

   private static String GetItemMetaData( this JProperty jProp )
   {
      return ( jProp?.Value as JValue )?.Value?.ToString() ?? "";
   }
}
