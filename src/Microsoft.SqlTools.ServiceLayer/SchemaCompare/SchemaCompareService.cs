﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable disable
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.SqlServer.Dac.Compare;
using Microsoft.SqlTools.Hosting.Protocol;
using Microsoft.SqlTools.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.DacFx.Contracts;
using Microsoft.SqlTools.ServiceLayer.Hosting;
using Microsoft.SqlTools.ServiceLayer.SchemaCompare.Contracts;
using Microsoft.SqlTools.ServiceLayer.TaskServices;
using Microsoft.SqlTools.ServiceLayer.Utility;
using Microsoft.SqlTools.Utility;

namespace Microsoft.SqlTools.ServiceLayer.SchemaCompare
{
    /// <summary>
    /// Main class for SchemaCompare service
    /// </summary>
    class SchemaCompareService
    {
        private static ConnectionService connectionService = null;
        private SqlTaskManager sqlTaskManagerInstance = null;
        private static readonly Lazy<SchemaCompareService> instance = new Lazy<SchemaCompareService>(() => new SchemaCompareService());
        private Lazy<ConcurrentDictionary<string, SchemaComparisonResult>> schemaCompareResults =
            new Lazy<ConcurrentDictionary<string, SchemaComparisonResult>>(() => new ConcurrentDictionary<string, SchemaComparisonResult>());
        private Lazy<ConcurrentDictionary<string, Action>> currentComparisonCancellationAction =
            new Lazy<ConcurrentDictionary<string, Action>>(() => new ConcurrentDictionary<string, Action>());

        // For testability
        internal Task CurrentSchemaCompareTask;

        /// <summary>
        /// Gets the singleton instance object
        /// </summary>
        public static SchemaCompareService Instance
        {
            get { return instance.Value; }
        }

        /// <summary>
        /// Initializes the service instance
        /// </summary>
        /// <param name="serviceHost"></param>
        public void InitializeService(ServiceHost serviceHost)
        {
            serviceHost.SetRequestHandler(SchemaCompareRequest.Type, this.HandleSchemaCompareRequest, true);
            serviceHost.SetRequestHandler(SchemaCompareCancellationRequest.Type, this.HandleSchemaCompareCancelRequest, true);
            serviceHost.SetRequestHandler(SchemaCompareGenerateScriptRequest.Type, this.HandleSchemaCompareGenerateScriptRequest, true);
            serviceHost.SetRequestHandler(SchemaComparePublishDatabaseChangesRequest.Type, this.HandleSchemaComparePublishDatabaseChangesRequest, true);
            serviceHost.SetRequestHandler(SchemaComparePublishProjectChangesRequest.Type, this.HandleSchemaComparePublishProjectChangesRequest, true);
            serviceHost.SetRequestHandler(SchemaCompareIncludeExcludeNodeRequest.Type, this.HandleSchemaCompareIncludeExcludeNodeRequest, true);
            serviceHost.SetRequestHandler(SchemaCompareIncludeExcludeAllNodesRequest.Type, this.HandleSchemaCompareIncludeExcludeAllNodesRequest, true);
            serviceHost.SetRequestHandler(SchemaCompareGetDefaultOptionsRequest.Type, this.HandleSchemaCompareGetDefaultOptionsRequest, true);
            serviceHost.SetRequestHandler(SchemaCompareOpenScmpRequest.Type, this.HandleSchemaCompareOpenScmpRequest, true);
            serviceHost.SetRequestHandler(SchemaCompareSaveScmpRequest.Type, this.HandleSchemaCompareSaveScmpRequest, true);
        }

