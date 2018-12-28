using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using HotChocolate.Resolvers;
using HotChocolate.Runtime;

namespace HotChocolate.Execution
{
    internal abstract partial class ExecutionStrategyBase
        : IExecutionStrategy
    {
        public abstract Task<IExecutionResult> ExecuteAsync(
            IExecutionContext executionContext,
            CancellationToken cancellationToken);

        protected async Task<IQueryExecutionResult> ExecuteQueryAsync(
            IExecutionContext executionContext,
            BatchOperationHandler batchOperationHandler,
            CancellationToken cancellationToken)
        {
            var data = new OrderedDictionary();

            IEnumerable<ResolverTask> rootResolverTasks =
                CreateRootResolverTasks(executionContext, data);

            await ExecuteResolversAsync(
                executionContext,
                rootResolverTasks,
                batchOperationHandler,
                cancellationToken);

            return new QueryResult(data, executionContext.GetErrors());
        }

        protected async Task ExecuteResolversAsync(
            IExecutionContext executionContext,
            IEnumerable<ResolverTask> initialBatch,
            BatchOperationHandler batchOperationHandler,
            CancellationToken cancellationToken)
        {
            var currentBatch = new List<ResolverTask>(initialBatch);
            var nextBatch = new List<ResolverTask>();
            List<ResolverTask> swap = null;

            while (currentBatch.Count > 0)
            {
                await ExecuteResolverBatchAsync(
                    executionContext, currentBatch, nextBatch,
                    batchOperationHandler, cancellationToken);

                swap = currentBatch;
                currentBatch = nextBatch;
                nextBatch = swap;
                nextBatch.Clear();

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private async Task ExecuteResolverBatchAsync(
            IExecutionContext executionContext,
            IReadOnlyCollection<ResolverTask> currentBatch,
            List<ResolverTask> nextBatch,
            BatchOperationHandler batchOperationHandler,
            CancellationToken cancellationToken)
        {
            // start field resolvers
            IReadOnlyCollection<Task> tasks = BeginExecuteResolverBatch(
                currentBatch,
                executionContext.ErrorHandler,
                cancellationToken);

            // execute batch data loaders
            await CompleteBatchOperationsAsync(
                tasks,
                batchOperationHandler,
                cancellationToken);

            // await field resolver results
            await EndExecuteResolverBatchAsync(
                executionContext,
                currentBatch,
                nextBatch.Add,
                cancellationToken);
        }

        private IReadOnlyCollection<Task> BeginExecuteResolverBatch(
            IEnumerable<ResolverTask> currentBatch,
            IErrorHandler errorHandler,
            CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();

            foreach (ResolverTask resolverTask in currentBatch)
            {
                resolverTask.Task = ExecuteResolverAsync(
                    resolverTask, errorHandler, cancellationToken);
                tasks.Add(resolverTask.Task);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return tasks;
        }

        protected Task CompleteBatchOperationsAsync(
            IReadOnlyCollection<Task> tasks,
            BatchOperationHandler batchOperationHandler,
            CancellationToken cancellationToken)
        {
            return (batchOperationHandler == null)
                ? Task.CompletedTask
                : batchOperationHandler.CompleteAsync(
                    tasks, cancellationToken);
        }

        private async Task EndExecuteResolverBatchAsync(
            IExecutionContext executionContext,
            IEnumerable<ResolverTask> currentBatch,
            Action<ResolverTask> enqueueTask,
            CancellationToken cancellationToken)
        {
            foreach (ResolverTask resolverTask in currentBatch)
            {
                // complete resolver tasks
                if (resolverTask.Task.IsCompleted)
                {
                    resolverTask.ResolverResult = resolverTask.Task.Result;
                }
                else
                {
                    resolverTask.ResolverResult = await resolverTask.Task;
                }

                // serialize and integrate result into final query result
                var completionContext = new FieldValueCompletionContext(
                    executionContext, resolverTask.ResolverContext,
                    resolverTask, enqueueTask);

                CompleteValue(completionContext);

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        protected IEnumerable<ResolverTask> CreateRootResolverTasks(
            IExecutionContext executionContext,
            OrderedDictionary result)
        {
            ImmutableStack<object> source = ImmutableStack<object>.Empty
                .Push(executionContext.RootValue);

            IReadOnlyCollection<FieldSelection> fieldSelections =
                executionContext.CollectFields(
                    executionContext.OperationType,
                    executionContext.Operation.SelectionSet);

            foreach (FieldSelection fieldSelection in fieldSelections)
            {
                yield return new ResolverTask(
                    executionContext,
                    executionContext.OperationType,
                    fieldSelection,
                    Path.New(fieldSelection.ResponseName),
                    source,
                    result);
            }
        }

        protected static BatchOperationHandler CreateBatchOperationHandler(
            IExecutionContext executionContext)
        {
            var batchOperations = executionContext.Services
                .GetService<IEnumerable<IBatchOperation>>();

            if (batchOperations.Any())
            {
                return new BatchOperationHandler(batchOperations);
            }
            return null;
        }
    }
}