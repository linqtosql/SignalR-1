// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Sockets;
using Microsoft.Extensions.Internal;
using System.Threading;
using System.Collections.Concurrent;

namespace Microsoft.AspNetCore.SignalR
{
    public class DefaultHubLifetimeManager<THub> : HubLifetimeManager<THub>
    {
        private readonly ConnectionList _connections = new ConnectionList();
        private readonly InvocationAdapterRegistry _registry;

        public DefaultHubLifetimeManager(InvocationAdapterRegistry registry)
        {
            _registry = registry;
        }

        public override Task AddGroupAsync(Connection connection, string groupName)
        {
            var groups = connection.Metadata.GetOrAdd("groups", _ => new HashSet<string>());

            lock (groups)
            {
                groups.Add(groupName);
            }

            return TaskCache.CompletedTask;
        }

        public override Task RemoveGroupAsync(Connection connection, string groupName)
        {
            var groups = connection.Metadata.Get<HashSet<string>>("groups");

            if (groups == null)
            {
                return TaskCache.CompletedTask;
            }

            lock (groups)
            {
                groups.Remove(groupName);
            }

            return TaskCache.CompletedTask;
        }

        public override Task InvokeAllAsync(string methodName, object[] args)
        {
            return InvokeAllWhere(methodName, args, c => true);
        }

        private Task InvokeAllWhere(string methodName, object[] args, Func<Connection, bool> include)
        {
            var tasks = new List<Task>(_connections.Count);
            var message = new InvocationDescriptor
            {
                Method = methodName,
                Arguments = args
            };

            // TODO: serialize once per format by providing a different stream?
            foreach (var connection in _connections)
            {
                if (!include(connection))
                {
                    continue;
                }

                var invocationAdapter = _registry.GetInvocationAdapter(connection.Metadata.Get<string>("formatType"));

                tasks.Add(WriteAsync(connection, invocationAdapter, message));
            }

            return Task.WhenAll(tasks);
        }

        public override async Task<object> InvokeConnectionAsync(string connectionId, string methodName, object[] args)
        {
            var connection = _connections[connectionId];
            var tcs = new TaskCompletionSource<object>();

            var invocationList = connection.Metadata.GetOrAdd("invocations", _ => new ConnectionInvocationList());

            string id;
            var task = invocationList.GetNextInvocationId(out id);

            var invocationAdapter = _registry.GetInvocationAdapter(connection.Metadata.Get<string>("formatType"));

            var message = new InvocationDescriptor
            {
                Id = id,
                Method = methodName,
                Arguments = args
            };

            await WriteAsync(connection, invocationAdapter, message);

            return await task;
        }

        public override Task InvokeGroupAsync(string groupName, string methodName, object[] args)
        {
            return InvokeAllWhere(methodName, args, connection =>
            {
                var groups = connection.Metadata.Get<HashSet<string>>("groups");
                return groups?.Contains(groupName) == true;
            });
        }

        public override Task InvokeUserAsync(string userId, string methodName, object[] args)
        {
            return InvokeAllWhere(methodName, args, connection =>
            {
                return string.Equals(connection.User.Identity.Name, userId, StringComparison.Ordinal);
            });
        }

        public override Task OnConnectedAsync(Connection connection)
        {
            _connections.Add(connection);
            return TaskCache.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Connection connection)
        {
            var invocationList = connection.Metadata.Get<ConnectionInvocationList>("invocations");

            invocationList?.CancelAll();

            _connections.Remove(connection);
            return TaskCache.CompletedTask;
        }

        private static async Task WriteAsync(Connection connection, IInvocationAdapter invocationAdapter, InvocationDescriptor invocation)
        {
            var stream = new MemoryStream();
            await invocationAdapter.WriteMessageAsync(invocation, stream);

            var buffer = ReadableBuffer.Create(stream.ToArray()).Preserve();
            var message = new Message(buffer, connection.Metadata.Format, endOfMessage: true);

            while (await connection.Transport.Output.WaitToWriteAsync())
            {
                if (connection.Transport.Output.TryWrite(message))
                {
                    break;
                }
            }
        }
    }

    public class ConnectionInvocationList
    {
        private int _id;
        private ConcurrentDictionary<string, TaskCompletionSource<object>> _invocations = new ConcurrentDictionary<string, TaskCompletionSource<object>>();

        public Task<object> GetNextInvocationId(out string id)
        {
            id = Interlocked.Increment(ref _id).ToString();
            var tcs = new TaskCompletionSource<object>();
            _invocations[id] = tcs;
            return tcs.Task;
        }

        public void Complete(InvocationResultDescriptor descriptor)
        {
            TaskCompletionSource<object> tcs;
            if (_invocations.TryRemove(descriptor.Id, out tcs))
            {
                if (descriptor.Error != null)
                {
                    tcs.TrySetResult(new Exception(descriptor.Error));
                }
                else
                {
                    tcs.TrySetResult(descriptor.Result);
                }
            }
        }

        public void CancelAll()
        {
            foreach (var invocation in _invocations)
            {
                invocation.Value.TrySetCanceled();
            }
        }
    }
}