        /// <summary>
        /// Handles schema compare request
        /// </summary>
        /// <returns></returns>
        public Task HandleSchemaCompareRequest(SchemaCompareParams parameters, RequestContext<SchemaCompareResult> requestContext)
        {
            ConnectionInfo sourceConnInfo;
            ConnectionInfo targetConnInfo;
            ConnectionServiceInstance.TryFindConnection(
                    parameters.SourceEndpointInfo.OwnerUri,
                    out sourceConnInfo);
            ConnectionServiceInstance.TryFindConnection(
                parameters.TargetEndpointInfo.OwnerUri,
                out targetConnInfo);

            CurrentSchemaCompareTask = Task.Run(async () =>
            {
                SchemaCompareOperation operation = null;

                try
                {
                    operation = new SchemaCompareOperation(parameters, sourceConnInfo, targetConnInfo);
                    currentComparisonCancellationAction.Value[operation.OperationId] = operation.Cancel;
                    operation.Execute(parameters.TaskExecutionMode);

                    // add result to dictionary of results
                    schemaCompareResults.Value[operation.OperationId] = operation.ComparisonResult;

                    await requestContext.SendResult(new SchemaCompareResult()
                    {
                        OperationId = operation.OperationId,
                        Success = operation.ComparisonResult.IsValid,
                        ErrorMessage = operation.ErrorMessage,
                        AreEqual = operation.ComparisonResult.IsEqual,
                        Differences = operation.Differences
                    });

                    // clean up cancellation action now that the operation is complete (using try remove to avoid exception)
                    Action cancelAction = null;
                    currentComparisonCancellationAction.Value.TryRemove(operation.OperationId, out cancelAction);
                }
                catch (Exception e)
                {
                    Logger.Error("Failed to compare schema. Error: " + e);
                    await requestContext.SendResult(new SchemaCompareResult()
                    {
                        OperationId = operation != null ? operation.OperationId : null,
                        Success = false,
                        ErrorMessage = operation == null ? e.Message : operation.ErrorMessage,
                    });
                }
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles schema compare cancel request
        /// </summary>
        /// <returns></returns>
        public async Task HandleSchemaCompareCancelRequest(SchemaCompareCancelParams parameters, RequestContext<ResultStatus> requestContext)
        {
            Action cancelAction = null;
            if (currentComparisonCancellationAction.Value.TryRemove(parameters.OperationId, out cancelAction))
            {
                if (cancelAction != null)
                {
                    cancelAction.Invoke();
                    await requestContext.SendResult(new ResultStatus()
                    {
                        Success = true,
                        ErrorMessage = null
                    });
                }
            }
            else
            {
                await requestContext.SendResult(new ResultStatus()
                {
                    Success = false,
                    ErrorMessage = SR.SchemaCompareSessionNotFound
                });
            }
        }

        /// <summary>
        /// Handles request for schema compare generate deploy script
        /// </summary>
        /// <returns></returns>
        public async Task HandleSchemaCompareGenerateScriptRequest(SchemaCompareGenerateScriptParams parameters, RequestContext<ResultStatus> requestContext)
        {
            SchemaCompareGenerateScriptOperation operation = null;
            try
            {
                SchemaComparisonResult compareResult = schemaCompareResults.Value[parameters.OperationId];
                operation = new SchemaCompareGenerateScriptOperation(parameters, compareResult);
                SqlTask sqlTask = null;
                TaskMetadata metadata = new TaskMetadata();
                metadata.TaskOperation = operation;
                metadata.TaskExecutionMode = parameters.TaskExecutionMode;
                metadata.ServerName = parameters.TargetServerName;
                metadata.DatabaseName = parameters.TargetDatabaseName;
                metadata.Name = SR.GenerateScriptTaskName;

                sqlTask = SqlTaskManagerInstance.CreateAndRun<SqlTask>(metadata);

                await requestContext.SendResult(new ResultStatus()
                {
                    Success = true,
                    ErrorMessage = operation.ErrorMessage
                });
            }
            catch (Exception e)
            {
                Logger.Error("Failed to generate schema compare script. Error: " + e);
                await requestContext.SendResult(new ResultStatus()
                {
                    Success = false,
                    ErrorMessage = operation == null ? e.Message : operation.ErrorMessage,
                });
            }
        }

        /// <summary>
        /// Handles request for schema compare publish database changes script
        /// </summary>
        /// <returns></returns>
        public async Task HandleSchemaComparePublishDatabaseChangesRequest(SchemaComparePublishDatabaseChangesParams parameters, RequestContext<ResultStatus> requestContext)
        {
            SchemaComparePublishDatabaseChangesOperation operation = null;
            try
            {
                SchemaComparisonResult compareResult = schemaCompareResults.Value[parameters.OperationId];
                operation = new SchemaComparePublishDatabaseChangesOperation(parameters, compareResult);
                SqlTask sqlTask = null;
                TaskMetadata metadata = new TaskMetadata
                {
                    TaskOperation = operation,
                    ServerName = parameters.TargetServerName,
                    DatabaseName = parameters.TargetDatabaseName,
                    Name = SR.PublishChangesTaskName
                };

                sqlTask = SqlTaskManagerInstance.CreateAndRun<SqlTask>(metadata);

                await requestContext.SendResult(new ResultStatus()
                {
                    Success = true,
                    ErrorMessage = operation.ErrorMessage
                });
            }
            catch (Exception e)
            {
                Logger.Error("Failed to publish schema compare database changes. Error: " + e);
                await requestContext.SendResult(new ResultStatus()
                {
                    Success = false,
                    ErrorMessage = operation == null ? e.Message : operation.ErrorMessage,
                });
            }
        }

        /// <summary>
        /// Handles request for schema compare publish database changes script
        /// </summary>
        /// <returns></returns>
        public async Task HandleSchemaComparePublishProjectChangesRequest(SchemaComparePublishProjectChangesParams parameters, RequestContext<SchemaComparePublishProjectResult> requestContext)
        {
            SchemaComparePublishProjectChangesOperation operation = null;
            try
            {
                SchemaComparisonResult compareResult = schemaCompareResults.Value[parameters.OperationId];
                operation = new SchemaComparePublishProjectChangesOperation(parameters, compareResult);

                TaskMetadata metadata = new()
                {
                    TaskOperation = operation,
                    TargetLocation = parameters.TargetProjectPath,
                    Name = SR.PublishChangesTaskName
                };

                SqlTask sqlTask = SqlTaskManagerInstance.CreateTask<SqlTask>(metadata);
                await sqlTask.RunAsync();

                await requestContext.SendResult(new SchemaComparePublishProjectResult()
                {
                    ChangedFiles = operation.PublishResult.ChangedFiles,
                    AddedFiles = operation.PublishResult.AddedFiles,
                    DeletedFiles = operation.PublishResult.DeletedFiles,
                    Success = true,
                    ErrorMessage = operation.ErrorMessage
                });
            }
            catch (Exception e)
            {
                Logger.Error("Failed to publish schema compare database changes. Error: " + e);
                await requestContext.SendResult(new SchemaComparePublishProjectResult()
                {
                    ChangedFiles = Array.Empty<string>(),
                    AddedFiles = Array.Empty<string>(),
                    DeletedFiles = Array.Empty<string>(),
                    Success = false,
                    ErrorMessage = operation?.ErrorMessage ?? e.Message
                });
            }
        }

        /// <summary>
        /// Handles request for exclude incude node in Schema compare result
        /// </summary>
        /// <returns></returns>
        public async Task HandleSchemaCompareIncludeExcludeNodeRequest(SchemaCompareNodeParams parameters, RequestContext<ResultStatus> requestContext)
        {
            SchemaCompareIncludeExcludeNodeOperation operation = null;
            try
            {
                SchemaComparisonResult compareResult = schemaCompareResults.Value[parameters.OperationId];
                operation = new SchemaCompareIncludeExcludeNodeOperation(parameters, compareResult);

                operation.Execute(parameters.TaskExecutionMode);

                // update the comparison result if the include/exclude was successful
                if (operation.Success)
                {
                    schemaCompareResults.Value[parameters.OperationId] = operation.ComparisonResult;
                }

                await requestContext.SendResult(new SchemaCompareIncludeExcludeResult()
                {
                    Success = operation.Success,
                    ErrorMessage = operation.ErrorMessage,
                    AffectedDependencies = operation.AffectedDependencies,
                    BlockingDependencies = operation.BlockingDependencies
                });
            }
            catch (Exception e)
            {
                Logger.Error("Failed to select compare schema result node. Error: " + e);
                await requestContext.SendResult(new ResultStatus()
                {
                    Success = false,
                    ErrorMessage = operation == null ? e.Message : operation.ErrorMessage,
                });
            }
        }

        // <summary>
        /// Handles request for exclude incude all nodes in Schema compare result
        /// </summary>
        /// <returns></returns>
        public async Task HandleSchemaCompareIncludeExcludeAllNodesRequest(SchemaCompareIncludeExcludeAllNodesParams parameters, RequestContext<ResultStatus> requestContext)
        {
            SchemaCompareIncludeExcludeAllNodesOperation operation = null;
            try
            {
                SchemaComparisonResult compareResult = schemaCompareResults.Value[parameters.OperationId];
                operation = new SchemaCompareIncludeExcludeAllNodesOperation(parameters, compareResult);

                operation.Execute(parameters.TaskExecutionMode);

                // update the comparison result if the include/exclude was successful
                if (operation.Success)
                {
                    schemaCompareResults.Value[parameters.OperationId] = operation.ComparisonResult;
                }

                await requestContext.SendResult(new SchemaCompareIncludeExcludeAllNodesResult()
                {
                    Success = operation.Success,
                    ErrorMessage = operation.ErrorMessage,
                    AllIncludedOrExcludedDifferences = operation.AllIncludedOrExcludedDifferences
                });
            }
            catch (Exception e)
            {
                Logger.Error("Failed to select compare schema result node. Error: " + e);
                await requestContext.SendResult(new SchemaCompareIncludeExcludeAllNodesResult()
                {
                    Success = false,
                    ErrorMessage = operation == null ? e.Message : operation.ErrorMessage,
                });
            }
        }

        /// <summary>
        /// Handles request to create default deployment options as per DacFx
        /// </summary>
        /// <returns></returns>
        public async Task HandleSchemaCompareGetDefaultOptionsRequest(SchemaCompareGetOptionsParams parameters, RequestContext<SchemaCompareOptionsResult> requestContext)
        {
            try
            {
                // this does not need to be an async operation since this only creates and returns the default object
                DeploymentOptions options = DeploymentOptions.GetDefaultSchemaCompareOptions();

                await requestContext.SendResult(new SchemaCompareOptionsResult()
                {
                    DefaultDeploymentOptions = options,
                    Success = true,
                    ErrorMessage = null
                });
            }
            catch (Exception e)
            {
                await requestContext.SendResult(new SchemaCompareOptionsResult()
                {
                    DefaultDeploymentOptions = null,
                    Success = false,
                    ErrorMessage = e.Message
                });
            }
        }

        /// <summary>
        /// Handles schema compare open SCMP request
        /// </summary>
        /// <returns></returns>
        public async Task HandleSchemaCompareOpenScmpRequest(SchemaCompareOpenScmpParams parameters, RequestContext<SchemaCompareOpenScmpResult> requestContext)
        {
            CurrentSchemaCompareTask = Task.Run(async () =>
            {
                SchemaCompareOpenScmpOperation operation = null;

                try
                {
                    operation = new SchemaCompareOpenScmpOperation(parameters);
                    operation.Execute(TaskExecutionMode.Execute);

                    await requestContext.SendResult(operation.Result);
                }
                catch (Exception e)
                {
                    await requestContext.SendResult(new SchemaCompareOpenScmpResult()
                    {
                        Success = false,
                        ErrorMessage = operation == null ? e.Message : operation.ErrorMessage,
                    });
                }
            });
        }

        /// <summary>
        /// Handles schema compare save SCMP request
        /// </summary>
        /// <returns></returns>
        public Task HandleSchemaCompareSaveScmpRequest(SchemaCompareSaveScmpParams parameters, RequestContext<ResultStatus> requestContext)
        {
            ConnectionInfo sourceConnInfo;
            ConnectionInfo targetConnInfo;
            ConnectionServiceInstance.TryFindConnection(parameters.SourceEndpointInfo.OwnerUri, out sourceConnInfo);
            ConnectionServiceInstance.TryFindConnection(parameters.TargetEndpointInfo.OwnerUri, out targetConnInfo);

            CurrentSchemaCompareTask = Task.Run(async () =>
            {
                SchemaCompareSaveScmpOperation operation = null;

                try
                {
                    operation = new SchemaCompareSaveScmpOperation(parameters, sourceConnInfo, targetConnInfo);
                    operation.Execute(parameters.TaskExecutionMode);

                    await requestContext.SendResult(new ResultStatus()
                    {
                        Success = true,
                        ErrorMessage = operation.ErrorMessage,
                    });
                }
                catch (Exception e)
                {
                    Logger.Error("Failed to save scmp file. Error: " + e);
                    await requestContext.SendResult(new SchemaCompareResult()
                    {
                        OperationId = operation != null ? operation.OperationId : null,
                        Success = false,
                        ErrorMessage = operation == null ? e.Message : operation.ErrorMessage,
                    });
                }
            });
            return Task.CompletedTask;
        }

        private SqlTaskManager SqlTaskManagerInstance
        {
            get
            {
                sqlTaskManagerInstance ??= SqlTaskManager.Instance;
                return sqlTaskManagerInstance;
            }
            set
            {
                sqlTaskManagerInstance = value;
            }
        }

        internal static ConnectionService ConnectionServiceInstance
        {
            get
            {
                connectionService ??= ConnectionService.Instance;
                return connectionService;
            }
            set
            {
                connectionService = value;
            }
        }
    }
}
